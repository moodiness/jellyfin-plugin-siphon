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
            await store.ConfigureAsync(user, true, "https://receiver.example/private-token", true, [old], Now, default);
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
        await store.ConfigureAsync(user, false, "https://receiver.example/one", true, [first], Now.AddSeconds(3), default);
        Assert.Empty(store.Get(user).Pending);
        await store.ConfigureAsync(user, true, "https://receiver.example/two", true, [first], Now.AddSeconds(4), default);
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

    private static async Task Enable(NotificationWebhookStore store, Guid user)
    {
        await store.ConfigureAsync(user, true, "https://receiver.example/one", true, [], Now, default);
        await store.RotateAsync(user, true, [], Now, default);
    }

    private static Task Observe(NotificationWebhookStore store, Guid user, CalendarNotification[] inbox, DateTimeOffset now)
        => store.ObserveAsync(user, true, inbox, inbox.Select(entry => entry.Id).ToHashSet(), now, default);

    private static CalendarNotification Entry(DateTimeOffset now) => new(Guid.NewGuid(), "NewEpisode",
        new(Guid.NewGuid(), Guid.NewGuid(), "Série", "Episode", 1, 1, "2026-09-18", now.AddDays(1), false, false), now, null, null);

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
