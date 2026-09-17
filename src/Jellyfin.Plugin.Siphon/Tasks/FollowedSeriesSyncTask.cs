using Jellyfin.Plugin.Siphon.Identity;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Siphon.Tasks;

/// <summary>Refreshes followed series without catalog discovery or absence cleanup.</summary>
public sealed class FollowedSeriesSyncTask(CatalogSyncService sync) : IScheduledTask
{
    public string Name => "Refresh followed Siphon series";
    public string Key => "SiphonFollowedSeriesSync";
    public string Description => "Refresh started or favorited series and discover their new episodes without scanning catalogs.";
    public string Category => "Siphon";
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => sync.SynchronizeAsync(progress, cancellationToken, SyncKind.FollowedSeries);
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(2).Ticks
        };
    }
}
