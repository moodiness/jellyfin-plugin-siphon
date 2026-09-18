using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Siphon.Calendar;

/// <summary>Observes published native metadata and release boundaries, without source polling.</summary>
public sealed class CalendarNotificationService(ConfigurationAccessor configuration, UserPreferenceStore preferences,
    SiphonStateStore state, FollowedCalendarService calendar, CalendarNotificationStore inbox,
    IUserManager users, ISessionManager sessions, ILogger<CalendarNotificationService> logger) : BackgroundService
{
    private int _offset;
    private readonly Dictionary<Guid, (long Revision, DateTimeOffset NextScanUtc)> _observed = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The hosted service can start before native libraries finish initialization.
        // No cursor is changed until the first successful, committed-state snapshot.
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await ObserveAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { logger.LogWarning(exception, "Siphon calendar notification observation failed; durable cursors were retained"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task ObserveAsync(CancellationToken ct)
    {
        var all = users.GetUsers().ToDictionary(user => user.Id);
        var allowed = configuration.Current.EnableCalendarNotifications;
        var enabled = allowed ? all.Keys.Where(id => preferences.Get(id).NotificationsEnabled).Order().ToArray() : [];
        var enabledIds = enabled.ToHashSet();
        foreach (var id in _observed.Keys.Where(id => !enabledIds.Contains(id)).ToArray()) _observed.Remove(id);
        await inbox.SynchronizeRecipientsAsync(all.Keys.ToHashSet(), enabledIds, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        if (enabled.Length == 0)
        {
            _offset = 0;
            return;
        }
        // Fair rotation bounds per-cycle work even on servers with many inactive users.
        var count = Math.Min(32, enabled.Length);
        for (var index = 0; index < count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var id = enabled[(_offset + index) % enabled.Length];
            if (!configuration.Current.EnableCalendarNotifications || !preferences.Get(id).NotificationsEnabled) continue;
            if (_observed.TryGetValue(id, out var observed) && observed.Revision == state.Revision
                && observed.NextScanUtc > DateTimeOffset.UtcNow) continue;
            if (!await configuration.SynchronizationGate.WaitAsync(0, ct).ConfigureAwait(false)) break;
            IReadOnlyList<CalendarNotification> generated;
            try
            {
                var managed = state.GetReadSnapshot();
                var revision = managed.Revision!.Value;
                var now = DateTimeOffset.UtcNow;
                var snapshot = await calendar.GetSnapshotAsync(all[id], now, ct, managed.Items).ConfigureAwait(false);
                if (snapshot.Truncated)
                {
                    logger.LogWarning("Siphon calendar observation exceeded its episode limit for user {UserId}; cursor not advanced", id);
                    continue;
                }
                var stillEnabled = configuration.Current.EnableCalendarNotifications && preferences.Get(id).NotificationsEnabled;
                generated = await inbox.ObserveAsync(id, snapshot, stillEnabled, now, ct).ConfigureAwait(false);
                // Unchanged idle calendars need no DB queries until a known release,
                // committed catalog revision, or periodic native favorite/permission refresh.
                var nextScan = now.AddMinutes(5);
                foreach (var episode in snapshot.Episodes)
                    if (episode.AnnouncedReleaseUtc > now && episode.AnnouncedReleaseUtc < nextScan)
                        nextScan = episode.AnnouncedReleaseUtc;
                _observed[id] = (revision, nextScan);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Siphon calendar observation failed for user {UserId}; cursor not advanced", id);
                continue;
            }
            finally { configuration.SynchronizationGate.Release(); }
            // Inbox and cursor are already durable. Transport failures must never create
            // duplicate inbox events; delivery is deliberately best effort and not retried.
            if (generated.Count > 0) await SendAsync(id, generated, ct).ConfigureAwait(false);
        }
        _offset = (_offset + count) % enabled.Length;
    }

    private async Task SendAsync(Guid userId, IReadOnlyList<CalendarNotification> notifications, CancellationToken ct)
    {
        if (!configuration.Current.EnableCalendarNotifications || !preferences.Get(userId).NotificationsEnabled) return;
        var recipient = users.GetUserById(userId);
        if (recipient is null) return;
        var accessible = calendar.FilterInbox(recipient, notifications, ct);
        if (accessible.Count == 0) return;
        var text = accessible.Count == 1
            ? accessible[0].Episode.SeriesName + ": " + accessible[0].Episode.Name + " — " + Label(accessible[0].Kind)
            : accessible.Count + " announced-release updates in your Siphon inbox.";
        foreach (var session in sessions.Sessions.Where(session => CanSend(session, userId)).Take(16).ToArray())
        {
            ct.ThrowIfCancellationRequested();
            // Re-check after enumeration; a device may have switched its primary user.
            if (!CanSend(session, userId)) continue;
            using var delivery = CancellationTokenSource.CreateLinkedTokenSource(ct);
            delivery.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await sessions.SendMessageCommand(null!, session.Id, new MessageCommand
                {
                    Header = "Siphon calendar",
                    Text = text + " Stream availability is not verified.",
                    TimeoutMs = 8000
                }, delivery.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception) { logger.LogDebug(exception, "Native Siphon calendar message was not delivered; it remains in the user's inbox"); }
        }
    }

    internal static bool CanSend(SessionInfo session, Guid userId)
        => session.UserId == userId && userId != Guid.Empty && session.IsActive && session.SupportsMediaControl
            && session.Capabilities?.SupportedCommands.Contains(GeneralCommandType.DisplayMessage) == true
            && !session.AdditionalUsers.Any(other => other.UserId != userId);

    private static string Label(string kind) => kind switch
    {
        "NewEpisode" => "new episode date announced.",
        "DateChanged" => "announced release date changed.",
        _ => "announced release date reached."
    };
}
