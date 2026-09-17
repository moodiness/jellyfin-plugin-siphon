using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Siphon.Configuration;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Siphon/Sync")]
public sealed class SiphonSyncController(ConfigurationAccessor configuration, ISiphonStateStore state,
    TargetedSyncRequestStore requests, ITaskManager tasks, SyncDiagnostics diagnostics) : ControllerBase
{
    [HttpGet("Targets")]
    public ActionResult<SyncTargets> GetTargets()
    {
        var catalogs = configuration.Current.Addons.Where(addon => addon.Enabled).SelectMany(addon => addon.Catalogs
            .Where(catalog => catalog.Enabled).Select(catalog => new CatalogTarget(SyncTarget.CatalogIdentity(addon.Id, catalog.Key),
                SyncDiagnostics.SafeName(addon.DisplayName + " / " + catalog.Id), catalog.Type is "movie" or "series" or "anime" ? catalog.Type : "other",
                Guid.TryParse(addon.Id, out var installation) ? installation.ToString("N") : null,
                Guid.TryParse(catalog.Key, out var subscription) ? subscription.ToString("N") : null))).ToArray();
        var series = state.GetReadSnapshot().Items.Where(item => item.Type == "series" && !item.IsSearchPreview).GroupBy(item => item.ContentKey, StringComparer.Ordinal)
            .Select(group => new SeriesTarget(group.Key, SyncDiagnostics.SafeName(group.First().SeriesName ?? group.First().Name)))
            .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        return new SyncTargets(catalogs, series);
    }

    [HttpPost("Target")]
    [RequestSizeLimit(4096)]
    public ActionResult StartTarget([FromBody] SyncTarget target)
    {
        try { target.Validate(configuration.Current, state.GetReadSnapshot()); }
        catch (ArgumentException) { return BadRequest(new SyncError("InvalidTarget", "Select exactly one catalog or series from the target list.")); }
        catch (KeyNotFoundException exception) { return NotFound(new SyncError("TargetNotFound", exception.Message)); }
        var worker = tasks.ScheduledTasks.FirstOrDefault(task => task.ScheduledTask is TargetedSyncTask);
        if (worker is null) return StatusCode(503, new SyncError("TaskUnavailable", "The native targeted sync task is unavailable. Restart Jellyfin after installing Siphon."));
        if (worker.State != TaskState.Idle || !requests.TryQueue(target))
            return Conflict(new SyncError("TargetBusy", "A targeted refresh is queued or running. Wait for it to finish or cancel it in Scheduled Tasks."));
        try { tasks.QueueScheduledTask<TargetedSyncTask>(); }
        catch
        {
            requests.Complete();
            throw;
        }
        return Accepted(new { TaskKey = "SiphonTargetedSync" });
    }

    [HttpGet("History")]
    public ActionResult<IReadOnlyList<SyncRunSnapshot>> GetHistory() => Ok(diagnostics.GetHistory());

    [HttpGet("History/{id}")]
    public ActionResult<SyncDecisionPage> GetHistoryDetails(string id, [FromQuery] int startIndex = 0, [FromQuery] int limit = 100)
    {
        if (startIndex is < 0 or > 100000 || limit is < 1 or > 200)
            return BadRequest(new SyncError("InvalidPage", "Use startIndex 0–100000 and limit 1–200."));
        var page = diagnostics.GetHistoryDetails(id, startIndex, limit);
        return page is null ? NotFound(new SyncError("RunNotFound", "The run is missing or has aged out of the twenty-run history.")) : Ok(page);
    }

    public sealed record SyncTargets(IReadOnlyList<CatalogTarget> Catalogs, IReadOnlyList<SeriesTarget> Series);
    public sealed record CatalogTarget(string Key, string Name, string Type, string? InstallationId, string? CatalogId);
    public sealed record SeriesTarget(string ContentKey, string Name);
    public sealed record SyncError(string Code, string Message);
}
