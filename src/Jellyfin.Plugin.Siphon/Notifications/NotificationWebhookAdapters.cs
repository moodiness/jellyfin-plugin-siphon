using System.Globalization;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Calendar;

namespace Jellyfin.Plugin.Siphon.Notifications;

/// <summary>Receiver-specific JSON only; all requests still use the checked, bounded POST transport.</summary>
internal static class NotificationWebhookAdapters
{
    internal const int MaximumBodyBytes = 32 * 1024;
    internal const int MaximumDigestEvents = 20;

    internal static Uri Endpoint(string adapter, Uri configured)
    {
        if (adapter == "Ntfy")
        {
            _ = Topic(configured);
            return new UriBuilder(configured) { Path = configured.AbsolutePath[..(configured.AbsolutePath.LastIndexOf('/') + 1)] }.Uri;
        }
        if (adapter == "Discord")
        {
            // wait=true asks Discord to acknowledge the created message rather than only accepting the request.
            var query = configured.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !Uri.UnescapeDataString(part.Split('=', 2)[0]).Equals("wait", StringComparison.OrdinalIgnoreCase));
            return new UriBuilder(configured) { Query = string.Join('&', query.Append("wait=true")) }.Uri;
        }
        return configured;
    }

    internal static string Create(WebhookUserState settings, WebhookDelivery delivery)
    {
        var notifications = delivery.Notifications;
        if (settings.Adapter == "Json")
        {
            if (settings.DigestMinutes == 0) return NotificationWebhookPayload.Create(notifications[0]);
            return JsonSerializer.Serialize(new
            {
                Version = 1,
                Type = "calendar.digest",
                EventId = delivery.EventId,
                delivery.CreatedUtc,
                Test = false,
                Notifications = notifications.Select(NotificationWebhookPayload.Event).ToArray(),
                AvailabilityNote = FollowedCalendarService.AvailabilityNote
            });
        }
        var heading = notifications.Length == 1 ? "Siphon calendar notification"
            : "Siphon calendar digest (" + notifications.Length.ToString(CultureInfo.InvariantCulture) + " events)";
        var limit = settings.Adapter switch { "Ntfy" => 3500, "Slack" => 2800, _ => 1900 };
        var perLine = (limit - Encoding.UTF8.GetByteCount(heading + FollowedCalendarService.AvailabilityNote) - 4) / notifications.Length - 1;
        var text = new StringBuilder(heading).Append('\n');
        foreach (var notification in notifications)
        {
            var label = notification.Kind switch
            {
                "NewEpisode" => "New episode",
                "DateChanged" => "Date changed",
                "AnnouncedRelease" => "Announced release",
                _ => throw new ArgumentException("Unsupported notification kind.")
            };
            var date = notification.Episode.AnnouncedReleaseUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            text.Append(Clip(label + " " + date + ": " + Plain(notification.Episode.SeriesName) + " / " + Plain(notification.Episode.Name), perLine)).Append('\n');
        }
        text.Append(FollowedCalendarService.AvailabilityNote);
        return Vendor(settings.Adapter, settings.Url, heading, text.ToString());
    }

    internal static string CreateTest(WebhookUserState settings, Guid eventId, DateTimeOffset now)
        => settings.Adapter == "Json" ? NotificationWebhookPayload.CreateTest(eventId, now)
            : Vendor(settings.Adapter, settings.Url, "Siphon webhook test",
                "Siphon webhook test. This is not a calendar notification. Event: " + eventId.ToString("D"));

    private static string Vendor(string adapter, string url, string title, string message) => adapter switch
    {
        // https://docs.discord.com/developers/resources/webhook#execute-webhook
        "Discord" => JsonSerializer.Serialize(new { content = message, allowed_mentions = new { parse = Array.Empty<string>() } }),
        // A static fallback contains no provider text; plain_text blocks disable Slack's mention/markup parser.
        // https://docs.slack.dev/messaging/sending-messages-using-incoming-webhooks/
        "Slack" => JsonSerializer.Serialize(new
        {
            text = title,
            mrkdwn = false,
            blocks = new[] { new { type = "section", text = new { type = "plain_text", text = message, emoji = false } } }
        }),
        // https://docs.ntfy.sh/publish/#publish-as-json — JSON publishes to the instance root, not /topic.
        "Ntfy" => JsonSerializer.Serialize(new { topic = Topic(new Uri(url)), title, message }),
        _ => throw new ArgumentException("Unsupported webhook adapter.")
    };

    private static string Topic(Uri configured)
    {
        var topic = Uri.UnescapeDataString(configured.AbsolutePath[(configured.AbsolutePath.LastIndexOf('/') + 1)..]);
        if (topic.Length is < 1 or > 64 || topic.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
            throw new ArgumentException("Use an ntfy topic URL ending in 1 to 64 letters, numbers, underscores or dashes.");
        return topic;
    }

    private static string Plain(string value)
        => value.Replace("@", "＠", StringComparison.Ordinal).Replace("<", "＜", StringComparison.Ordinal)
            .Replace(">", "＞", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');

    private static string Clip(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes) return value;
        var length = 0;
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximumBytes - 3) break;
            length += rune.Utf16SequenceLength;
            bytes += rune.Utf8SequenceLength;
        }
        return value[..length] + "…";
    }
}
