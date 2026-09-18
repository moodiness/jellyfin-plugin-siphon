using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Calendar;
using Jellyfin.Plugin.Siphon.Infrastructure;

namespace Jellyfin.Plugin.Siphon.Notifications;

/// <summary>Encrypted endpoint settings and bounded delivery cursors, independent of inbox read state.</summary>
public sealed class NotificationWebhookStore : IDisposable
{
    internal const int MaximumAttempts = 5;
    private const int MaximumUsers = 1024;
    private const int MaximumPendingTotal = 10_000;
    private const int MaximumFileBytes = 16 * 1024 * 1024;
    private const long MaximumRetainedBytes = 8 * 1024 * 1024;
    private static readonly byte[] Domain = Encoding.UTF8.GetBytes("Siphon.NotificationWebhooks.v1");
    private readonly string _path;
    private readonly byte[] _key;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private Dictionary<Guid, WebhookUserState> _users;

    public NotificationWebhookStore(SiphonPaths paths, SiphonSecretStore secrets)
        : this(Path.Combine(paths.DataDirectory, "notification-webhooks.bin"), secrets.GetSigningKey()) { }

    internal NotificationWebhookStore(string path, byte[] signingKey)
    {
        _path = path;
        _key = HMACSHA256.HashData(signingKey, Domain);
        _users = Load();
    }

    internal WebhookUserState Get(Guid userId) => Volatile.Read(ref _users).GetValueOrDefault(userId) ?? new();
    internal Guid[] GetUsers() => Volatile.Read(ref _users).Keys.Order().ToArray();

    internal NotificationWebhookHealth GetHealth(bool enabled)
    {
        var values = Volatile.Read(ref _users).Values;
        return new(enabled, values.Count(value => value.Enabled && value.Url.Length > 0),
            values.Sum(value => value.Pending.Length), values.Sum(value => value.Pending.Count(entry => entry.Attempts > 0)),
            values.Sum(value => value.AbandonedCount));
    }

    internal Task ConfigureAsync(Guid userId, NotificationWebhookSettings settings, bool allowed,
        IReadOnlyList<CalendarNotification> inbox, DateTimeOffset now, CancellationToken ct)
    {
        settings = settings.Normalize();
        return ChangeAsync(userId, value =>
        {
            if (value.Enabled == settings.Enabled && value.Url == settings.Url && value.Adapter == settings.Adapter
                && value.DigestMinutes == settings.DigestMinutes && value.Kinds.Order(StringComparer.Ordinal).SequenceEqual(settings.Kinds))
                return value;
            var secret = value.Secret.Length == 0 && settings.Adapter != "Json"
                ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) : value.Secret;
            return Baseline(value with
            {
                Enabled = settings.Enabled,
                Url = settings.Url,
                Adapter = settings.Adapter,
                Kinds = settings.Kinds,
                DigestMinutes = settings.DigestMinutes,
                Secret = secret,
                LastError = null,
                LastSuccessUtc = null
            }, settings.Enabled && allowed && secret.Length > 0, inbox, now);
        }, ct);
    }

    internal async Task<string> RotateAsync(Guid userId, bool allowed, IReadOnlyList<CalendarNotification> inbox,
        DateTimeOffset now, CancellationToken ct)
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await ChangeAsync(userId, value => Baseline(value with { Secret = secret, LastError = null },
            allowed && value.Enabled, inbox, now), ct).ConfigureAwait(false);
        return secret;
    }

    internal Task ObserveAsync(Guid userId, bool allowed, IReadOnlyList<CalendarNotification> inbox,
        IReadOnlySet<Guid> accessible, DateTimeOffset now, CancellationToken ct)
        => ChangeAsync(userId, value =>
        {
            var active = allowed && value.Enabled && value.Secret.Length > 0;
            if (!active || !value.Active)
                return value.Active == active && value.Pending.Length == 0 && !active ? value : Baseline(value, active, inbox, now);
            var current = inbox.ToDictionary(entry => entry.Id);
            var seen = value.Seen.ToHashSet();
            var pending = value.Pending.Where(delivery => delivery.Attempts < MaximumAttempts
                && now < delivery.DueUtc.AddDays(1)
                && delivery.Notifications.All(entry => accessible.Contains(entry.Id)
                    && current.TryGetValue(entry.Id, out var committed) && WebhookDelivery.SameEvent(entry, committed))).ToList();
            // A retained group is indivisible: never rewrite a frozen body after one member loses authority.
            var abandoned = value.Pending.Length - pending.Count;
            var suppressed = false;
            var capacityReached = false;
            var eventCount = pending.Sum(delivery => delivery.Notifications.Length);
            foreach (var entry in inbox.OrderBy(entry => entry.CreatedUtc).ThenBy(entry => entry.Id))
            {
                if (!seen.Add(entry.Id) || entry.CreatedUtc <= value.SinceUtc) continue;
                if (!accessible.Contains(entry.Id) || !value.Kinds.Contains(entry.Kind, StringComparer.Ordinal)
                    || now - entry.CreatedUtc >= TimeSpan.FromDays(1))
                {
                    suppressed = true;
                    continue;
                }
                if (eventCount >= CalendarNotificationStore.MaximumInboxPerUser)
                {
                    suppressed = capacityReached = true;
                    continue;
                }
                var notification = entry with { ReadUtc = null };
                var groupIndex = value.DigestMinutes == 0 ? -1 : pending.FindLastIndex(delivery => delivery.Body is null
                    && delivery.DueUtc > now && delivery.Notifications.Length < NotificationWebhookAdapters.MaximumDigestEvents);
                if (groupIndex >= 0 && value.Adapter == "Json")
                {
                    var candidate = pending[groupIndex] with { Notifications = [.. pending[groupIndex].Notifications, notification] };
                    if (Encoding.UTF8.GetByteCount(NotificationWebhookAdapters.Create(value, candidate)) > NotificationWebhookAdapters.MaximumBodyBytes)
                        groupIndex = -1;
                }
                if (groupIndex >= 0)
                    pending[groupIndex] = pending[groupIndex] with { Notifications = [.. pending[groupIndex].Notifications, notification] };
                else
                {
                    var due = now.AddMinutes(value.DigestMinutes);
                    pending.Add(new(value.DigestMinutes == 0 ? entry.Id : Guid.NewGuid(), entry.CreatedUtc, 0, due)
                    {
                        DueUtc = due,
                        Notifications = [notification]
                    });
                }
                eventCount++;
            }
            var nextSeen = seen.Where(current.ContainsKey).Order().ToArray();
            return value.Seen.SequenceEqual(nextSeen) && value.Pending.SequenceEqual(pending) && !suppressed ? value
                : value with
                {
                    Seen = nextSeen,
                    Pending = pending.ToArray(),
                    AbandonedCount = value.AbandonedCount + abandoned,
                    LastOutcome = abandoned > 0 ? "Abandoned" : suppressed ? "Suppressed" : value.LastOutcome,
                    LastError = abandoned > 0 ? "Webhook delivery expired, exhausted its retry budget or lost notification access."
                        : capacityReached ? "Webhook pending delivery capacity reached; excess events were not queued." : value.LastError
                };
        }, ct);

    internal Task BeginAttemptAsync(Guid userId, Guid generation, Guid eventId, DateTimeOffset now, CancellationToken ct)
        => ChangeAsync(userId, value =>
        {
            if (value.Generation != generation) return value;
            var delivery = value.Pending.FirstOrDefault(entry => entry.EventId == eventId);
            if (delivery is null || delivery.Attempts >= MaximumAttempts) return value;
            var body = delivery.Body ?? NotificationWebhookAdapters.Create(value, delivery);
            if (Encoding.UTF8.GetByteCount(body) > NotificationWebhookAdapters.MaximumBodyBytes)
                return Abandon(value, delivery, "Webhook payload exceeded the delivery size limit.");
            var next = delivery with
            {
                Body = body,
                Attempts = delivery.Attempts + 1,
                NextAttemptUtc = now.AddSeconds(30 * Math.Pow(4, delivery.Attempts))
            };
            return value with
            {
                Pending = value.Pending.Select(entry => entry.EventId == eventId ? next : entry).ToArray(),
                LastAttemptUtc = now,
                LastAttemptCount = next.Attempts,
                LastOutcome = "Retrying"
            };
        }, ct);

    internal Task CompleteAsync(Guid userId, Guid generation, Guid eventId, bool success, bool retry,
        string? error, DateTimeOffset now, CancellationToken ct)
        => ChangeAsync(userId, value =>
        {
            if (value.Generation != generation) return value;
            var delivery = value.Pending.FirstOrDefault(entry => entry.EventId == eventId);
            if (delivery is null) return value;
            var retain = !success && retry && delivery.Attempts < MaximumAttempts && now < delivery.DueUtc.AddDays(1);
            return value with
            {
                Pending = retain ? value.Pending : value.Pending.Where(entry => entry.EventId != eventId).ToArray(),
                LastSuccessUtc = success ? now : value.LastSuccessUtc,
                LastOutcome = success ? "Delivered" : retain ? "Retrying" : "Abandoned",
                AbandonedCount = value.AbandonedCount + (!success && !retain ? 1 : 0),
                LastError = success ? null : error
            };
        }, ct);

    private static WebhookUserState Abandon(WebhookUserState value, WebhookDelivery delivery, string error)
        => value with
        {
            Pending = value.Pending.Where(entry => entry.EventId != delivery.EventId).ToArray(),
            LastOutcome = "Abandoned",
            AbandonedCount = value.AbandonedCount + 1,
            LastError = error
        };

    internal async Task<bool> ReserveTestAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var reserved = false;
        await ChangeAsync(userId, value =>
        {
            if (value.LastTestUtc is { } previous && now - previous < TimeSpan.FromMinutes(1)) return value;
            reserved = true;
            return value with { LastTestUtc = now, LastAttemptUtc = now, LastAttemptCount = 1, LastOutcome = null };
        }, ct).ConfigureAwait(false);
        return reserved;
    }

    internal Task RecordTestAsync(Guid userId, Guid generation, bool success, DateTimeOffset now, CancellationToken ct)
        => ChangeAsync(userId, value => value.Generation != generation ? value : value with
        {
            LastSuccessUtc = success ? now : value.LastSuccessUtc,
            LastOutcome = success ? "Delivered" : "Abandoned",
            AbandonedCount = value.AbandonedCount + (success ? 0 : 1),
            LastError = success ? null : "Webhook test delivery failed."
        }, ct);

    private static WebhookUserState Baseline(WebhookUserState value, bool active,
        IReadOnlyList<CalendarNotification> inbox, DateTimeOffset now)
        => value with
        {
            Active = active,
            Generation = Guid.NewGuid(),
            SinceUtc = now,
            Seen = inbox.Select(entry => entry.Id).Distinct().Order().ToArray(),
            Pending = [],
            LastOutcome = value.Pending.Length > 0 ? "Abandoned" : value.LastOutcome,
            AbandonedCount = value.AbandonedCount + value.Pending.Length,
            LastError = value.Pending.Length > 0 ? "Pending webhook deliveries were abandoned after settings or permissions changed." : value.LastError
        };

    private async Task ChangeAsync(Guid userId, Func<WebhookUserState, WebhookUserState> change, CancellationToken ct)
    {
        if (userId == Guid.Empty) throw new ArgumentException("A current user is required.");
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previous = Get(userId);
            var next = change(previous);
            if (ReferenceEquals(previous, next)) return;
            var users = new Dictionary<Guid, WebhookUserState>(_users) { [userId] = next };
            if (users.Count > MaximumUsers) throw new WebhookCapacityException();
            var otherUsers = users.Where(pair => pair.Key != userId).Select(pair => pair.Value).ToArray();
            var remainingEvents = MaximumPendingTotal - otherUsers.Sum(value => value.Pending.Sum(entry => entry.Notifications.Length));
            var remainingBytes = MaximumRetainedBytes - otherUsers.Sum(value => value.Pending.Sum(RetainedBytes));
            var retained = new List<WebhookDelivery>();
            foreach (var delivery in next.Pending)
            {
                var size = RetainedBytes(delivery);
                if (delivery.Notifications.Length > remainingEvents || size > remainingBytes) continue;
                retained.Add(delivery);
                remainingEvents -= delivery.Notifications.Length;
                remainingBytes -= size;
            }
            if (retained.Count != next.Pending.Length)
            {
                var abandoned = next.Pending.Except(retained).Count(entry => entry.Attempts > 0);
                users[userId] = next with
                {
                    Pending = retained.ToArray(),
                    LastOutcome = abandoned > 0 ? "Abandoned" : "Suppressed",
                    AbandonedCount = next.AbandonedCount + abandoned,
                    LastError = "Webhook pending delivery capacity reached; excess events were not queued."
                };
            }
            await CommitAsync(users, ct).ConfigureAwait(false);
        }
        finally { _writer.Release(); }
    }

    // Conservative upper bound for escaped JSON copies of frozen bodies and retained event snapshots.
    private static long RetainedBytes(WebhookDelivery delivery) => (delivery.Body?.Length ?? 0) * 2L
        + delivery.Notifications.Sum(entry => 1024L + 6L * (entry.Episode.SeriesName.Length + entry.Episode.Name.Length + entry.Episode.Date.Length));

    private Dictionary<Guid, WebhookUserState> Load()
    {
        if (!File.Exists(_path)) return [];
        if (new FileInfo(_path).Length is < 29 or > MaximumFileBytes)
            throw new InvalidDataException("Webhook state has an invalid size.");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var encrypted = File.ReadAllBytes(_path);
        var plaintext = new byte[encrypted.Length - 28];
        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(encrypted.AsSpan(0, 12), encrypted.AsSpan(28), encrypted.AsSpan(12, 16), plaintext, Domain);
            var document = JsonSerializer.Deserialize<Document>(plaintext)
                ?? throw new InvalidDataException("Webhook state is empty.");
            if (document.Version is not (1 or 2) || document.Users is null || document.Users.Count > MaximumUsers
                || document.Users.Any(pair => pair.Key == Guid.Empty || pair.Value is null || !Valid(pair.Value, document.Version))
                || document.Users.Values.Sum(value => value.Pending.Sum(entry => Math.Max(1, entry.Notifications.Length))) > MaximumPendingTotal
                || document.Users.Values.Sum(value => value.Pending.Sum(RetainedBytes)) > MaximumRetainedBytes)
                throw new InvalidDataException("Webhook state has an unsupported format or limits.");
            // Version 1 has no frozen payloads. Preserve settings and secrets, but establish a fresh inbox
            // baseline on the next observation rather than reconstructing or replaying old deliveries.
            return document.Version == 2 ? document.Users : document.Users.ToDictionary(pair => pair.Key, pair => pair.Value with
            {
                Active = false,
                Generation = Guid.NewGuid(),
                Pending = [],
                LastOutcome = pair.Value.Pending.Length > 0 ? "Abandoned" : null,
                AbandonedCount = pair.Value.Pending.Length,
                LastError = pair.Value.Pending.Length > 0 ? "Pending webhook deliveries were abandoned during the delivery format upgrade." : null
            });
        }
        catch (CryptographicException) { throw new InvalidDataException("Webhook state could not be decrypted with the server signing key."); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static bool Valid(WebhookUserState value, int version)
    {
        if (value.Url is null || value.Url.Length > 2048 || value.Secret is null || value.Seen is null || value.Pending is null
            || value.Kinds is null || value.Kinds.Length > 3 || value.Kinds.Any(kind => kind is not ("NewEpisode" or "DateChanged" or "AnnouncedRelease"))
            || value.Adapter is not ("Json" or "Discord" or "Slack" or "Ntfy") || value.DigestMinutes is < 0 or > 1440
            || value.LastOutcome is not (null or "Delivered" or "Retrying" or "Abandoned" or "Suppressed")
            || value.LastAttemptCount is < 0 or > MaximumAttempts || value.AbandonedCount < 0
            || value.Seen.Length > CalendarNotificationStore.MaximumInboxPerUser
            || value.Pending.Length > CalendarNotificationStore.MaximumInboxPerUser || value.Generation == Guid.Empty
            || value.Pending.Any(entry => entry is null || entry.EventId == Guid.Empty || entry.Attempts is < 0 or > MaximumAttempts
                || entry.Notifications is null || (version == 2 && !ValidDelivery(entry)))
            || value.Pending.Sum(entry => entry.Notifications.Length) > CalendarNotificationStore.MaximumInboxPerUser
            || value.Pending.Select(entry => entry.EventId).Distinct().Count() != value.Pending.Length) return false;
        Span<byte> key = stackalloc byte[32];
        return value.Secret.Length == 0 || (Convert.TryFromBase64String(value.Secret, key, out var bytes) && bytes == 32);
    }

    private static bool ValidDelivery(WebhookDelivery delivery)
        => delivery.Notifications.Length is > 0 and <= NotificationWebhookAdapters.MaximumDigestEvents
            && delivery.Notifications.All(entry => entry is not null && entry.Id != Guid.Empty
                && entry.Episode is not null && entry.Episode.SeriesName is { Length: <= 256 }
                && entry.Episode.Name is { Length: <= 256 } && entry.Episode.Date is { Length: <= 32 }
                && entry.Kind is "NewEpisode" or "DateChanged" or "AnnouncedRelease")
            && delivery.Notifications.Select(entry => entry.Id).Distinct().Count() == delivery.Notifications.Length
            && delivery.DueUtc != default
            && (delivery.Body is null ? delivery.Attempts == 0
                : Encoding.UTF8.GetByteCount(delivery.Body) <= NotificationWebhookAdapters.MaximumBodyBytes);

    private async Task CommitAsync(Dictionary<Guid, WebhookUserState> users, CancellationToken ct)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new Document { Users = users });
        var encrypted = new byte[plaintext.Length + 28];
        try
        {
            if (encrypted.Length > MaximumFileBytes) throw new WebhookCapacityException();
            RandomNumberGenerator.Fill(encrypted.AsSpan(0, 12));
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(encrypted.AsSpan(0, 12), plaintext, encrypted.AsSpan(28), encrypted.AsSpan(12, 16), Domain);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(encrypted, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
            Volatile.Write(ref _users, users);
        }
        finally { File.Delete(temporary); }
    }

    public void Dispose() { _writer.Dispose(); CryptographicOperations.ZeroMemory(_key); }

    private sealed record Document
    {
        public int Version { get; init; } = 2;
        public Dictionary<Guid, WebhookUserState> Users { get; init; } = [];
    }
}

internal sealed class WebhookCapacityException : InvalidOperationException { }
