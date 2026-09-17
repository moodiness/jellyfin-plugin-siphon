using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Siphon.Infrastructure;

internal sealed record MetadataQuotaState(DateTimeOffset PausedUntilUtc = default, long? Limit = null,
    long? Remaining = null, DateTimeOffset? ResetUtc = null, DateTimeOffset? ObservedAtUtc = null)
{
    internal static readonly MetadataQuotaState Empty = new();

    internal MetadataQuotaState Merge(MetadataQuotaState response)
    {
        var state = response.ObservedAtUtc.HasValue ? response : this;
        var until = response.PausedUntilUtc > PausedUntilUtc ? response.PausedUntilUtc : PausedUntilUtc;
        return state.PausedUntilUtc == until ? state : state with { PausedUntilUtc = until };
    }
}

/// <summary>Provider-imposed deadlines and last reported quotas survive response-cache eviction and restart.</summary>
public sealed class MetadataQuotaStore
{
    private const long MaximumFileBytes = 1024 * 1024;
    private const int MaximumRetainedObservations = 256;
    private static readonly TimeSpan SaveRetryDelay = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger<MetadataQuotaStore> _logger;
    private readonly Dictionary<string, MetadataQuotaState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _currentKeys = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private bool _dirty;
    private DateTimeOffset _nextSaveAttempt;

    public MetadataQuotaStore(SiphonPaths paths, ILogger<MetadataQuotaStore> logger, TimeProvider? timeProvider = null)
    {
        _path = Path.Combine(paths.DataDirectory, "metadata-quotas.json");
        _logger = logger;
        _clock = timeProvider ?? TimeProvider.System;
        var now = _clock.GetUtcNow();
        try
        {
            if (!File.Exists(_path)) return;
            using var stream = File.OpenRead(_path);
            if (stream.Length > MaximumFileBytes) throw new InvalidDataException();
            using var saved = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 4 });
            if (saved.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            foreach (var entry in saved.RootElement.EnumerateObject())
            {
                // The original format mapped credential digests directly to deadlines. Upgrade on the next write.
                if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.TryGetDateTimeOffset(out var legacyUntil))
                {
                    if (legacyUntil > now) _states[entry.Name] = new(legacyUntil);
                    continue;
                }
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                var until = Date(entry.Value, "PausedUntilUtc") ?? default;
                var observed = Date(entry.Value, "ObservedAtUtc");
                var limit = Number(entry.Value, "Limit");
                var remaining = Number(entry.Value, "Remaining");
                var reset = Date(entry.Value, "ResetUtc");
                if (remaining > limit) remaining = null;
                if (reset < DateTimeOffset.UnixEpoch) reset = null;
                if (observed is null || observed > now || observed < DateTimeOffset.UnixEpoch
                    || (limit is null && remaining is null && reset is null))
                {
                    // Invalid observations must never invalidate a separately persisted hard deadline.
                    observed = null;
                    limit = remaining = null;
                    reset = null;
                }
                if (until > now || observed.HasValue) _states[entry.Name] = new(until, limit, remaining, reset, observed);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
        {
            _states.Clear();
            _logger.LogWarning("Siphon provider quota state could not be loaded; limits will be learned from the next provider response.");
        }
    }

    internal MetadataQuotaState Get(string provider, string credential)
    {
        lock (_gate)
        {
            var key = provider + ":" + credential;
            _currentKeys[provider] = key;
            var now = _clock.GetUtcNow();
            TrySave(now);
            return _states.GetValueOrDefault(key, MetadataQuotaState.Empty);
        }
    }

    internal MetadataQuotaState Record(string provider, string credential, MetadataQuotaState response)
    {
        lock (_gate)
        {
            var key = provider + ":" + credential;
            var previous = _states.GetValueOrDefault(key, MetadataQuotaState.Empty);
            var state = previous.Merge(response);
            var now = _clock.GetUtcNow();
            if (state != previous)
            {
                _states[key] = state;
                _dirty = true;
                Prune(provider, now, key);
            }
            // Even an identical response must retry a previously failed write after backoff.
            TrySave(now);
            return state;
        }
    }

    private void Prune(string provider, DateTimeOffset now, string retainedKey)
    {
        if (_states.Count <= MaximumRetainedObservations) return;
        var prefix = provider + ":";
        var currentKey = _currentKeys.GetValueOrDefault(provider);
        // Compact a provider only after it is accessed: another provider's current credential
        // is not known yet. Keep its stale observations until that credential can be protected.
        // Live hard deadlines are never part of the disposable observation history.
        var obsolete = _states.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal)
                && entry.Key != retainedKey && entry.Key != currentKey && entry.Value.PausedUntilUtc <= now)
            .OrderByDescending(entry => entry.Value.ObservedAtUtc)
            .Skip(MaximumRetainedObservations)
            .Select(entry => entry.Key).ToArray();
        foreach (var key in obsolete) _states.Remove(key);
        if (obsolete.Length != 0) _dirty = true;
    }

    private void TrySave(DateTimeOffset now)
    {
        if (!_dirty || now < _nextSaveAttempt) return;
        foreach (var current in _currentKeys) Prune(current.Key, now, current.Value);
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(stream, _states);
                // Never publish a file that the loader would reject. If active deadlines alone
                // exceed the bound, retain the previous durable file and all in-memory deadlines.
                if (stream.Length > MaximumFileBytes) throw new InvalidDataException();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
            _dirty = false;
            _nextSaveAttempt = default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
        {
            _nextSaveAttempt = now + SaveRetryDelay;
            _logger.LogWarning("Siphon provider quota state could not be saved; durability across restart is not guaranteed. Persistence will be retried on later provider activity.");
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static DateTimeOffset? Date(JsonElement entry, string name)
        => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.TryGetDateTimeOffset(out var date) ? date : null;

    private static long? Number(JsonElement entry, string name)
        => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number) && number >= 0 ? number : null;
}
