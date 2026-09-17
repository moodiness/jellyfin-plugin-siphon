using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Siphon.Tasks;

public sealed class TargetedSyncTask(CatalogSyncService sync, TargetedSyncRequestStore requests) : IScheduledTask
{
    public string Name => "Refresh one Siphon catalog or series";
    public string Key => "SiphonTargetedSync";
    public string Description => "Refreshes the target selected in Siphon settings. Other titles and ownership are preserved.";
    public string Category => "Siphon";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var target = requests.BeginExecution()
            ?? throw new InvalidOperationException("Choose a catalog or series in Siphon settings before starting a targeted refresh.");
        try { await sync.SynchronizeAsync(progress, cancellationToken, SyncKind.Targeted, target).ConfigureAwait(false); }
        finally { requests.Complete(); }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
