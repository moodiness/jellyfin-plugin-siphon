using System.Globalization;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Siphon.Playback;

/// <summary>Loads stream versions only after Jellyfin authorizes a single item's detail response.</summary>
public sealed class NativeVersionResultFilter(
    NativeVersionService versions,
    ILibraryManager library,
    IMediaSourceManager sources,
    IAuthorizationContext authorization,
    IUserManager users) : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { Value: BaseItemDto { MediaSources: not null } dto } result
            && (result.StatusCode is null or >= 200 and < 300)
            && library.GetItemById(dto.Id) is Video video && NativeVersionService.IsManaged(video))
        {
            var user = await GetEffectiveUser(context).ConfigureAwait(false);
            if (user is not null && library.GetItemById<Video>(video.Id, user) is not null)
            {
                var available = await versions.GetVersionsAsync(video, context.HttpContext.RequestAborted).ConfigureAwait(false);
                // Materialization registers refreshed native instances; the controller's original
                // item may still have no source markers on this very first request.
                video = library.GetItemById<Video>(video.Id, user) ?? video;
                dto.MediaSources = available.Count == 0 ? [] : sources.GetStaticMediaSources(video, true, user).ToArray();
                dto.MediaSourceCount = dto.MediaSources.Length;
                if (dto.Path is not null) dto.Path = video.Path;
            }
        }
        else if (context.Result is ObjectResult listResult && (listResult.StatusCode is null or >= 200 and < 300))
        {
            IEnumerable<BaseItemDto> items = listResult.Value switch
            {
                QueryResult<BaseItemDto> query => query.Items,
                IEnumerable<BaseItemDto> list => list,
                _ => []
            };
            User? user = null;
            Dictionary<Guid, int>? counts = null;
            foreach (var item in items)
            {
                if (item.MediaSourceCount is not > 1 || library.GetItemById(item.Id) is not Video entryVideo
                    || !NativeVersionService.IsManaged(entryVideo)
                    || entryVideo.GetProviderId(NativeVersionService.SourceProvider) is null) continue;
                user ??= await GetEffectiveUser(context).ConfigureAwait(false);
                if (user is null) continue;
                counts ??= [];
                var primaryId = entryVideo.PrimaryVersionId ?? entryVideo.Id;
                if (counts.TryGetValue(primaryId, out var count))
                {
                    item.MediaSourceCount = count;
                    continue;
                }
                // Retired versions keep their history, but must not inflate the native card badge.
                // This reads only saved native state, never resolving streams for a whole row.
                item.MediaSourceCount = entryVideo.GetAllVersions().Count(version =>
                    version.GetProviderId("Siphon") == entryVideo.GetProviderId("Siphon")
                    && int.TryParse(version.GetProviderId(NativeVersionService.SourceOrderProvider), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var rank) && rank >= 0
                    && library.GetItemById<Video>(version.Id, user) is not null);
                counts.Add(primaryId, item.MediaSourceCount.Value);
            }
        }
        await next().ConfigureAwait(false);
    }

    private async Task<User?> GetEffectiveUser(ResultExecutingContext context)
    {
        // The controller already validated impersonation; retain its effective user for visibility.
        var request = context.HttpContext.Request;
        var requestedUser = request.RouteValues["userId"]?.ToString() ?? request.Query["userId"].ToString();
        return Guid.TryParse(requestedUser, out var userId) && userId != Guid.Empty
            ? users.GetUserById(userId)
            : (await authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false)).User;
    }
}
