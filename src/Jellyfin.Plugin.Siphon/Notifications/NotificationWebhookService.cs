using System.Net;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Siphon.Calendar;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Siphon.Notifications;

/// <summary>Polls committed inbox entries; never changes the inbox or resolves media sources.</summary>
public sealed class NotificationWebhookService(NotificationWebhookStore store, CalendarNotificationStore inbox,
    FollowedCalendarService calendar, UserPreferenceStore preferences, ConfigurationAccessor configuration,
    IUserManager users, SafeHttpClient http, SsrfPolicy policy, ILogger<NotificationWebhookService> logger) : BackgroundService
{
    // Configuration changes and sends share a gate. Once an endpoint update returns, no old send is in flight.
    private readonly SemaphoreSlim _delivery = new(1, 1);
    private int _offset;
    private DateTimeOffset _testWindow;
    private int _testsInWindow;

    internal bool Allowed(Guid userId) => configuration.Current.EnableCalendarNotifications
        && configuration.Current.EnableNotificationWebhooks && preferences.Get(userId).NotificationsEnabled
        && AccountAllowed(users.GetUserById(userId));

    private static bool AccountAllowed(User? user) => user is not null && !user.HasPermission(PermissionKind.IsDisabled)
        && (user.HasPermission(PermissionKind.IsAdministrator) || user.IsParentalScheduleAllowed());

    internal NotificationWebhookStatus GetStatus(Guid userId)
    {
        var value = store.Get(userId);
        return new(Allowed(userId), value.Enabled, value.Url, value.Secret.Length > 0,
            value.LastSuccessUtc, value.LastError, value.Pending.Length);
    }

    internal async Task ConfigureAsync(Guid userId, NotificationWebhookSettings settings, CancellationToken ct)
    {
        if (settings.Url is null) throw new ArgumentException("A webhook destination is required.");
        var url = settings.Url.Trim();
        if (url.Length > 0 && (settings.Enabled || url != store.Get(userId).Url)) url = ValidateEndpoint(url).AbsoluteUri;
        if (url.Length == 0 && settings.Enabled) throw new ArgumentException("A webhook destination is required.");
        await _delivery.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (settings.Enabled && !Allowed(userId)) throw new InvalidOperationException("Enable calendar notifications and webhook delivery first.");
            await store.ConfigureAsync(userId, settings.Enabled, url, Allowed(userId), inbox.Get(userId),
                DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        }
        finally { _delivery.Release(); }
    }

    internal async Task<string> RotateAsync(Guid userId, CancellationToken ct)
    {
        await _delivery.WaitAsync(ct).ConfigureAwait(false);
        try { return await store.RotateAsync(userId, Allowed(userId), inbox.Get(userId), DateTimeOffset.UtcNow, ct).ConfigureAwait(false); }
        finally { _delivery.Release(); }
    }

    internal async Task<NotificationWebhookTestResult> TestAsync(Guid userId, CancellationToken ct)
    {
        await _delivery.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var value = store.Get(userId);
            if (!Allowed(userId) || !value.Enabled || value.Secret.Length == 0)
                throw new InvalidOperationException("Enable all notification options and rotate a signing secret before testing.");
            var endpoint = ValidateEndpoint(value.Url);
            var now = DateTimeOffset.UtcNow;
            if (now - _testWindow >= TimeSpan.FromMinutes(1)) { _testWindow = now; _testsInWindow = 0; }
            if (_testsInWindow >= 8 || !await store.ReserveTestAsync(userId, now, ct).ConfigureAwait(false))
                throw new WebhookTestRateLimitException();
            _testsInWindow++;
            if (!Allowed(userId)) throw new InvalidOperationException("Webhook delivery is no longer allowed.");
            var id = Guid.NewGuid();
            var result = await PostAsync(endpoint, NotificationWebhookPayload.CreateTest(id, now), value.Secret, id, ct).ConfigureAwait(false);
            await store.RecordTestAsync(userId, value.Generation, result.Success, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            return new(result.Success, result.Success ? "Siphon webhook test delivered." : "Webhook test failed. Check the destination and its access policy.");
        }
        finally { _delivery.Release(); }
    }

    internal Uri ValidateEndpoint(string url)
    {
        if (url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Fragment.Length > 0)
            throw new ArgumentException("Use a valid webhook URL without a fragment, at most 2048 characters.");
        try { policy.ValidateUri(uri); }
        catch (HttpRequestException) { throw new ArgumentException("Use an HTTP(S) webhook URL without embedded credentials."); }
        if (uri.Scheme != Uri.UriSchemeHttps && !policy.AllowsPrivateHost(uri.IdnHost))
            throw new ArgumentException("Webhook destinations require HTTPS unless the administrator explicitly allows the exact hostname.");
        if (IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var address) && !policy.IsAllowed(uri.IdnHost, address))
            throw new ArgumentException("The webhook destination is not permitted by the server network policy.");
        return uri;
    }

    private bool EndpointAllowed(string url)
    {
        try { ValidateEndpoint(url); return true; }
        catch (ArgumentException) { return false; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                using var cycle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cycle.CancelAfter(TimeSpan.FromSeconds(30));
                try { await DeliverCycleAsync(cycle.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (OperationCanceledException) when (cycle.IsCancellationRequested) { }
                // Never include exceptions: transport and persistence errors may contain credential-bearing URLs.
                catch (Exception) { logger.LogWarning("Siphon webhook cycle failed; durable delivery state was retained."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task DeliverCycleAsync(CancellationToken ct)
    {
        var ids = store.GetUsers();
        if (ids.Length == 0) return;
        var remaining = 4;
        var count = Math.Min(32, ids.Length);
        for (var index = 0; index < count; index++)
        {
            // Advance even when this user's network timeout exhausts the cycle deadline.
            var id = ids[_offset % ids.Length];
            _offset = (_offset + 1) % ids.Length;
            ct.ThrowIfCancellationRequested();
            await _delivery.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!await configuration.SynchronizationGate.WaitAsync(0, ct).ConfigureAwait(false)) continue;
                try
                {
                    var current = inbox.Get(id);
                    var user = users.GetUserById(id);
                    var value = store.Get(id);
                    var allowed = Allowed(id) && EndpointAllowed(value.Url);
                    var accessible = allowed && user is not null ? calendar.FilterInbox(user, current, ct) : [];
                    await store.ObserveAsync(id, allowed, current, accessible.Select(entry => entry.Id).ToHashSet(),
                        DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                }
                finally { configuration.SynchronizationGate.Release(); }
                if (remaining == 0) continue;
                var state = store.Get(id);
                if (!state.Active) continue;
                var pending = state.Pending.Where(entry => entry.NextAttemptUtc <= DateTimeOffset.UtcNow)
                    .OrderBy(entry => entry.NextAttemptUtc).ThenBy(entry => entry.EventId).FirstOrDefault();
                if (pending is null) continue;
                remaining--;
                await DeliverAsync(id, state, pending, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { logger.LogWarning("Siphon webhook recipient processing failed; no diagnostic credentials were retained."); }
            finally { _delivery.Release(); }
        }
    }

    private async Task DeliverAsync(Guid userId, WebhookUserState state, WebhookDelivery pending, CancellationToken ct)
    {
        // Read permissions and the committed entry afresh for every attempt, including retries.
        var user = users.GetUserById(userId);
        var current = inbox.Get(userId);
        if (!Allowed(userId) || user is null || !EndpointAllowed(state.Url))
        {
            await store.ObserveAsync(userId, false, current, new HashSet<Guid>(), DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            return;
        }
        var notification = calendar.FilterInbox(user, current.Where(entry => entry.Id == pending.EventId).ToArray(), ct).SingleOrDefault();
        if (notification is null)
        {
            await store.CompleteAsync(userId, state.Generation, pending.EventId, false, false,
                "Delivery revoked because the notification is no longer accessible.", DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            return;
        }
        await store.BeginAttemptAsync(userId, state.Generation, pending.EventId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        // No network activity until the attempt is durable, so crashes also consume the bounded attempt budget.
        user = users.GetUserById(userId);
        if (!Allowed(userId) || user is null || calendar.FilterInbox(user, [notification], ct).Count == 0)
        {
            await store.ObserveAsync(userId, false, current, new HashSet<Guid>(), DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            return;
        }
        var result = await PostAsync(ValidateEndpoint(state.Url), NotificationWebhookPayload.Create(notification),
            state.Secret, pending.EventId, ct).ConfigureAwait(false);
        await store.CompleteAsync(userId, state.Generation, pending.EventId, result.Success, result.Retry,
            result.Success ? null : "Webhook delivery failed. Check the destination and its access policy.",
            DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
    }

    private async Task<(bool Success, bool Retry)> PostAsync(Uri endpoint, string json, string secret, Guid eventId, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var headers = new Dictionary<string, string>
            {
                ["X-Siphon-Event-Id"] = eventId.ToString("D"),
                ["X-Siphon-Signature"] = NotificationWebhookPayload.Sign(json, secret)
            };
            using var response = await http.PostJsonAsync(endpoint, json, headers, deadline.Token).ConfigureAwait(false);
            // Headers are sufficient; never buffer, parse, log or reflect an untrusted response body.
            return (response.IsSuccessStatusCode, (int)response.StatusCode >= 500
                || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ArgumentException) { return (false, false); }
        catch (Exception) { return (false, true); }
    }

    public override void Dispose() { base.Dispose(); _delivery.Dispose(); }
}

internal sealed class WebhookTestRateLimitException : Exception { }
