using System.Text.Json;
using Jellyfin.Plugin.Siphon.Configuration;

namespace Jellyfin.Plugin.Siphon.Infrastructure;

public sealed record UserPreferences
{
    public string SearchMode { get; init; } = "Inherit";
    public bool? HideUnreleased { get; init; }
    public bool NotificationsEnabled { get; init; }
}

/// <summary>User preferences never mutate shared addon credentials or invalidate playback leases.</summary>
public sealed class UserPreferenceStore : IDisposable
{
    private const int MaximumUsers = 4096;
    private static readonly UserPreferences Defaults = new();
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 8, RespectNullableAnnotations = true };
    private readonly string _path;
    private readonly ConfigurationAccessor _configuration;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private Dictionary<Guid, UserPreferences> _snapshot;

    public UserPreferenceStore(SiphonPaths paths, ConfigurationAccessor configuration)
    {
        _path = Path.Combine(paths.DataDirectory, "user-preferences.json");
        _configuration = configuration;
        if (!File.Exists(_path))
        {
            _snapshot = [];
            return;
        }
        if (new FileInfo(_path).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Siphon user preferences exceed the storage limit.");
        using var stream = File.OpenRead(_path);
        var document = JsonSerializer.Deserialize<Document>(stream, Json)
            ?? throw new InvalidDataException("Siphon user preferences are corrupt.");
        if (document.Version != 1 || document.Users is null || document.Users.Count > MaximumUsers)
            throw new InvalidDataException("Siphon user preferences have an unsupported format.");
        foreach (var (userId, value) in document.Users) Validate(userId, value);
        _snapshot = document.Users;
    }

    public UserPreferences Get(Guid userId) => Volatile.Read(ref _snapshot).GetValueOrDefault(userId, Defaults);

    public bool UsesLocalSearch(Guid userId)
    {
        var mode = Get(userId).SearchMode;
        return (mode == "Inherit" ? _configuration.Current.DefaultSearchMode : mode) == "Local";
    }

    public bool HidesUnreleased(Guid userId) => Get(userId).HideUnreleased ?? _configuration.Current.HideUnreleased;

    public async Task SaveAsync(Guid userId, UserPreferences value, CancellationToken ct)
    {
        Validate(userId, value);
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var current = Volatile.Read(ref _snapshot);
            if (current.GetValueOrDefault(userId, Defaults) == value) return;
            var next = new Dictionary<Guid, UserPreferences>(current);
            if (value == Defaults) next.Remove(userId);
            else next[userId] = value;
            if (next.Count > MaximumUsers)
                throw new InvalidOperationException("Siphon has reached its user preference storage limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
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
                await JsonSerializer.SerializeAsync(stream, new Document { Users = next }, Json, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
            Volatile.Write(ref _snapshot, next);
        }
        finally
        {
            try { if (temporary is not null) File.Delete(temporary); }
            finally { _writer.Release(); }
        }
    }

    private static void Validate(Guid userId, UserPreferences value)
    {
        if (userId == Guid.Empty || value is null || value.SearchMode is not ("Inherit" or "All" or "Local"))
            throw new ArgumentException("Choose Inherit, All or Local search for a valid user.");
    }

    public void Dispose() => _writer.Dispose();

    private sealed class Document
    {
        public int Version { get; init; } = 1;
        public Dictionary<Guid, UserPreferences> Users { get; init; } = [];
    }
}
