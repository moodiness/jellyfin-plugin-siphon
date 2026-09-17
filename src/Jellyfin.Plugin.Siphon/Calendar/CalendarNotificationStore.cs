using System.Text.Json;
using Jellyfin.Plugin.Siphon.Infrastructure;

namespace Jellyfin.Plugin.Siphon.Calendar;

/// <summary>Commits event cursors and inbox together, before any best-effort client delivery.</summary>
public sealed class CalendarNotificationStore : IDisposable
{
    internal const int MaximumInboxPerUser = 200;
    internal const int MaximumInboxTotal = 10_000;
    private const int MaximumUsers = 1_000;
    private const int MaximumObservations = 100_000;
    private readonly string _path;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private NotificationDocument _document;

    public CalendarNotificationStore(SiphonPaths paths) : this(Path.Combine(paths.DataDirectory, "calendar-notifications.json")) { }

    internal CalendarNotificationStore(string path)
    {
        _path = path;
        if (!File.Exists(path)) { _document = new(); return; }
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("Calendar notification state exceeds its size limit.");
        using var stream = File.OpenRead(path);
        _document = JsonSerializer.Deserialize<NotificationDocument>(stream)
            ?? throw new InvalidDataException("Calendar notification state is empty.");
        if (_document.Version != 1 || _document.Users is null || _document.Users.Count > MaximumUsers
            || _document.Users.Any(pair => pair.Key == Guid.Empty || pair.Value is null || pair.Value.SeriesIds is null
                || pair.Value.Observations is null || pair.Value.Inbox is null
                || pair.Value.Observations.Length > FollowedCalendarService.MaximumEpisodes
                || pair.Value.SeriesIds.Length > FollowedCalendarService.MaximumEpisodes
                || pair.Value.Inbox.Length > MaximumInboxPerUser)
            || _document.Users.Values.Sum(value => value.Observations.Length + value.SeriesIds.Length) > MaximumObservations
            || _document.Users.Values.Sum(value => value.Inbox.Length) > MaximumInboxTotal)
            throw new InvalidDataException("Calendar notification state has an unsupported format or limits.");
    }

    public IReadOnlyList<CalendarNotification> Get(Guid userId)
        => Volatile.Read(ref _document).Users.TryGetValue(userId, out var value)
            ? value.Inbox.OrderByDescending(entry => entry.CreatedUtc).ThenBy(entry => entry.Id).ToArray() : [];

    internal async Task<IReadOnlyList<CalendarNotification>> ObserveAsync(Guid userId, FollowedCalendarSnapshot snapshot,
        bool enabled, DateTimeOffset now, CancellationToken ct)
    {
        if (userId == Guid.Empty || snapshot.Truncated) return [];
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _document;
            current.Users.TryGetValue(userId, out var previous);
            if (!enabled && previous is not { Enabled: true }) return [];
            var next = Transition(previous, snapshot, enabled, now, out var generated);
            if (ReferenceEquals(previous, next)) return [];
            var users = new Dictionary<Guid, NotificationUserState>(current.Users) { [userId] = next };
            Trim(users, userId);
            var document = new NotificationDocument { Users = users };
            await CommitAsync(document, ct).ConfigureAwait(false);
            // A global cap may have discarded old events, but never acknowledge events not retained.
            var retained = users[userId].Inbox.Select(entry => entry.Id).ToHashSet();
            return generated.Where(entry => retained.Contains(entry.Id)).ToArray();
        }
        finally { _writer.Release(); }
    }

    internal async Task SynchronizeRecipientsAsync(IReadOnlySet<Guid> existing, IReadOnlySet<Guid> enabled,
        DateTimeOffset now, CancellationToken ct)
    {
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_document.Users.Any(pair => !existing.Contains(pair.Key) || pair.Value.Enabled && !enabled.Contains(pair.Key))) return;
            var users = new Dictionary<Guid, NotificationUserState>(_document.Users);
            foreach (var pair in _document.Users)
            {
                if (!existing.Contains(pair.Key)) users.Remove(pair.Key);
                else if (pair.Value.Enabled && !enabled.Contains(pair.Key))
                    users[pair.Key] = pair.Value with { Enabled = false, Observations = [], SeriesIds = [], UpdatedUtc = now };
            }
            await CommitAsync(new() { Users = users }, ct).ConfigureAwait(false);
        }
        finally { _writer.Release(); }
    }

    public async Task<bool> MarkReadAsync(Guid userId, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_document.Users.TryGetValue(userId, out var user)) return false;
            var index = Array.FindIndex(user.Inbox, entry => entry.Id == id);
            if (index < 0) return false;
            if (user.Inbox[index].ReadUtc is not null) return true;
            var inbox = (CalendarNotification[])user.Inbox.Clone();
            inbox[index] = inbox[index] with { ReadUtc = now };
            var users = new Dictionary<Guid, NotificationUserState>(_document.Users) { [userId] = user with { Inbox = inbox } };
            await CommitAsync(new() { Users = users }, ct).ConfigureAwait(false);
            return true;
        }
        finally { _writer.Release(); }
    }

    internal static NotificationUserState Transition(NotificationUserState? previous, FollowedCalendarSnapshot snapshot,
        bool enabled, DateTimeOffset now, out IReadOnlyList<CalendarNotification> generated)
    {
        generated = [];
        if (!enabled)
            return (previous ?? new()) with { Enabled = false, Observations = [], SeriesIds = [], UpdatedUtc = now };
        // A missing episode can be temporarily inaccessible. Keep its cursor while
        // the series remains followed; only visible episodes below can emit events.
        var observations = previous?.Observations.Where(value => snapshot.SeriesIds.Contains(value.SeriesId))
            .ToDictionary(value => value.Id) ?? [];
        // As with a truncated snapshot, never evict deduplication history to make
        // room: leave the durable baseline intact until the followed set fits.
        if (observations.Count + snapshot.Episodes.Count(episode => !observations.ContainsKey(episode.Id))
            > FollowedCalendarService.MaximumEpisodes)
            return previous ?? new();
        var priorSeries = previous?.SeriesIds.ToHashSet() ?? [];
        var notifications = new List<CalendarNotification>();
        var changed = previous is not { Enabled: true } || !priorSeries.SetEquals(snapshot.SeriesIds);
        foreach (var episode in snapshot.Episodes)
        {
            observations.TryGetValue(episode.Id, out var prior);
            var released = episode.AnnouncedReleaseUtc <= now || prior is { Released: true }
                && prior.ReleaseUtc == episode.AnnouncedReleaseUtc && prior.IsDateOnly == episode.IsDateOnly;
            var observation = new EpisodeObservation(episode.Id, episode.SeriesId, episode.AnnouncedReleaseUtc,
                episode.IsDateOnly, released);
            observations[episode.Id] = observation;
            if (prior == observation) continue;
            changed = true;
            // The first opt-in snapshot and every newly followed series establish a
            // baseline. Old episodes are never replayed as a notification flood.
            if (previous is not { Enabled: true } || !priorSeries.Contains(episode.SeriesId)) continue;
            string? kind = null;
            DateTimeOffset? earlier = null;
            if (prior is null) kind = "NewEpisode";
            else if (prior.ReleaseUtc != observation.ReleaseUtc || prior.IsDateOnly != observation.IsDateOnly)
            {
                kind = "DateChanged";
                earlier = prior.ReleaseUtc;
            }
            else if (!prior.Released && observation.Released) kind = "AnnouncedRelease";
            if (kind is not null)
                notifications.Add(new(Guid.NewGuid(), kind, episode with { AnnouncedReleased = observation.Released }, now, null, earlier));
        }
        if (!changed && previous is not null) return previous;
        generated = notifications;
        return new()
        {
            Enabled = true,
            UpdatedUtc = now,
            SeriesIds = snapshot.SeriesIds.Order().ToArray(),
            Observations = observations.Values.OrderBy(value => value.Id).ToArray(),
            Inbox = (previous?.Inbox ?? []).Concat(notifications).OrderByDescending(entry => entry.CreatedUtc)
                .ThenBy(entry => entry.Id).Take(MaximumInboxPerUser).ToArray()
        };
    }

    private static void Trim(Dictionary<Guid, NotificationUserState> users, Guid currentUser)
    {
        var observations = users.Values.Sum(value => value.Observations.Length + value.SeriesIds.Length);
        foreach (var candidate in users.Where(pair => pair.Key != currentUser).OrderBy(pair => pair.Value.UpdatedUtc).ThenBy(pair => pair.Key).ToArray())
        {
            if (users.Count <= MaximumUsers && observations <= MaximumObservations) break;
            observations -= candidate.Value.Observations.Length + candidate.Value.SeriesIds.Length;
            // Evicted cursors are baselined again, not replayed on their next visit.
            users.Remove(candidate.Key);
        }
        var keep = users.Values.SelectMany(value => value.Inbox).OrderByDescending(entry => entry.CreatedUtc)
            .ThenBy(entry => entry.Id).Take(MaximumInboxTotal).Select(entry => entry.Id).ToHashSet();
        foreach (var pair in users.ToArray())
            if (pair.Value.Inbox.Any(entry => !keep.Contains(entry.Id)))
                users[pair.Key] = pair.Value with { Inbox = pair.Value.Inbox.Where(entry => keep.Contains(entry.Id)).ToArray() };
    }

    private async Task CommitAsync(NotificationDocument document, CancellationToken ct)
    {
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
                await JsonSerializer.SerializeAsync(stream, document, cancellationToken: ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
            Volatile.Write(ref _document, document);
        }
        finally { File.Delete(temporary); }
    }

    public void Dispose() => _writer.Dispose();

    private sealed record NotificationDocument
    {
        public int Version { get; init; } = 1;
        public Dictionary<Guid, NotificationUserState> Users { get; init; } = [];
    }

    internal sealed record NotificationUserState
    {
        public bool Enabled { get; init; }
        public DateTimeOffset UpdatedUtc { get; init; }
        public Guid[] SeriesIds { get; init; } = [];
        public EpisodeObservation[] Observations { get; init; } = [];
        public CalendarNotification[] Inbox { get; init; } = [];
    }

    internal sealed record EpisodeObservation(Guid Id, Guid SeriesId, DateTimeOffset ReleaseUtc, bool IsDateOnly, bool Released);
}
