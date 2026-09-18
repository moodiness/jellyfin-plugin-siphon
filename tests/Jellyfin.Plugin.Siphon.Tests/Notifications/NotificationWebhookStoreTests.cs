using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Calendar;
using Jellyfin.Plugin.Siphon.Notifications;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Notifications;

public sealed class NotificationWebhookStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "siphon-webhooks-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private string PathName => Path.Combine(_directory, "webhooks.bin");
    private NotificationWebhookStore Open() => new(PathName, _key);

    [Fact]
    public void SignatureMatchesRfc4231AndAuthenticatesExactUtf8Payload()
    {
        var secret = Convert.ToBase64String(Enumerable.Repeat((byte)0x0b, 20).ToArray());
        Assert.Equal("sha256=b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7",
            NotificationWebhookPayload.Sign("Hi There", secret));
        var entry = Entry(Now);
        var payload = NotificationWebhookPayload.Create(entry);
        using var document = JsonDocument.Parse(payload);
        Assert.Equal(entry.Id, document.RootElement.GetProperty("EventId").GetGuid());
        Assert.False(document.RootElement.GetProperty("Test").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("ReadUtc", out _));
        Assert.Equal(payload, NotificationWebhookPayload.Create(entry with { ReadUtc = Now.AddMinutes(1) }));
        Assert.NotEqual(NotificationWebhookPayload.Sign(payload, secret), NotificationWebhookPayload.Sign(payload + " ", secret));
    }

    [Fact]
    public async Task RestartRetainsEncryptedSettingsAttemptsAndDedupWithoutPreOptInBacklog()
    {
        var user = Guid.NewGuid();
        var old = Entry(Now.AddMinutes(-1));
        var future = Entry(Now.AddSeconds(1));
        string secret;
        using (var store = Open())
        {
            await store.ConfigureAsync(user, new(true, "https://receiver.example/private-token"), true, [old], Now, default);
            secret = await store.RotateAsync(user, true, [old], Now, default);
            await Observe(store, user, [future, old], Now.AddSeconds(2));
            Assert.Equal(future.Id, Assert.Single(store.Get(user).Pending).EventId);
            await store.BeginAttemptAsync(user, store.Get(user).Generation, future.Id, Now.AddSeconds(3), default);
        }
        var bytes = await File.ReadAllBytesAsync(PathName);
        Assert.DoesNotContain("private-token", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(bytes));
        using (var store = Open())
        {
            Assert.Equal(secret, store.Get(user).Secret);
            var pending = Assert.Single(store.Get(user).Pending);
            Assert.Equal(1, pending.Attempts);
            Assert.True(pending.NextAttemptUtc > Now.AddSeconds(3));
            await store.CompleteAsync(user, store.Get(user).Generation, future.Id, true, false, null, Now.AddSeconds(4), default);
        }
        using (var store = Open())
        {
            await Observe(store, user, [future, old], Now.AddMinutes(1));
            Assert.Empty(store.Get(user).Pending);
            Assert.Equal(Now.AddSeconds(4), store.Get(user).LastSuccessUtc);
        }
    }

    [Fact]
    public async Task OptOutReconfigurationAndRotationRevokePendingAndNeverReplayTheirBacklog()
    {
        var user = Guid.NewGuid();
        using var store = Open();
        await Enable(store, user);
        var first = Entry(Now.AddSeconds(1));
        await Observe(store, user, [first], Now.AddSeconds(2));
        var originalGeneration = store.Get(user).Generation;
        await store.ConfigureAsync(user, new(false, "https://receiver.example/one"), true, [first], Now.AddSeconds(3), default);
        Assert.Empty(store.Get(user).Pending);
        await store.ConfigureAsync(user, new(true, "https://receiver.example/two"), true, [first], Now.AddSeconds(4), default);
        await Observe(store, user, [first], Now.AddSeconds(5));
        Assert.Empty(store.Get(user).Pending);
        var second = Entry(Now.AddSeconds(6));
        await Observe(store, user, [second, first], Now.AddSeconds(7));
        Assert.Equal(second.Id, Assert.Single(store.Get(user).Pending).EventId);
        var previousSecret = store.Get(user).Secret;
        var rotated = await store.RotateAsync(user, true, [second, first], Now.AddSeconds(8), default);
        Assert.NotEqual(previousSecret, rotated);
        Assert.Empty(store.Get(user).Pending);
        await store.CompleteAsync(user, originalGeneration, first.Id, true, false, null, Now.AddSeconds(9), default);
        Assert.Null(store.Get(user).LastSuccessUtc);
        await Observe(store, user, [second, first], Now.AddSeconds(10));
        Assert.Empty(store.Get(user).Pending);
    }

    [Fact]
    public async Task UsersAndRevokedLibraryOrNotificationPermissionsRemainIsolated()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        using var store = Open();
        await Enable(store, alice);
        await Enable(store, bob);
        var first = Entry(Now.AddSeconds(1));
        var second = Entry(Now.AddSeconds(2));
        await Observe(store, alice, [first], Now.AddSeconds(3));
        await Observe(store, bob, [second], Now.AddSeconds(3));
        Assert.Equal(first.Id, Assert.Single(store.Get(alice).Pending).EventId);
        Assert.Equal(second.Id, Assert.Single(store.Get(bob).Pending).EventId);
        Assert.NotEqual(store.Get(alice).Secret, store.Get(bob).Secret);
        await store.ObserveAsync(alice, true, [first], new HashSet<Guid>(), Now.AddSeconds(4), default);
        await Observe(store, alice, [first], Now.AddSeconds(5));
        Assert.Empty(store.Get(alice).Pending);
        Assert.Single(store.Get(bob).Pending);
        await store.ObserveAsync(bob, false, [second], new HashSet<Guid>(), Now.AddSeconds(6), default);
        await Observe(store, bob, [second], Now.AddSeconds(7));
        Assert.Empty(store.Get(bob).Pending);
    }

    [Fact]
    public async Task CrashAttemptsExhaustBudgetAndTestsStayRateLimitedAcrossRestart()
    {
        var user = Guid.NewGuid();
        var entry = Entry(Now.AddSeconds(1));
        using (var store = Open())
        {
            await Enable(store, user);
            await Observe(store, user, [entry], Now.AddSeconds(2));
            Assert.True(await store.ReserveTestAsync(user, Now, default));
        }
        for (var attempt = 0; attempt < NotificationWebhookStore.MaximumAttempts; attempt++)
        {
            using var store = Open();
            Assert.False(await store.ReserveTestAsync(user, Now.AddSeconds(30), default));
            await store.BeginAttemptAsync(user, store.Get(user).Generation, entry.Id, Now.AddMinutes(attempt + 1), default);
        }
        using (var store = Open())
        {
            await Observe(store, user, [entry], Now.AddHours(1));
            Assert.Empty(store.Get(user).Pending);
            Assert.True(await store.ReserveTestAsync(user, Now.AddMinutes(1), default));
        }
    }

    [Fact]
    public async Task TamperingOrDifferentServerKeyFailsClosed()
    {
        using (var store = Open()) await Enable(store, Guid.NewGuid());
        Assert.Throws<InvalidDataException>(() => new NotificationWebhookStore(PathName, RandomNumberGenerator.GetBytes(32)));
        var bytes = await File.ReadAllBytesAsync(PathName);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(PathName, bytes);
        Assert.Throws<InvalidDataException>(() => Open());
    }

    [Fact]
    public async Task DigestFreezesEveryMemberAndSurvivesRestartReadChangesAndLaterEvents()
    {
        var user = Guid.NewGuid();
        var first = Entry(Now.AddSeconds(1));
        var second = Entry(Now.AddSeconds(2));
        Guid id;
        string body;
        using (var store = Open())
        {
            await Enable(store, user);
            await store.ConfigureAsync(user, new(true, "https://receiver.example/one") { DigestMinutes = 10 }, true, [], Now, default);
            await Observe(store, user, [first], Now.AddSeconds(3));
            var waiting = Assert.Single(store.Get(user).Pending);
            Assert.Equal(Now.AddMinutes(10).AddSeconds(3), waiting.NextAttemptUtc);
            await Observe(store, user, [second, first], Now.AddMinutes(2));
            var group = Assert.Single(store.Get(user).Pending);
            Assert.Equal(waiting.EventId, group.EventId);
            Assert.Equal(new[] { first.Id, second.Id }, group.Notifications.Select(entry => entry.Id));
            id = group.EventId;
            await store.BeginAttemptAsync(user, store.Get(user).Generation, id, group.NextAttemptUtc, default);
            body = Assert.Single(store.Get(user).Pending).Body!;
            using var payload = JsonDocument.Parse(body);
            Assert.Equal("calendar.digest", payload.RootElement.GetProperty("Type").GetString());
            Assert.Equal(id, payload.RootElement.GetProperty("EventId").GetGuid());
            Assert.Equal(2, payload.RootElement.GetProperty("Notifications").GetArrayLength());
            await store.CompleteAsync(user, store.Get(user).Generation, id, false, true, "Webhook delivery failed.", Now.AddMinutes(11), default);
        }
        using (var store = Open())
        {
            var third = Entry(Now.AddMinutes(11));
            await Observe(store, user, [third, second, first with { ReadUtc = Now.AddMinutes(11) }], Now.AddMinutes(12));
            var frozen = Assert.Single(store.Get(user).Pending, entry => entry.EventId == id);
            Assert.Equal(body, frozen.Body);
            Assert.Equal(2, frozen.Notifications.Length);
            Assert.Single(store.Get(user).Pending, entry => entry.EventId != id && entry.Notifications.Single().Id == third.Id);
            await store.BeginAttemptAsync(user, store.Get(user).Generation, id, Now.AddMinutes(13), default);
            Assert.Equal(body, Assert.Single(store.Get(user).Pending, entry => entry.EventId == id).Body);
            Assert.Equal(2, store.Get(user).LastAttemptCount);
            Assert.Equal(Now.AddMinutes(13), store.Get(user).LastAttemptUtc);
            Assert.Equal("Retrying", store.Get(user).LastOutcome);
        }
    }

    [Fact]
    public async Task OneRevokedDigestMemberAbandonsWholeFrozenBodyWithoutReplayingAccessibleMembers()
    {
        using var store = Open();
        var user = Guid.NewGuid();
        await Enable(store, user);
        await store.ConfigureAsync(user, new(true, "https://receiver.example/one") { DigestMinutes = 1 }, true, [], Now, default);
        var first = Entry(Now.AddSeconds(1));
        var second = Entry(Now.AddSeconds(2));
        await Observe(store, user, [first, second], Now.AddSeconds(3));
        var pending = Assert.Single(store.Get(user).Pending);
        await store.BeginAttemptAsync(user, store.Get(user).Generation, pending.EventId, Now.AddMinutes(2), default);
        await store.ObserveAsync(user, true, [first, second], new HashSet<Guid> { first.Id }, Now.AddMinutes(3), default);
        Assert.Empty(store.Get(user).Pending);
        Assert.Equal("Abandoned", store.Get(user).LastOutcome);
        Assert.Equal(1, store.Get(user).AbandonedCount);
        await Observe(store, user, [first, second], Now.AddMinutes(4));
        Assert.Empty(store.Get(user).Pending);
    }

    [Fact]
    public async Task FiltersAndAdapterChangesEstablishNewBaselinesWithoutBacklog()
    {
        using var store = Open();
        var user = Guid.NewGuid();
        var settings = new NotificationWebhookSettings(true, "https://receiver.example/one") { Adapter = "Slack", Kinds = ["DateChanged"] };
        await store.ConfigureAsync(user, settings, true, [], Now, default);
        Assert.True(store.Get(user).Active); // Vendor adapters do not need a manual secret-exchange ceremony.
        var first = Entry(Now.AddSeconds(1));
        var second = Entry(Now.AddSeconds(2)) with { Kind = "DateChanged" };
        await Observe(store, user, [first, second], Now.AddSeconds(3));
        Assert.Equal(second.Id, Assert.Single(store.Get(user).Pending).EventId);
        var generation = store.Get(user).Generation;
        await store.ConfigureAsync(user, settings with { Adapter = "Discord", DigestMinutes = 5, Kinds = ["NewEpisode"] },
            true, [first, second], Now.AddSeconds(4), default);
        Assert.NotEqual(generation, store.Get(user).Generation);
        await Observe(store, user, [first, second], Now.AddSeconds(5));
        Assert.Empty(store.Get(user).Pending);
        var third = Entry(Now.AddSeconds(6));
        await Observe(store, user, [first, second, third], Now.AddSeconds(7));
        Assert.Equal(third.Id, Assert.Single(Assert.Single(store.Get(user).Pending).Notifications).Id);
        await store.ConfigureAsync(user, settings with { Kinds = [] }, true, [first, second, third], Now.AddSeconds(8), default);
        await Observe(store, user, [first, second, third, Entry(Now.AddSeconds(9))], Now.AddSeconds(10));
        Assert.Empty(store.Get(user).Pending);
        Assert.Equal("Suppressed", store.Get(user).LastOutcome);
    }

    [Fact]
    public async Task LongestDigestHasDeliveryWindowAndLargeUnicodeDigestsSplitBelowTransportLimit()
    {
        using var store = Open();
        var user = Guid.NewGuid();
        await Enable(store, user);
        await store.ConfigureAsync(user, new(true, "https://receiver.example/one") { DigestMinutes = 1440 }, true, [], Now, default);
        var entries = Enumerable.Range(1, 40).Select(index =>
        {
            var entry = Entry(Now.AddSeconds(index));
            return entry with { Episode = entry.Episode with { Name = new string('界', 240), SeriesName = new string('界', 240) } };
        }).ToArray();
        await Observe(store, user, entries, Now.AddMinutes(1));
        await Observe(store, user, entries, Now.AddDays(1).AddMinutes(1));
        Assert.Equal(entries.Select(entry => entry.Id).Order(),
            store.Get(user).Pending.SelectMany(entry => entry.Notifications).Select(entry => entry.Id).Order());
        foreach (var pending in store.Get(user).Pending)
        {
            await store.BeginAttemptAsync(user, store.Get(user).Generation, pending.EventId, Now.AddDays(1).AddMinutes(1), default);
            var frozen = Assert.Single(store.Get(user).Pending, entry => entry.EventId == pending.EventId);
            Assert.InRange(Encoding.UTF8.GetByteCount(frozen.Body!), 1, NotificationWebhookAdapters.MaximumBodyBytes);
        }
    }

    [Fact]
    public async Task VersionOneSettingsSurviveUpgradeWithoutReplayingOldOrUnobservedBacklog()
    {
        var user = Guid.NewGuid();
        var entry = Entry(Now.AddSeconds(1));
        var unseen = Entry(Now.AddSeconds(2));
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1,
            Users = new Dictionary<Guid, object>
            {
                [user] = new
                {
                    Enabled = true,
                    Active = true,
                    Url = "https://receiver.example/one",
                    Secret = secret,
                    Generation = Guid.NewGuid(),
                    SinceUtc = Now,
                    Seen = new[] { entry.Id },
                    Pending = new[] { new { EventId = entry.Id, entry.CreatedUtc, Attempts = 1, NextAttemptUtc = Now.AddMinutes(1) } }
                }
            }
        });
        var domain = Encoding.UTF8.GetBytes("Siphon.NotificationWebhooks.v1");
        var encrypted = new byte[plaintext.Length + 28];
        RandomNumberGenerator.Fill(encrypted.AsSpan(0, 12));
        using (var aes = new AesGcm(HMACSHA256.HashData(_key, domain), 16))
            aes.Encrypt(encrypted.AsSpan(0, 12), plaintext, encrypted.AsSpan(28), encrypted.AsSpan(12, 16), domain);
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(PathName, encrypted);
        using var store = Open();
        Assert.Equal(secret, store.Get(user).Secret);
        Assert.True(store.Get(user).Enabled);
        await Observe(store, user, [entry, unseen], Now.AddMinutes(2));
        Assert.Empty(store.Get(user).Pending);
        var fresh = Entry(Now.AddMinutes(3));
        await Observe(store, user, [entry, unseen, fresh], Now.AddMinutes(4));
        Assert.Equal(fresh.Id, Assert.Single(store.Get(user).Pending).EventId);
    }

    [Fact]
    public async Task RetryExhaustionIsFiniteObservableAndCannotBeResurrected()
    {
        using var store = Open();
        var user = Guid.NewGuid();
        await Enable(store, user);
        var entry = Entry(Now.AddSeconds(1));
        await Observe(store, user, [entry], Now.AddSeconds(2));
        for (var attempt = 1; attempt <= NotificationWebhookStore.MaximumAttempts; attempt++)
        {
            var delivery = Assert.Single(store.Get(user).Pending);
            await store.BeginAttemptAsync(user, store.Get(user).Generation, entry.Id, delivery.NextAttemptUtc, default);
            await store.CompleteAsync(user, store.Get(user).Generation, entry.Id, false, true,
                "Webhook delivery failed.", delivery.NextAttemptUtc, default);
            Assert.Equal(attempt, store.Get(user).LastAttemptCount);
            Assert.Equal(attempt == NotificationWebhookStore.MaximumAttempts ? "Abandoned" : "Retrying", store.Get(user).LastOutcome);
        }
        await Observe(store, user, [entry], Now.AddHours(12));
        Assert.Empty(store.Get(user).Pending);
        Assert.Equal(1, store.GetHealth(true).Abandoned);
        Assert.Equal(0, store.GetHealth(true).Retrying);
    }

    private static async Task Enable(NotificationWebhookStore store, Guid user)
    {
        await store.ConfigureAsync(user, new(true, "https://receiver.example/one"), true, [], Now, default);
        await store.RotateAsync(user, true, [], Now, default);
    }

    private static Task Observe(NotificationWebhookStore store, Guid user, CalendarNotification[] inbox, DateTimeOffset now)
        => store.ObserveAsync(user, true, inbox, inbox.Select(entry => entry.Id).ToHashSet(), now, default);

    private static CalendarNotification Entry(DateTimeOffset now) => new(Guid.NewGuid(), "NewEpisode",
        new(Guid.NewGuid(), Guid.NewGuid(), "Série", "Episode", 1, 1, "2026-09-18", now.AddDays(1), false, false), now, null, null);

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
