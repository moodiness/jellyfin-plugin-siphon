using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Calendar;

namespace Jellyfin.Plugin.Siphon.Notifications;

public sealed record NotificationWebhookStatus(bool Allowed, bool Enabled, string Url, bool HasSecret,
    DateTimeOffset? LastSuccessUtc, string? LastError, int PendingCount)
{
    public string Adapter { get; init; } = "Json";
    public string[] Kinds { get; init; } = NotificationWebhookSettings.DefaultKinds();
    public int DigestMinutes { get; init; }
    public DateTimeOffset? LastAttemptUtc { get; init; }
    public DateTimeOffset? NextAttemptUtc { get; init; }
    public int LastAttemptCount { get; init; }
    public string? LastOutcome { get; init; }
    public long AbandonedCount { get; init; }
}

public sealed record NotificationWebhookSettings(bool Enabled, string Url)
{
    public string Adapter { get; init; } = "Json";
    public string[] Kinds { get; init; } = DefaultKinds();
    public int DigestMinutes { get; init; }

    internal static string[] DefaultKinds() => ["NewEpisode", "DateChanged", "AnnouncedRelease"];

    internal NotificationWebhookSettings Normalize()
    {
        if (Adapter is not ("Json" or "Discord" or "Slack" or "Ntfy"))
            throw new ArgumentException("Choose Json, Discord, Slack or Ntfy as the webhook adapter.");
        if (Kinds is null || Kinds.Length > 3 || Kinds.Any(kind => kind is not ("NewEpisode" or "DateChanged" or "AnnouncedRelease")))
            throw new ArgumentException("Choose supported calendar notification kinds.");
        if (DigestMinutes is < 0 or > 1440)
            throw new ArgumentException("Use zero for immediate delivery or a digest interval from 1 to 1440 minutes.");
        return this with { Kinds = Kinds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() };
    }
}

public sealed record NotificationWebhookHealth(bool Enabled, int ConfiguredUsers, int Pending, int Retrying, long Abandoned);
public sealed record NotificationWebhookSecret(string Secret);
public sealed record NotificationWebhookTestResult(bool Success, string Message, string? Code = null);

internal sealed record WebhookUserState
{
    public bool Enabled { get; init; }
    public bool Active { get; init; }
    public string Url { get; init; } = "";
    public string Secret { get; init; } = "";
    public string Adapter { get; init; } = "Json";
    public string[] Kinds { get; init; } = NotificationWebhookSettings.DefaultKinds();
    public int DigestMinutes { get; init; }
    public Guid Generation { get; init; } = Guid.NewGuid();
    public DateTimeOffset SinceUtc { get; init; }
    public Guid[] Seen { get; init; } = [];
    public WebhookDelivery[] Pending { get; init; } = [];
    public DateTimeOffset? LastSuccessUtc { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? LastTestUtc { get; init; }
    public DateTimeOffset? LastAttemptUtc { get; init; }
    public int LastAttemptCount { get; init; }
    public string? LastOutcome { get; init; }
    public long AbandonedCount { get; init; }
}

internal sealed record WebhookDelivery(Guid EventId, DateTimeOffset CreatedUtc, int Attempts, DateTimeOffset NextAttemptUtc)
{
    public DateTimeOffset DueUtc { get; init; }
    public CalendarNotification[] Notifications { get; init; } = [];
    public string? Body { get; init; }

    internal static bool SameEvent(CalendarNotification left, CalendarNotification right)
        => (left with { ReadUtc = null }) == (right with { ReadUtc = null });
}

internal static class NotificationWebhookPayload
{
    internal static string Create(CalendarNotification notification) => JsonSerializer.Serialize(Event(notification));

    internal static object Event(CalendarNotification notification) => new
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
    };

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
