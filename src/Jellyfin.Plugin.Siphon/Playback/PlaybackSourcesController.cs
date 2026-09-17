using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Siphon.Playback;

public sealed record SourceSummary(string MediaSourceId, string Name, string Kind, long? Size, bool CanDownload, string? DownloadUnavailableReason, bool CanQueue);
public sealed record SourcesResponse(Guid ItemId, IReadOnlyList<SourceSummary> Sources, IReadOnlyList<AddonSourceDiagnostic> Addons, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, bool CanDownload);
public sealed record DownloadTicketRequest(string MediaSourceId);
public sealed record DownloadTicketResponse(string Url, DateTimeOffset ExpiresAtUtc);

[ApiController]
[Authorize]
[Route("Siphon/Items/{itemId:guid}")]
public sealed class PlaybackSourcesController(PlaybackAccess access, ISiphonStateStore state, StreamResolver resolver,
    NativeVersionService versions, ProxySessionStore sessions, PlaybackDownloadService downloads, ConfigurationAccessor configuration) : ControllerBase
{
    [HttpGet("Sources")]
    public Task<ActionResult<SourcesResponse>> Sources(Guid itemId) => Inspect(itemId, false);
    [HttpPost("Sources/Refresh")]
    public Task<ActionResult<SourcesResponse>> Refresh(Guid itemId) => Inspect(itemId, true);
    private async Task<ActionResult<SourcesResponse>> Inspect(Guid itemId, bool refresh)
    {
        var user = access.CurrentUser();
        if (user is null) return Unauthorized();
        if (access.GetVideo(itemId, user.Id) is not { } video || video.GetProviderId("Siphon") is not { } key
            || state.FindByKey(key) is not { } managed) return NotFound();
        if (refresh)
        {
            resolver.Invalidate(key, user.Id);
            sessions.InvalidateDefaults(key, user.Id);
        }
        try
        {
            var available = await versions.GetVersionsAsync(video, user.Id, HttpContext.RequestAborted).ConfigureAwait(false);
            var resolution = await resolver.GetResolutionAsync(managed, user.Id, HttpContext.RequestAborted).ConfigureAwait(false);
            var canDownload = PlaybackAccess.CanDownload(video, user);
            var canQueue = canDownload && configuration.Current.EnableDownloadQueue;
            return new SourcesResponse(video.PrimaryVersionId ?? video.Id,
                available.Select(version => new SourceSummary(version.Item.Id.ToString("N"), version.Stream.Name, version.Stream.P2p is null ? "Http" : "P2p", version.Stream.Size,
                    canDownload && PlaybackDownloadService.DownloadUnavailableReason(version.Stream) is null,
                    PlaybackDownloadService.DownloadUnavailableReason(version.Stream), canQueue)).ToArray(),
                resolution.Addons, resolution.CreatedAt, resolution.ExpiresAt, canDownload);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch { return StatusCode(502, new { Message = "Sources are temporarily unavailable." }); }
    }

    [HttpGet("Download")]
    [HttpHead("Download")]
    public async Task<IActionResult> Download(Guid itemId, [FromQuery] string? mediaSourceId)
    {
        var selected = await Select(itemId, mediaSourceId).ConfigureAwait(false);
        if (selected is null) return NotFound();
        await downloads.RelayAsync(HttpContext, selected.Source, HttpContext.RequestAborted).ConfigureAwait(false);
        return new EmptyResult();
    }

    [HttpPost("DownloadTicket")]
    public async Task<ActionResult<DownloadTicketResponse>> Ticket(Guid itemId, [FromBody] DownloadTicketRequest request)
    {
        var selected = await Select(itemId, request.MediaSourceId).ConfigureAwait(false);
        if (selected is null) return NotFound();
        if (PlaybackDownloadService.DownloadUnavailableReason(selected.Source) is { } reason)
            return StatusCode(415, new { Code = "HlsRequiresOfflinePreparation", Message = reason });
        return new DownloadTicketResponse(Request.PathBase.Value + "/Siphon/download/" + selected.Token, selected.DownloadExpiresAtUtc!.Value);
    }

    private async Task<ProxySession?> Select(Guid itemId, string? mediaSourceId)
    {
        var user = access.CurrentUser();
        if (user is null || access.GetVideo(itemId, user.Id, download: true) is not { } video
            || video.GetProviderId("Siphon") is not { } key || state.FindByKey(key) is not { } managed) return null;
        var selectedId = string.IsNullOrEmpty(mediaSourceId) ? video.Id : Guid.TryParse(mediaSourceId, out var parsed) ? parsed : Guid.Empty;
        if (selectedId == Guid.Empty) return null;
        // Reject a foreign explicit source before resolving any provider.
        if (selectedId != video.Id && (access.GetVideo(selectedId, user.Id, true) is not { } selected || selected.GetProviderId("Siphon") != key)) return null;
        var available = await versions.GetVersionsAsync(video, user.Id, HttpContext.RequestAborted).ConfigureAwait(false);
        var source = available.FirstOrDefault(version => version.Item.Id == selectedId)
            ?? (string.IsNullOrEmpty(mediaSourceId) && !video.PrimaryVersionId.HasValue ? available.FirstOrDefault() : null);
        return source is null ? null : sessions.Create(managed, source.Stream, download: true);
    }
}
