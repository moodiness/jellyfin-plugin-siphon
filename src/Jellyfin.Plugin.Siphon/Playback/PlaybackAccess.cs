using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Siphon.Playback;

/// <summary>Resolves native request identity before any addon or media I/O.</summary>
public sealed class PlaybackAccess(IHttpContextAccessor contexts, IAuthorizationContext authorization, IUserManager users, ILibraryManager library)
{
    public User? CurrentUser()
    {
        var context = contexts.HttpContext;
        if (context is null) return null;
        var authenticated = authorization.GetAuthorizationInfo(context).GetAwaiter().GetResult().User;
        if (authenticated is null) return null;
        var requested = context.Request.RouteValues["userId"]?.ToString() ?? context.Request.Query["userId"].ToString();
        if (!Guid.TryParse(requested, out var userId) || userId == Guid.Empty || userId == authenticated.Id) return authenticated;
        return context.User.IsInRole("Administrator") ? users.GetUserById(userId) : null;
    }

    public Video? GetVideo(Guid id, Guid userId, bool download = false)
    {
        var user = users.GetUserById(userId);
        if (user is null || user.HasPermission(PermissionKind.IsDisabled)
            || (!user.HasPermission(PermissionKind.IsAdministrator) && !user.IsParentalScheduleAllowed())
            || library.GetItemById<Video>(id, user) is not { } video) return null;
        var query = new InternalItemsQuery(user) { Recursive = true, IncludeAlternateVersions = true, GroupByPresentationUniqueKey = false };
        library.ConfigureUserAccess(query, user);
        query.ItemIds = [id];
        if (!library.GetItemList(query).Any(item => item.Id == id)) return null;
        if (video.PrimaryVersionId.HasValue || !string.IsNullOrEmpty(video.GetProviderId(NativeVersionService.OwnerProvider)))
        {
            if (!Guid.TryParse(video.GetProviderId(NativeVersionService.OwnerProvider), out var owner) || owner != userId) return null;
        }
        return download && !CanDownload(video, user) ? null : video;
    }

    public static bool CanDownload(Video video, User user)
        => video.CanDownload(user) || (NativeVersionService.IsManaged(video)
            && video.VideoType == VideoType.VideoFile && video.IsAuthorizedToDownload(user));

    public Video? FindVideo(string itemKey, Guid userId, string? sourceId = null, bool download = false)
    {
        var user = users.GetUserById(userId);
        if (user is null) return null;
        return library.GetItemList(new InternalItemsQuery(user)
        {
            HasAnyProviderId = new Dictionary<string, string> { ["Siphon"] = itemKey },
            IncludeAlternateVersions = true,
            GroupByPresentationUniqueKey = false,
            EnableTotalRecordCount = false
        }).OfType<Video>().FirstOrDefault(video => NativeVersionService.IsManaged(video)
            && video.GetProviderId("Siphon") == itemKey
            && (sourceId is null ? !video.PrimaryVersionId.HasValue : video.GetProviderId(NativeVersionService.SourceProvider) == sourceId)
            && GetVideo(video.Id, userId, download) is not null);
    }

    public Guid SourceOwner(string itemKey, string sourceId)
        => library.GetItemList(new InternalItemsQuery
        {
            HasAnyProviderId = new Dictionary<string, string> { ["Siphon"] = itemKey },
            IncludeAlternateVersions = true,
            GroupByPresentationUniqueKey = false,
            EnableTotalRecordCount = false
        }).OfType<Video>().Where(video => video.GetProviderId("Siphon") == itemKey
            && video.GetProviderId(NativeVersionService.SourceProvider) == sourceId)
            .Select(video => Guid.TryParse(video.GetProviderId(NativeVersionService.OwnerProvider), out var owner) ? owner : Guid.Empty)
            .FirstOrDefault();
    public bool CanPlay(Guid userId) => users.GetUserById(userId)?.HasPermission(PermissionKind.EnableMediaPlayback) == true;


    public bool AllowsLease(ProxySession session, bool download = false)
    {
        var current = CurrentUser();
        // Anonymous native ffmpeg readers hold an unguessable capability created after authorization.
        // An authenticated user cannot replay a different user's capability.
        if (contexts.HttpContext?.User.Identity?.IsAuthenticated == true && current is null) return false;
        return session.Source.UserId != Guid.Empty && (download || CanPlay(session.Source.UserId))
            && (current is null || current.Id == session.Source.UserId)
            && FindVideo(session.ItemKey, session.Source.UserId, session.Source.Id, download) is not null;
    }
}
