using Jellyfin.Plugin.Siphon.Identity;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Siphon.Tasks;

/// <summary>Jellyfin's scheduler owns task execution, cancellation and configurable triggers.</summary>
public sealed class CatalogSyncTask(CatalogSyncService sync) : IScheduledTask
{
    public string Name => "Synchronize Stremio catalogs";
    public string Key => "SiphonCatalogSync";
    public string Description => "Synchronize selected Stremio catalogs into the Siphon home channel.";
    public string Category => "Siphon";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => sync.SynchronizeAsync(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(12).Ticks
        };
    }
}
