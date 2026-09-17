using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Calendar;
using Jellyfin.Plugin.Siphon.Notifications;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Notifications;

public sealed class NotificationWebhookAdapterTests
{
    [Theory]
    [InlineData("Discord")]
    [InlineData("Slack")]
    [InlineData("Ntfy")]
    public void ProviderMentionsRemainPlainTextAndPayloadsRespectReceiverLimits(string adapter)
    {
        var settings = new WebhookUserState { Adapter = adapter, Url = "https://receiver.example/private-topic", DigestMinutes = 10 };
        var now = DateTimeOffset.UtcNow;
        var entries = Enumerable.Range(0, 20).Select(index => new CalendarNotification(Guid.NewGuid(), "NewEpisode",
            new(Guid.NewGuid(), Guid.NewGuid(), "@everyone @here <!channel> <@&123> " + new string('界', 180),
                "<!everyone> @here " + new string('界', 200), 1, index, "2026-09-17", now, false, false), now, null, null)).ToArray();
        var delivery = new WebhookDelivery(Guid.NewGuid(), now, 0, now) { Notifications = entries };
        using var body = JsonDocument.Parse(NotificationWebhookAdapters.Create(settings, delivery));
        var root = body.RootElement;
        string message;
        switch (adapter)
        {
            case "Discord":
                Assert.Empty(root.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
                message = root.GetProperty("content").GetString()!;
                Assert.InRange(message.Length, 1, 2000);
                break;
            case "Slack":
                Assert.False(root.GetProperty("mrkdwn").GetBoolean());
                Assert.DoesNotContain("@", root.GetProperty("text").GetString());
                var block = Assert.Single(root.GetProperty("blocks").EnumerateArray());
                Assert.Equal("section", block.GetProperty("type").GetString());
                Assert.Equal("plain_text", block.GetProperty("text").GetProperty("type").GetString());
                message = block.GetProperty("text").GetProperty("text").GetString()!;
                Assert.InRange(message.Length, 1, 3000);
                break;
            default:
                Assert.Equal("private-topic", root.GetProperty("topic").GetString());
                message = root.GetProperty("message").GetString()!;
                Assert.InRange(Encoding.UTF8.GetByteCount(message), 1, 4096);
                Assert.False(root.TryGetProperty("actions", out _));
                break;
        }
        Assert.DoesNotContain("@", message);
        Assert.DoesNotContain("<!", message);
        Assert.Equal(20, message.Split('\n').Count(line => line.StartsWith("New episode", StringComparison.Ordinal)));
    }

    [Fact]
    public void NtfyPostsJsonAtSameOriginParentAndKeepsAuthenticationQuery()
    {
        var configured = new Uri("https://receiver.example/ntfy/my-topic?auth=private");
        var endpoint = NotificationWebhookAdapters.Endpoint("Ntfy", configured);
        Assert.Equal("https://receiver.example/ntfy/?auth=private", endpoint.AbsoluteUri);
        Assert.Throws<ArgumentException>(() => NotificationWebhookAdapters.Endpoint("Ntfy", new Uri("https://receiver.example/")));
        Assert.Throws<ArgumentException>(() => NotificationWebhookAdapters.Endpoint("Ntfy", new Uri("https://receiver.example/topic%2Fother")));
    }

    [Fact]
    public void DiscordWaitAcknowledgesMessagesWithoutDiscardingThreadSelection()
    {
        var configured = new Uri("https://receiver.example/hooks/private?thread_id=123&wait=false");
        Assert.Equal("https://receiver.example/hooks/private?thread_id=123&wait=true",
            NotificationWebhookAdapters.Endpoint("Discord", configured).AbsoluteUri);
    }

    [Fact]
    public void SettingsRejectUnknownAdapterKindsAndOutOfRangeIntervals()
    {
        var settings = new NotificationWebhookSettings(true, "https://receiver.example/hook");
        Assert.Throws<ArgumentException>(() => (settings with { Adapter = "Unknown" }).Normalize());
        Assert.Throws<ArgumentException>(() => (settings with { Kinds = ["Unknown"] }).Normalize());
        Assert.Throws<ArgumentException>(() => (settings with { DigestMinutes = -1 }).Normalize());
        Assert.Throws<ArgumentException>(() => (settings with { DigestMinutes = 1441 }).Normalize());
    }
}
