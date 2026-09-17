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

    internal Task ConfigureAsync(Guid userId, bool enabled, string url, bool allowed,
        IReadOnlyList<CalendarNotification> inbox, DateTimeOffset now, CancellationToken ct)
        => ChangeAsync(userId, value => value.Enabled == enabled && value.Url == url ? value
            : Baseline(value with { Enabled = enabled, Url = url, LastError = null, LastSuccessUtc = null },
                enabled && allowed && value.Secret.Length > 0, inbox, now), ct);

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
            var current = inbox.Select(entry => entry.Id).ToHashSet();
            var seen = value.Seen.ToHashSet();
            var exhausted = value.Pending.Any(entry => entry.Attempts >= MaximumAttempts || now - entry.CreatedUtc >= TimeSpan.FromDays(1));
            var pending = value.Pending.Where(entry => current.Contains(entry.EventId) && accessible.Contains(entry.EventId)
                && entry.Attempts < MaximumAttempts && now - entry.CreatedUtc < TimeSpan.FromDays(1)).ToList();
            foreach (var entry in inbox.OrderBy(entry => entry.CreatedUtc).ThenBy(entry => entry.Id))
            {
                if (seen.Add(entry.Id) && entry.CreatedUtc > value.SinceUtc && accessible.Contains(entry.Id)
                    && now - entry.CreatedUtc < TimeSpan.FromDays(1) && pending.Count < CalendarNotificationStore.MaximumInboxPerUser)
                    pending.Add(new(entry.Id, entry.CreatedUtc, 0, now));
            }
            var nextSeen = seen.Where(current.Contains).Order().ToArray();
            return value.Seen.SequenceEqual(nextSeen) && value.Pending.SequenceEqual(pending) ? value
                : value with
                {
                    Seen = nextSeen,
                    Pending = pending.ToArray(),
                    LastError = exhausted ? "Webhook delivery expired or exhausted its retry budget." : value.LastError
                };
        }, ct);

    internal Task BeginAttemptAsync(Guid userId, Guid generation, Guid eventId, DateTimeOffset now, CancellationToken ct)
        => ChangeAsync(userId, value => value.Generation != generation ? value : value with
        {
            Pending = value.Pending.Select(entry => entry.EventId != eventId ? entry : entry with
            {
                Attempts = entry.Attempts + 1,
                NextAttemptUtc = now.AddSeconds(30 * Math.Pow(4, entry.Attempts))
            }).ToArray()
        }, ct);

    internal Task CompleteAsync(Guid userId, Guid generation, Guid eventId, bool success, bool retry,
        string? error, DateTimeOffset now, CancellationToken ct)
        => ChangeAsync(userId, value => value.Generation != generation ? value : value with
        {
            Pending = value.Pending.Where(entry => entry.EventId != eventId
                || (!success && retry && entry.Attempts < MaximumAttempts)).ToArray(),
            LastSuccessUtc = success ? now : value.LastSuccessUtc,
            LastError = success ? null : error
        }, ct);

    internal async Task<bool> ReserveTestAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var reserved = false;
        await ChangeAsync(userId, value =>
        {
            if (value.LastTestUtc is { } previous && now - previous < TimeSpan.FromMinutes(1)) return value;
            reserved = true;
            return value with { LastTestUtc = now };
        }, ct).ConfigureAwait(false);
        return reserved;
    }

    internal Task RecordTestAsync(Guid userId, Guid generation, bool success, DateTimeOffset now, CancellationToken ct)
        => CompleteAsync(userId, generation, Guid.Empty, success, false,
            success ? null : "Webhook test delivery failed.", now, ct);

    private static WebhookUserState Baseline(WebhookUserState value, bool active,
        IReadOnlyList<CalendarNotification> inbox, DateTimeOffset now)
        => value with
        {
            Active = active,
            Generation = Guid.NewGuid(),
            SinceUtc = now,
            Seen = inbox.Select(entry => entry.Id).Distinct().Order().ToArray(),
            Pending = []
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
            if (users.Count > MaximumUsers) throw new InvalidOperationException("Webhook settings capacity reached.");
            var otherPending = users.Where(pair => pair.Key != userId).Sum(pair => pair.Value.Pending.Length);
            if (otherPending + next.Pending.Length > MaximumPendingTotal)
                users[userId] = next with
                {
                    Pending = next.Pending.Take(Math.Max(0, MaximumPendingTotal - otherPending)).ToArray(),
                    LastError = "Webhook pending delivery capacity reached; excess events were not queued."
                };
            await CommitAsync(users, ct).ConfigureAwait(false);
        }
        finally { _writer.Release(); }
    }

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
            if (document.Version != 1 || document.Users is null || document.Users.Count > MaximumUsers
                || document.Users.Any(pair => pair.Key == Guid.Empty || pair.Value is null || !Valid(pair.Value))
                || document.Users.Values.Sum(value => value.Pending.Length) > MaximumPendingTotal)
                throw new InvalidDataException("Webhook state has an unsupported format or limits.");
            return document.Users;
        }
        catch (CryptographicException) { throw new InvalidDataException("Webhook state could not be decrypted with the server signing key."); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static bool Valid(WebhookUserState value)
    {
        if (value.Url is null || value.Url.Length > 2048 || value.Secret is null || value.Seen is null || value.Pending is null
            || value.Seen.Length > CalendarNotificationStore.MaximumInboxPerUser
            || value.Pending.Length > CalendarNotificationStore.MaximumInboxPerUser || value.Generation == Guid.Empty
            || value.Pending.Any(entry => entry is null || entry.EventId == Guid.Empty || entry.Attempts is < 0 or > MaximumAttempts)
            || value.Pending.Select(entry => entry.EventId).Distinct().Count() != value.Pending.Length) return false;
        Span<byte> key = stackalloc byte[32];
        return value.Secret.Length == 0 || (Convert.TryFromBase64String(value.Secret, key, out var bytes) && bytes == 32);
    }

    private async Task CommitAsync(Dictionary<Guid, WebhookUserState> users, CancellationToken ct)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new Document { Users = users });
        var encrypted = new byte[plaintext.Length + 28];
        try
        {
            if (encrypted.Length > MaximumFileBytes) throw new InvalidOperationException("Webhook state capacity reached.");
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
        public int Version { get; init; } = 1;
        public Dictionary<Guid, WebhookUserState> Users { get; init; } = [];
    }
}
