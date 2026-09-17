using Jellyfin.Plugin.Siphon.Identity;

namespace Jellyfin.Plugin.Siphon.Infrastructure;

/// <summary>An immutable catalog and its process-local commit revision, captured atomically.</summary>
public sealed class SiphonReadSnapshot
{
    private readonly Dictionary<string, ManagedItemSnapshot> _keys;
    private readonly Dictionary<string, ManagedItemSnapshot> _paths;
    private readonly Dictionary<string, ManagedItemSnapshot> _contents;

    internal SiphonReadSnapshot(ManagedItem[] owned, long? revision)
    {
        Revision = revision;
        var items = new ManagedItemSnapshot[owned.Length];
        _keys = new(owned.Length, StringComparer.Ordinal);
        _paths = new(owned.Length, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        _contents = new(StringComparer.Ordinal);
        for (var index = 0; index < owned.Length; index++)
        {
            var item = new ManagedItemSnapshot(owned[index]);
            items[index] = item;
            if (!_keys.TryAdd(item.Key, item) || !_paths.TryAdd(Path.GetFullPath(item.Path), item))
                throw new InvalidDataException("Siphon state contains duplicate identities or paths.");
            _contents.TryAdd(item.ContentKey, item);
        }
        Items = Array.AsReadOnly(items);
    }

    /// <summary>Null when an alternate store does not supply a stable commit revision.</summary>
    public long? Revision { get; }
    public IReadOnlyList<ManagedItemSnapshot> Items { get; }
    public ManagedItemSnapshot? FindByKey(string key) => _keys.GetValueOrDefault(key);
    public ManagedItemSnapshot? FindByPath(string path) => _paths.GetValueOrDefault(Path.GetFullPath(path));
    public ManagedItemSnapshot? FindByContentKey(string contentKey) => _contents.GetValueOrDefault(contentKey);

    /// <summary>Safe fallback for alternate stores; only the persistent store caches by revision.</summary>
    public static SiphonReadSnapshot CopyOf(IReadOnlyList<ManagedItem> items)
        => new(items.Select(SiphonStateStore.Clone).ToArray(), null);
}
