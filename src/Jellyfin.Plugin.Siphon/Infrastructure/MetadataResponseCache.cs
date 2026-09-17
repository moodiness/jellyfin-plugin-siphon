using System.Text.Json;

namespace Jellyfin.Plugin.Siphon.Infrastructure;

/// <summary>Best-effort typed response storage, bounded to 4096 entries and 128 MiB.</summary>
public sealed class MetadataResponseCache(SiphonPaths paths) : IDisposable
{
    private const int MaxEntryBytes = 4 * 1024 * 1024;
    private const long MaxBytes = 128L * 1024 * 1024;
    private const int MaxEntries = 4096;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 48, RespectNullableAnnotations = true };
    private readonly string _directory = Path.Combine(paths.DataDirectory, "metadata-cache-v1");
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly Dictionary<string, (long Bytes, DateTime Written)> _entries = new(StringComparer.Ordinal);
    private bool _indexed;
    private long _bytes;

    internal async Task<T?> ReadAsync<T>(string key, DateTimeOffset now, TimeSpan lifetime, CancellationToken ct) where T : class
        => (await ReadObservedAsync<T>(key, now, lifetime, ct).ConfigureAwait(false))?.Value;

    internal async Task<(T Value, DateTimeOffset CreatedAt)?> ReadObservedAsync<T>(string key, DateTimeOffset now, TimeSpan lifetime, CancellationToken ct) where T : class
    {
        try
        {
            var path = FilePath(key);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxEntryBytes) return null;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 16384, true);
            var entry = await JsonSerializer.DeserializeAsync<Entry<T>>(stream, Json, ct).ConfigureAwait(false);
            return entry is not null && entry.Key == key && entry.CreatedAt <= now && now - entry.CreatedAt < lifetime
                ? (entry.Value, entry.CreatedAt) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    internal async Task<bool> WriteAsync<T>(string key, T value, DateTimeOffset now, CancellationToken ct) where T : class
    {
        byte[] bytes;
        try { bytes = JsonSerializer.SerializeToUtf8Bytes(new Entry<T>(key, now, value), Json); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { return false; }
        if (bytes.Length > MaxEntryBytes) return false;
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        var temporary = Path.Combine(_directory, "pending.tmp");
        try
        {
            Directory.CreateDirectory(_directory);
            if (!_indexed)
            {
                _entries.Clear();
                _bytes = 0;
                foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("*.json"))
                {
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetFileNameWithoutExtension(file.Name);
                    if (name.Length != 64 || name.Any(character => !char.IsAsciiHexDigit(character))) continue;
                    _entries[name] = (file.Length, file.LastWriteTimeUtc);
                    _bytes += file.Length;
                }
                _indexed = true;
            }
            var existing = _entries.GetValueOrDefault(key).Bytes;
            // Reserve room for the atomic temporary file as well as the replacement.
            while (_entries.Count >= MaxEntries || _bytes + bytes.Length > MaxBytes)
            {
                var oldest = _entries.MinBy(entry => entry.Value.Written);
                if (oldest.Key is null) return false;
                File.Delete(FilePath(oldest.Key));
                _entries.Remove(oldest.Key);
                _bytes -= oldest.Value.Bytes;
                if (oldest.Key == key) existing = 0;
            }
            await File.WriteAllBytesAsync(temporary, bytes, ct).ConfigureAwait(false);
            File.Move(temporary, FilePath(key), true);
            _bytes += bytes.Length - existing;
            _entries[key] = (bytes.Length, now.UtcDateTime);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _indexed = false; return false; }
        finally
        {
            try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _writer.Release();
        }
    }

    private string FilePath(string key) => Path.Combine(_directory, key + ".json");
    public void Dispose() => _writer.Dispose();
    private sealed record Entry<T>(string Key, DateTimeOffset CreatedAt, T Value);
}
