using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Siphon.Playback;

/// <summary>Exposes only the effective user's versions after native item authorization.</summary>
public sealed class NativeVersionResultFilter(NativeVersionService versions, ILibraryManager library,
    IMediaSourceManager sources, PlaybackAccess access) : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is not ObjectResult result || result.StatusCode is not (null or >= 200 and < 300))
        { await next().ConfigureAwait(false); return; }
        var user = access.CurrentUser();
        if (result.Value is BaseItemDto dto && library.GetItemById(dto.Id) is Video video && NativeVersionService.IsManaged(video))
        {
            if (user is null || access.GetVideo(video.Id, user.Id) is null) context.Result = new NotFoundResult();
            else
            {
                if (dto.MediaSources is not null)
                {
                    var available = await versions.GetVersionsAsync(video, user.Id, context.HttpContext.RequestAborted).ConfigureAwait(false);
                    video = library.GetItemById<Video>(video.Id, user) ?? video;
                    dto.MediaSources = available.Count == 0 ? [] : sources.GetStaticMediaSources(video, true, user).ToArray();
                    dto.MediaSourceCount = dto.MediaSources.Length;
                }
                dto.CanDownload = PlaybackAccess.CanDownload(video, user);
                if (dto.Path is not null) dto.Path = video.Path;
                HideInternalMarkers(dto);
            }
        }
        else
        {
            IEnumerable<BaseItemDto> items = result.Value switch
            {
                QueryResult<BaseItemDto> query => query.Items,
                IEnumerable<BaseItemDto> list => list,
                _ => []
            };
            var retained = new List<BaseItemDto>();
            var removed = 0;
            var counts = new Dictionary<Guid, int>();
            foreach (var item in items)
            {
                if (library.GetItemById(item.Id) is Video entry && NativeVersionService.IsManaged(entry))
                {
                    if ((entry.PrimaryVersionId.HasValue || !string.IsNullOrEmpty(entry.GetProviderId(NativeVersionService.OwnerProvider)))
                        && (!Guid.TryParse(entry.GetProviderId(NativeVersionService.OwnerProvider), out var owner) || owner != user?.Id))
                    { removed++; continue; }
                    if (user is not null)
                    {
                        var primaryId = entry.PrimaryVersionId ?? entry.Id;
                        if (!counts.TryGetValue(primaryId, out var count))
                        {
                            count = entry.GetAllVersions().Count(version => version.GetProviderId("Siphon") == entry.GetProviderId("Siphon")
                                && version.GetProviderId(NativeVersionService.OwnerProvider) == user.Id.ToString("N")
                                && int.TryParse(version.GetProviderId(NativeVersionService.SourceOrderProvider), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rank) && rank >= 0);
                            counts[primaryId] = count;
                        }
                        item.MediaSourceCount = count;
                        item.CanDownload = PlaybackAccess.CanDownload(entry, user);
                    }
                    HideInternalMarkers(item);
                }
                retained.Add(item);
            }
            if (removed > 0)
            {
                if (result.Value is QueryResult<BaseItemDto> query)
                {
                    query.Items = retained.ToArray();
                    query.TotalRecordCount = Math.Max(0, query.TotalRecordCount - removed);
                }
                else result.Value = retained.ToArray();
            }
        }
        await next().ConfigureAwait(false);
    }

    private static void HideInternalMarkers(BaseItemDto dto)
    {
        if (dto.ProviderIds is null) return;
        dto.ProviderIds = new Dictionary<string, string>(dto.ProviderIds, StringComparer.OrdinalIgnoreCase);
        dto.ProviderIds.Remove(NativeVersionService.SourceProvider);
        dto.ProviderIds.Remove(NativeVersionService.SourceNameProvider);
        dto.ProviderIds.Remove(NativeVersionService.SourceOrderProvider);
        dto.ProviderIds.Remove(NativeVersionService.OwnerProvider);
    }
}
