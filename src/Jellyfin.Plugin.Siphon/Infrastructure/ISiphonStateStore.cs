using Jellyfin.Plugin.Siphon.Identity;

namespace Jellyfin.Plugin.Siphon.Infrastructure;

/// <summary>Persistent ownership and identity index. Contains no upstream stream URLs.</summary>
public interface ISiphonStateStore
{
    IReadOnlyList<ManagedItem> GetItems();
    ManagedItem? FindByKey(string key);
    ManagedItem? FindByPath(string path);
    ManagedItem? FindByContentKey(string contentKey);
    Task SaveAsync(IReadOnlyList<ManagedItem> items, CancellationToken cancellationToken);
}
