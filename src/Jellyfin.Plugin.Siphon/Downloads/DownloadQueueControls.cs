namespace Jellyfin.Plugin.Siphon.Downloads;

public sealed partial class DownloadQueueService
{
    internal static DownloadJob[] ScheduleCandidates(IEnumerable<DownloadJob> jobs, Guid lastUser)
        => jobs.Where(job => !job.DeleteRequested && job.State == DownloadJobState.Queued && job.ReservedBytes == 0)
            .GroupBy(job => job.UserId).Select(group => group.OrderByDescending(job => job.Priority).ThenBy(job => job.CreatedUtc).ThenBy(job => job.Id).First())
            .OrderBy(job => job.UserId.CompareTo(lastUser) > 0 ? 0 : 1).ThenBy(job => job.UserId).ToArray();

    internal async Task<DownloadJobSummary> PauseAsync(Guid userId, Guid id, CancellationToken ct)
    {
        Task? completion = null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var job = Require(userId, id);
            if (!Enabled || !Authorized(job)) throw new DownloadQueueException(403, "Download permission or the playback profile changed.");
            if (job.State is not (DownloadJobState.Queued or DownloadJobState.Running or DownloadJobState.Paused))
                throw new DownloadQueueException(409, "Only queued or running downloads can be paused.");
            store.Update(id, current => current with
            {
                State = DownloadJobState.Paused,
                Phase = "Waiting",
                BytesPerSecond = null,
                EstimatedSecondsRemaining = null,
                UpdatedUtc = DateTimeOffset.UtcNow
            });
            if (_active.TryGetValue(id, out var active))
            {
                active.Cancellation.Cancel();
                completion = active.Completion;
            }
        }
        finally { _gate.Release(); }
        // The durable pause precedes cancellation; a disconnected caller cannot undo it.
        if (completion is not null) await completion.WaitAsync(ct).ConfigureAwait(false);
        return Summary(Require(userId, id), true);
    }

    internal async Task<DownloadJobSummary> ResumeAsync(Guid userId, Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var job = Require(userId, id);
            if (!Enabled || !Authorized(job)) throw new DownloadQueueException(403, "Download permission or the playback profile changed.");
            if (job.State != DownloadJobState.Paused || _active.ContainsKey(id) || job.ReservedBytes != 0)
                throw new DownloadQueueException(409, "Wait for the paused writer to stop before resuming.");
            CheckAdmission(job);
            return Summary(store.Update(id, current => current with
            {
                State = DownloadJobState.Queued,
                Phase = "Waiting",
                Error = null,
                BytesPerSecond = null,
                EstimatedSecondsRemaining = null,
                UpdatedUtc = DateTimeOffset.UtcNow
            })!, true);
        }
        finally { _gate.Release(); }
    }

    internal async Task<DownloadJobSummary> SetPriorityAsync(Guid userId, Guid id, int priority, CancellationToken ct)
    {
        if (priority is < -10 or > 10) throw new DownloadQueueException(400, "Priority must be between -10 and 10.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var job = Require(userId, id);
            if (!Enabled || !Authorized(job)) throw new DownloadQueueException(403, "Download permission or the playback profile changed.");
            if (job.State is not (DownloadJobState.Queued or DownloadJobState.Paused or DownloadJobState.Running))
                throw new DownloadQueueException(409, "Priority can only change for pending downloads.");
            return Summary(store.Update(id, current => current with
            {
                Priority = priority,
                BytesPerSecond = null,
                EstimatedSecondsRemaining = null,
                UpdatedUtc = DateTimeOffset.UtcNow
            })!, true);
        }
        finally { _gate.Release(); }
    }

    private static void ValidateRequest(DownloadQueueRequest request)
    {
        if (request.Priority is < -10 or > 10 || !DownloadTransferOptions.ValidLanguages(request.AudioLanguages)
            || !DownloadTransferOptions.ValidLanguages(request.SubtitleLanguages))
            throw new DownloadQueueException(400, "Use a priority from -10 to 10 and at most 32 distinct advertised languages per track type.");
    }

    private void CheckAdmission(DownloadJob job)
    {
        if (FileLimit > UserStorageLimit || job.TotalBytes > FileLimit)
            throw new DownloadQueueException(409, "The file budget must fit the global and per-user limits, and the selected version must fit the file budget.", "DownloadQuotaExceeded");
        // Retained partial/final directories count even before the worker's startup reconciliation.
        foreach (var retained in store.Snapshot().Where(value => !_active.ContainsKey(value.Id)))
        {
            var measured = store.Files.Measure(retained);
            if (measured != retained.StoredBytes) store.Update(retained.Id, current => current with { StoredBytes = measured });
        }
        var jobs = store.Snapshot();
        var retainedBytes = jobs.FirstOrDefault(value => value.Id == job.Id)?.StoredBytes ?? 0;
        if (retainedBytes > FileLimit || jobs.Sum(DownloadQueueStore.CommittedBytes) - retainedBytes > StorageLimit - FileLimit
            || jobs.Where(value => value.UserId == job.UserId).Sum(DownloadQueueStore.CommittedBytes) - retainedBytes > UserStorageLimit - FileLimit)
            throw new DownloadQueueException(409, "There is not enough unreserved download storage. Remove retained downloads or wait for an active transfer.", "DownloadQuotaExceeded");
    }
}

/// <summary>A bounded ten-second receiving window; no speed is inferred from staging or a resume offset.</summary>
internal sealed class DownloadRateEstimator
{
    private readonly Queue<(long Time, long Bytes)> _samples = [];
    private string? _phase;
    private long _lastBytes;
    internal bool PhaseChanged { get; private set; }

    internal (double? Rate, double? Remaining) Observe(DownloadTransferProgress progress, long now)
    {
        PhaseChanged = _phase != progress.Phase;
        if (PhaseChanged || progress.BytesReceived < _lastBytes) _samples.Clear();
        _phase = progress.Phase;
        _lastBytes = progress.BytesReceived;
        if (progress.Phase != "Receiving") { _samples.Clear(); return (null, null); }
        while (_samples.Count > 1 && now - _samples.Peek().Time > 10_000) _samples.Dequeue();
        if (_samples.Count == 0 || now - _samples.Last().Time >= 250) _samples.Enqueue((now, progress.BytesReceived));
        if (_samples.Count < 2) return (null, null);
        var first = _samples.Peek();
        var elapsed = now - first.Time;
        if (elapsed < 1000 || progress.BytesReceived <= first.Bytes) return (null, null);
        var rate = (progress.BytesReceived - first.Bytes) * 1000d / elapsed;
        return (rate, progress.TotalBytes is { } total && total >= progress.BytesReceived
            ? (total - progress.BytesReceived) / rate : null);
    }
}
