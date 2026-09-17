using Jellyfin.Plugin.Siphon.Identity;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Siphon.Tasks;

/// <summary>An explicit native task; provider caches are bypassed without changing saved policy.</summary>
public sealed class MetadataRefreshTask(CatalogSyncService sync) : IScheduledTask
{
    public string Name => "Refresh all Siphon metadata";
    public string Key => "SiphonMetadataRefresh";
    public string Description => "Synchronize catalogs and refresh all metadata without reusing provider response caches.";
    public string Category => "Siphon";
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => sync.SynchronizeAsync(progress, cancellationToken, SyncKind.FullRefresh);
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
