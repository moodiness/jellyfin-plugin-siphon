using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Siphon.Playback;

/// <summary>Handles native remote downloads after native authentication, before physical-file dispatch.</summary>
public sealed class NativeDownloadFilter(ILibraryManager library, PlaybackAccess access, NativeVersionService versions,
    ISiphonStateStore state, PlaybackDownloadService downloads) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor { ControllerName: "Library", ActionName: "GetDownload" or "GetFile" }
            || !context.ActionArguments.TryGetValue("itemId", out var value) || value is not Guid id
            || library.GetItemById(id) is not Video video || !NativeVersionService.IsManaged(video))
        { await next().ConfigureAwait(false); return; }
        var user = access.CurrentUser();
        if (user is null || access.GetVideo(id, user.Id, download: true) is null
            || video.GetProviderId("Siphon") is not { } key || state.FindByKey(key) is not { } managed)
        { context.Result = new NotFoundResult(); return; }
        var requested = context.HttpContext.Request.Query["mediaSourceId"].ToString();
        var selectedId = requested.Length == 0 ? id : Guid.TryParse(requested, out var parsed) ? parsed : Guid.Empty;
        if (selectedId == Guid.Empty || (selectedId != id && (access.GetVideo(selectedId, user.Id, true) is not { } selected || selected.GetProviderId("Siphon") != key)))
        { context.Result = new NotFoundResult(); return; }
        var available = await versions.GetVersionsAsync(video, user.Id, context.HttpContext.RequestAborted).ConfigureAwait(false);
        var version = available.FirstOrDefault(candidate => candidate.Item.Id == selectedId)
            ?? (requested.Length == 0 && !video.PrimaryVersionId.HasValue ? available.FirstOrDefault() : null);
        if (version is null) { context.Result = new NotFoundResult(); return; }
        await downloads.RelayAsync(context.HttpContext, version.Stream, context.HttpContext.RequestAborted).ConfigureAwait(false);
        context.Result = new EmptyResult();
    }
}
