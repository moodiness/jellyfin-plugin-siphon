using Jellyfin.Plugin.Siphon.Identity;

namespace Jellyfin.Plugin.Siphon.Infrastructure;

/// <summary>Persistent ownership and identity index. Contains no upstream stream URLs.</summary>
public interface ISiphonStateStore
{
    /// <summary>Read-only catalog. Implementations without revision caching return an isolated snapshot.</summary>
    SiphonReadSnapshot GetReadSnapshot() => SiphonReadSnapshot.CopyOf(GetItems());
    ManagedItemSnapshot? ReadByKey(string key) => FindByKey(key) is { } item ? ManagedItemSnapshot.CopyOf(item) : null;
    ManagedItemSnapshot? ReadByPath(string path) => FindByPath(path) is { } item ? ManagedItemSnapshot.CopyOf(item) : null;
    ManagedItemSnapshot? ReadByContentKey(string contentKey) => FindByContentKey(contentKey) is { } item ? ManagedItemSnapshot.CopyOf(item) : null;

    /// <summary>Isolated mutable working copies for synchronization, publication and recovery.</summary>
    IReadOnlyList<ManagedItem> GetItems();
    ManagedItem? FindByKey(string key);
    ManagedItem? FindByPath(string path);
    ManagedItem? FindByContentKey(string contentKey);
    Task SaveAsync(IReadOnlyList<ManagedItem> items, CancellationToken cancellationToken);
}
