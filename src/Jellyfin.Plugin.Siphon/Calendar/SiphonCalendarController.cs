using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Siphon.Calendar;

[ApiController]
[Authorize]
[Route("Siphon")]
public sealed class SiphonCalendarController(FollowedCalendarService calendar, CalendarNotificationStore inbox,
    UserPreferenceStore preferences, ConfigurationAccessor configuration, IAuthorizationContext authorization) : ControllerBase
{
    [HttpGet("Calendar")]
    public async Task<ActionResult<CalendarResponse>> GetCalendar([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
    {
        var user = (await authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).User;
        if (user is null) return Unauthorized();
        var now = DateTimeOffset.UtcNow;
        if (!CalendarWindow.TryCreate(from, to, now, out var window))
            return BadRequest(new { Message = "Use inclusive UTC dates yyyy-MM-dd, from <= to, with at most 90 days." });
        if (!await configuration.SynchronizationGate.WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
            return StatusCode(503, new { Message = "The managed library is updating. Retry the calendar shortly." });
        try
        {
            var snapshot = await calendar.GetSnapshotAsync(user, now, ct, window: window).ConfigureAwait(false);
            var episodes = snapshot.Episodes
                .OrderBy(episode => episode.AnnouncedReleaseUtc).ThenBy(episode => episode.SeriesName, StringComparer.Ordinal)
                .ThenBy(episode => episode.SeasonNumber).ThenBy(episode => episode.EpisodeNumber).ThenBy(episode => episode.Id)
                .Take(2001).ToArray();
            return new CalendarResponse(CalendarWindow.Format(window.From), CalendarWindow.Format(window.To), "UTC",
                episodes.Take(2000).ToArray(), snapshot.Truncated || episodes.Length > 2000, FollowedCalendarService.AvailabilityNote);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return StatusCode(503, new { Message = "The native followed-series calendar is temporarily unavailable." }); }
        finally { configuration.SynchronizationGate.Release(); }
    }

    [HttpGet("Notifications")]
    public async Task<ActionResult<CalendarInbox>> GetNotifications(CancellationToken ct)
    {
        var user = (await authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).User;
        if (user is null) return Unauthorized();
        // Revalidate native access on every read, including already persisted titles.
        var entries = calendar.FilterInbox(user, inbox.Get(user.Id), ct);
        return new CalendarInbox(entries, entries.Count(entry => entry.ReadUtc is null),
            configuration.Current.EnableCalendarNotifications, preferences.Get(user.Id).NotificationsEnabled);
    }

    [HttpPost("Notifications/{id:guid}/Read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        var user = (await authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).User;
        if (user is null) return Unauthorized();
        if (!calendar.FilterInbox(user, inbox.Get(user.Id), ct).Any(entry => entry.Id == id)) return NotFound();
        return await inbox.MarkReadAsync(user.Id, id, DateTimeOffset.UtcNow, ct).ConfigureAwait(false) ? NoContent() : NotFound();
    }
}
