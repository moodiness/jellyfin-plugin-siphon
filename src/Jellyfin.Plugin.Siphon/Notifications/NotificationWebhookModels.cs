using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Calendar;

namespace Jellyfin.Plugin.Siphon.Notifications;

public sealed record NotificationWebhookStatus(bool Allowed, bool Enabled, string Url, bool HasSecret,
    DateTimeOffset? LastSuccessUtc, string? LastError, int PendingCount);
public sealed record NotificationWebhookSettings(bool Enabled, string Url);
public sealed record NotificationWebhookSecret(string Secret);
public sealed record NotificationWebhookTestResult(bool Success, string Message);

internal sealed record WebhookUserState
{
    public bool Enabled { get; init; }
    public bool Active { get; init; }
    public string Url { get; init; } = "";
    public string Secret { get; init; } = "";
    public Guid Generation { get; init; } = Guid.NewGuid();
    public DateTimeOffset SinceUtc { get; init; }
    public Guid[] Seen { get; init; } = [];
    public WebhookDelivery[] Pending { get; init; } = [];
    public DateTimeOffset? LastSuccessUtc { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? LastTestUtc { get; init; }
}

internal sealed record WebhookDelivery(Guid EventId, DateTimeOffset CreatedUtc, int Attempts, DateTimeOffset NextAttemptUtc);

internal static class NotificationWebhookPayload
{
    internal static string Create(CalendarNotification notification) => JsonSerializer.Serialize(new
    {
        Version = 1,
        Type = "calendar.notification",
        EventId = notification.Id,
        notification.CreatedUtc,
        Test = false,
        notification.Kind,
        notification.Episode,
        notification.PreviousAnnouncedReleaseUtc,
        AvailabilityNote = FollowedCalendarService.AvailabilityNote
    });

    internal static string CreateTest(Guid id, DateTimeOffset now) => JsonSerializer.Serialize(new
    {
        Version = 1,
        Type = "webhook.test",
        EventId = id,
        CreatedUtc = now,
        Test = true,
        Message = "Siphon webhook test. This is not a calendar notification."
    });

    internal static string Sign(string json, string secret)
    {
        var key = Convert.FromBase64String(secret);
        try { return "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(json))); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
