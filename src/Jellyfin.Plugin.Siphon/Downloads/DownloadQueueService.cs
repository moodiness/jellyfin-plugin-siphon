using System.Security.Cryptography;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Playback;
using Jellyfin.Plugin.Siphon.Protocol;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Siphon.Downloads;

/// <summary>One worker owns scheduling, revocation and quota reservations; downloads never outlive their authority.</summary>
public sealed class DownloadQueueService(DownloadQueueStore store, IDownloadTransfer transfer, ConfigurationAccessor configuration,
    PlaybackAccess access, IUserManager users, ISiphonStateStore state, StreamResolver resolver,
    IHostApplicationLifetime lifetime, ILogger<DownloadQueueService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, ActiveJob> _active = [];
    private readonly Dictionary<string, Ticket> _tickets = new(StringComparer.Ordinal);
    private Guid _lastUser;
    private DateTimeOffset _nextMaintenance;
    private bool _reconciled;
    private sealed class ActiveJob(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Completion { get; set; } = Task.CompletedTask;
    }
    private sealed record Ticket(Guid JobId, Guid UserId, DateTimeOffset UpdatedUtc, DateTimeOffset ExpiresUtc)
    {
        private int _revoked;
        public bool Revoked => Volatile.Read(ref _revoked) != 0;
        public void Revoke() => Interlocked.Exchange(ref _revoked, 1);
    }
    private int ConcurrentLimit => Math.Clamp(configuration.Current.DownloadMaxConcurrentJobs, 1, 16);
    private long StorageLimit => Math.Clamp((long)configuration.Current.DownloadMaxStorageMiB, 1, 16 * 1024 * 1024) * 1024 * 1024;
    private long FileLimit => Math.Clamp((long)configuration.Current.DownloadMaxFileMiB, 1, 16 * 1024 * 1024) * 1024 * 1024;
    private bool Enabled => configuration.Current.EnableDownloadQueue;
    private string TransportKey => AddonRegistry.Digest(configuration.Current.PublicBaseUrl + "\n"
        + string.Join('\n', configuration.Current.AllowedPrivateHosts.Order(StringComparer.OrdinalIgnoreCase)));

    internal bool Allowed(Guid userId)
    {
        var user = users.GetUserById(userId);
        return user is not null && !user.HasPermission(PermissionKind.IsDisabled)
            && user.HasPermission(PermissionKind.EnableContentDownloading)
            && (user.HasPermission(PermissionKind.IsAdministrator) || user.IsParentalScheduleAllowed());
    }

    internal bool Authorized(DownloadJob job)
        => Allowed(job.UserId) && job.ProfileKey == AddonRegistry.PlaybackKey(configuration.Current, job.UserId)
            && job.TransportKey == TransportKey
            && access.GetVideo(job.ItemId, job.UserId, download: true) is { } item && item.GetProviderId("Siphon") == job.ItemKey
            && access.GetVideo(job.MediaSourceId, job.UserId, download: true) is { } selected
            && selected.GetProviderId("Siphon") == job.ItemKey && selected.GetProviderId(NativeVersionService.SourceProvider) == job.SourceId
            && selected.GetProviderId(NativeVersionService.OwnerProvider) == job.UserId.ToString("N")
            && state.FindByKey(job.ItemKey) is not null;

    internal DownloadQueueResponse List(Guid userId)
    {
        var enabled = Enabled;
        var allowed = Allowed(userId);
        var jobs = store.Snapshot().Where(job => job.UserId == userId && !job.DeleteRequested).OrderByDescending(job => job.CreatedUtc).ToArray();
        return new(enabled, allowed, jobs.Select(job => Summary(job, enabled && allowed && Authorized(job))).ToArray(),
            jobs.Sum(job => job.StoredBytes), StorageLimit);
    }

    internal async Task<DownloadJobSummary> EnqueueAsync(Guid userId, DownloadQueueRequest request, CancellationToken ct)
    {
        if (!Enabled || !Allowed(userId)) throw new DownloadQueueException(403, "Server downloads are not enabled for this account.");
        if (!Guid.TryParse(request.MediaSourceId, out var selectedId) || selectedId == Guid.Empty
            || access.GetVideo(request.ItemId, userId, download: true) is not { } item || item.GetProviderId("Siphon") is not { } key
            || state.FindByKey(key) is not { } managed
            // Reject foreign native sources before contacting any addon, including an administrator's request.
            || access.GetVideo(selectedId, userId, download: true) is not { } selected || selected.GetProviderId("Siphon") != key
            || selected.GetProviderId(NativeVersionService.OwnerProvider) != userId.ToString("N")
            || selected.GetProviderId(NativeVersionService.SourceProvider) is not { } sourceId)
            throw new DownloadQueueException(404, "The selected version is unavailable.");
        var profile = AddonRegistry.PlaybackKey(configuration.Current, userId);
        var transport = TransportKey;
        var source = (await resolver.GetSourcesAsync(managed, userId, ct).ConfigureAwait(false))
            .FirstOrDefault(candidate => candidate.Id == sourceId && candidate.UserId == userId);
        if (source is null) throw new DownloadQueueException(409, "The selected version is no longer available. Choose a version again.");
        var now = DateTimeOffset.UtcNow;
        var job = new DownloadJob
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ItemId = item.Id,
            MediaSourceId = selectedId,
            ItemKey = key,
            SourceId = source.Id,
            ProfileKey = profile,
            TransportKey = transport,
            StorageKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            Name = new string(item.Name.Where(character => !char.IsControl(character)).Take(256).ToArray()),
            State = DownloadJobState.Queued,
            CreatedUtc = now,
            UpdatedUtc = now,
            TotalBytes = source.Size is >= 0 ? source.Size : null
        };
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Enabled || !Authorized(job)) throw new DownloadQueueException(403, "Download permission or the playback profile changed.");
            if (job.TotalBytes > Math.Min(FileLimit, StorageLimit)) throw new DownloadQueueException(409, "This version exceeds the configured download storage limit.");
            return Summary(store.Add(job, Math.Clamp(configuration.Current.DownloadMaxJobsPerUser, 1, 200)), true);
        }
        finally { _gate.Release(); }
    }

    internal async Task CancelAsync(Guid userId, Guid id, bool delete, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var job = Require(userId, id);
            if (!delete && job.State == DownloadJobState.Completed) throw new DownloadQueueException(409, "Delete a completed download to remove its file.");
            store.Update(id, current => current with
            {
                State = DownloadJobState.Cancelled,
                DeleteRequested = delete,
                ReservedBytes = 0,
                UpdatedUtc = DateTimeOffset.UtcNow,
                Error = null
            });
            RevokeTickets(id);
            if (_active.TryGetValue(id, out var active)) active.Cancellation.Cancel();
            else Cleanup(store.Find(id, userId) ?? job with { DeleteRequested = delete, State = DownloadJobState.Cancelled, ReservedBytes = 0 });
        }
        finally { _gate.Release(); }
    }

    internal async Task<DownloadJobSummary> RetryAsync(Guid userId, Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var job = Require(userId, id);
            if (!Enabled || !Authorized(job)) throw new DownloadQueueException(403, "Download permission or the playback profile changed. Select the version again.");
            if (job.State is not (DownloadJobState.Failed or DownloadJobState.Cancelled) || _active.ContainsKey(id))
                throw new DownloadQueueException(409, "This download cannot be retried yet.");
            var updated = store.Update(id, current => current with { State = DownloadJobState.Queued, Error = null, UpdatedUtc = DateTimeOffset.UtcNow });
            return Summary(updated!, true);
        }
        finally { _gate.Release(); }
    }

    internal async Task<(string Token, DateTimeOffset Expires)> TicketAsync(Guid userId, Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var job = Require(userId, id);
            if (!Enabled || job.State != DownloadJobState.Completed || !Authorized(job)) throw new DownloadQueueException(404, "The completed download is unavailable.");
            using (store.Files.Open(job)) { }
            var now = DateTimeOffset.UtcNow;
            foreach (var key in _tickets.Where(pair => pair.Value.ExpiresUtc <= now).Select(pair => pair.Key).ToArray()) _tickets.Remove(key);
            if (_tickets.Count >= 4096 || _tickets.Values.Count(ticket => ticket.UserId == userId) >= 32)
                throw new DownloadQueueException(429, "Too many file tickets. Wait for an existing ticket to expire.");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var expires = now.AddMinutes(5);
            _tickets.Add(token, new(id, userId, job.UpdatedUtc, expires));
            return (token, expires);
        }
        finally { _gate.Release(); }
    }

    internal async Task<(Stream Stream, string ContentType, string FileName)?> OpenTicketAsync(string token, Guid? authenticatedUser, bool authenticated, CancellationToken ct)
    {
        if (token.Length != 64) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_tickets.TryGetValue(token, out var ticket) || authenticated && authenticatedUser != ticket.UserId
                || !ValidTicket(ticket)) return null;
            var job = store.Find(ticket.JobId, ticket.UserId)!;
            var file = store.Files.Open(job);
            return (new DownloadQueueReadStream(file, () => ValidTicket(ticket)), job.ContentType!, "siphon-download" + Path.GetExtension(job.FileName));
        }
        finally { _gate.Release(); }
    }

    private bool ValidTicket(Ticket ticket)
    {
        if (!ticket.Revoked && Enabled && ticket.ExpiresUtc > DateTimeOffset.UtcNow
            && store.Find(ticket.JobId, ticket.UserId) is { State: DownloadJobState.Completed } job
            && job.UpdatedUtc == ticket.UpdatedUtc && Authorized(job)) return true;
        ticket.Revoke();
        return false;
    }
    private DownloadJob Require(Guid userId, Guid id) => store.Find(id, userId) ?? throw new DownloadQueueException(404, "Download not found.");
    private static DownloadJobSummary Summary(DownloadJob job, bool allowed)
        => new(job.Id, job.ItemId, job.MediaSourceId.ToString("N"), job.Name, job.State.ToString(), job.BytesReceived, job.TotalBytes,
            job.CreatedUtc, job.UpdatedUtc, job.Error, allowed && job.State is DownloadJobState.Failed or DownloadJobState.Cancelled);
    private void RevokeTickets(Guid id)
    {
        foreach (var key in _tickets.Where(pair => pair.Value.JobId == id).Select(pair => pair.Key).ToArray())
        {
            _tickets[key].Revoke();
            _tickets.Remove(key);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        store.Recover();
        try
        {
            if (!lifetime.ApplicationStarted.IsCancellationRequested)
            {
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = lifetime.ApplicationStarted.Register(() => ready.TrySetResult());
                await ready.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await TickAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch { logger.LogWarning("Siphon download queue could not update its private storage."); }
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            Task[] active;
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                foreach (var job in _active.Values) job.Cancellation.Cancel();
                active = _active.Values.Select(job => job.Completion).ToArray();
                foreach (var ticket in _tickets.Values) ticket.Revoke();
                _tickets.Clear();
            }
            finally { _gate.Release(); }
            await Task.WhenAll(active).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(CancellationToken stoppingToken)
    {
        await _gate.WaitAsync(stoppingToken).ConfigureAwait(false);
        try
        {
            var jobs = store.Snapshot();
            foreach (var job in jobs.Where(job => _active.ContainsKey(job.Id)))
            {
                if (!Enabled) Interrupt(job, DownloadJobState.Queued, null);
                else if (!Authorized(job)) Interrupt(job, DownloadJobState.Failed, "Download permission or the playback profile changed.");
                else if (job.ReservedBytes > FileLimit) Interrupt(job, DownloadJobState.Failed, "The download storage limit was reduced.");
            }
            foreach (var key in _tickets.Where(pair => !ValidTicket(pair.Value)).Select(pair => pair.Key).ToArray()) _tickets.Remove(key);
            if (!Enabled) return;
            if (!_reconciled && _active.Count == 0)
            {
                // A crash can leave more partial bytes than the last persisted progress sample.
                // Account for every retained directory before granting a fresh reservation.
                foreach (var retained in jobs)
                {
                    var measured = store.Files.Measure(retained);
                    store.Update(retained.Id, current => current with { StoredBytes = measured });
                }
                _reconciled = true;
            }
            if (!_reconciled) return;
            foreach (var orphan in jobs.Where(job => job.State == DownloadJobState.Running && !_active.ContainsKey(job.Id)))
                store.Update(orphan.Id, current => current with { State = DownloadJobState.Queued, ReservedBytes = 0, UpdatedUtc = DateTimeOffset.UtcNow });
            var running = store.Snapshot().Where(job => job.State == DownloadJobState.Running).OrderBy(job => job.CreatedUtc).ToArray();
            var reserved = store.Snapshot().Sum(job => job.State == DownloadJobState.Running ? job.ReservedBytes : job.StoredBytes);
            for (var index = running.Length - 1; index >= 0 && (index >= ConcurrentLimit || reserved > StorageLimit); index--)
            {
                Interrupt(running[index], DownloadJobState.Failed, "The download queue limits were reduced.");
                reserved -= running[index].ReservedBytes - running[index].StoredBytes;
            }
            // Cancel is durable before the writer stops. Keep its reservation unavailable until it has actually exited.
            if (_active.Values.Any(active => active.Cancellation.IsCancellationRequested)) return;
            if (_nextMaintenance <= DateTimeOffset.UtcNow)
            {
                _nextMaintenance = DateTimeOffset.UtcNow.AddMinutes(1);
                var expiry = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(configuration.Current.DownloadRetentionDays, 1, 365));
                foreach (var job in store.Snapshot().Where(job => !_active.ContainsKey(job.Id)))
                {
                    if (job.State is DownloadJobState.Completed or DownloadJobState.Failed or DownloadJobState.Cancelled && job.UpdatedUtc < expiry)
                    {
                        store.Update(job.Id, current => current with { DeleteRequested = true });
                        Cleanup(job with { DeleteRequested = true });
                    }
                    else if (job.DeleteRequested || job.State == DownloadJobState.Cancelled) Cleanup(job);
                    else if (job.State == DownloadJobState.Completed) CompactCompleted(job);
                }
                foreach (var key in _tickets.Where(pair => pair.Value.ExpiresUtc <= DateTimeOffset.UtcNow).Select(pair => pair.Key).ToArray()) _tickets.Remove(key);
            }
            // Round-robin users, FIFO within each user; one busy account cannot occupy every future slot.
            var candidates = store.Snapshot().Where(job => !job.DeleteRequested && job.State == DownloadJobState.Queued)
                .GroupBy(job => job.UserId).Select(group => group.OrderBy(job => job.CreatedUtc).ThenBy(job => job.Id).First())
                .OrderBy(job => job.UserId.CompareTo(_lastUser) > 0 ? 0 : 1).ThenBy(job => job.UserId).ToArray();
            foreach (var job in candidates)
            {
                if (_active.Count >= ConcurrentLimit) break;
                if (!Authorized(job))
                {
                    FailQueued(job.Id, "Download permission or the playback profile changed.");
                    continue;
                }
                try
                {
                    var measured = store.Files.Measure(job);
                    store.Update(job.Id, current => current with { StoredBytes = measured });
                    if (measured > Math.Min(FileLimit, StorageLimit) || job.TotalBytes > Math.Min(FileLimit, StorageLimit))
                    {
                        FailQueued(job.Id, "This version exceeds the configured download storage limit.");
                        continue;
                    }
                    var started = store.TryStart(job.Id, ConcurrentLimit, StorageLimit, FileLimit);
                    if (started is null) continue;
                    _lastUser = job.UserId;
                    var active = new ActiveJob(CancellationTokenSource.CreateLinkedTokenSource(stoppingToken));
                    _active.Add(job.Id, active);
                    active.Completion = Task.Run(() => TransferAsync(started, active, stoppingToken), CancellationToken.None);
                }
                catch { FailQueued(job.Id, "The private download storage is unavailable."); }
            }
        }
        finally { _gate.Release(); }
    }

    private void FailQueued(Guid id, string error)
        => store.Update(id, job => job.State == DownloadJobState.Queued ? job with { State = DownloadJobState.Failed, Error = error, UpdatedUtc = DateTimeOffset.UtcNow } : null);
    private void Interrupt(DownloadJob job, DownloadJobState state, string? error)
    {
        if (job.State == DownloadJobState.Running)
            store.Update(job.Id, current => current.State == DownloadJobState.Running
                ? current with { State = state, ReservedBytes = 0, Error = error, UpdatedUtc = DateTimeOffset.UtcNow } : null);
        if (_active.TryGetValue(job.Id, out var active)) active.Cancellation.Cancel();
        RevokeTickets(job.Id);
    }

    private async Task TransferAsync(DownloadJob job, ActiveJob active, CancellationToken stoppingToken)
    {
        DownloadTransferResult? result = null;
        string? error = null;
        var lastProgress = DateTimeOffset.MinValue;
        try
        {
            var ct = active.Cancellation.Token;
            if (!Enabled || !Authorized(job)) throw new DownloadQueueException(403, "Download permission or the playback profile changed.");
            var managed = state.FindByKey(job.ItemKey) ?? throw new DownloadQueueException(404, "The selected version is unavailable.");
            resolver.Invalidate(job.ItemKey, job.UserId);
            var source = (await resolver.GetSourcesAsync(managed, job.UserId, ct).ConfigureAwait(false))
                .FirstOrDefault(candidate => candidate.Id == job.SourceId && candidate.UserId == job.UserId)
                ?? throw new DownloadQueueException(409, "The exact selected version is no longer available.");
            ct.ThrowIfCancellationRequested();
            if (!Enabled || !Authorized(job)) throw new DownloadQueueException(403, "Download permission or the playback profile changed.");
            var directory = store.Files.Prepare(job);
            result = await transfer.TransferAsync(source, directory, job.ReservedBytes, progress =>
            {
                ct.ThrowIfCancellationRequested();
                if (progress.StoredBytes < 0 || progress.StoredBytes > job.ReservedBytes || progress.BytesReceived < 0 || progress.TotalBytes is < 0)
                    throw new DownloadQueueException(409, "The transfer exceeded its reserved storage.");
                var now = DateTimeOffset.UtcNow;
                if (now - lastProgress >= TimeSpan.FromSeconds(1))
                {
                    if (!Enabled || !Authorized(job)) throw new DownloadQueueException(403, "Download permission or the playback profile changed.");
                    store.Update(job.Id, current => current.State == DownloadJobState.Running && !current.DeleteRequested
                        ? current with { BytesReceived = progress.BytesReceived, TotalBytes = progress.TotalBytes, StoredBytes = progress.StoredBytes, UpdatedUtc = now } : null);
                    lastProgress = now;
                }
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (active.Cancellation.IsCancellationRequested) { }
        catch (DownloadQueueException exception) { error = exception.Message; }
        catch { error = "The download failed. The selected source may be unavailable, unsupported, or over its storage limit."; }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var current = store.Snapshot().FirstOrDefault(value => value.Id == job.Id);
                if (current is not null)
                {
                    var stored = current.StoredBytes;
                    try { stored = store.Files.Measure(current); }
                    catch { _reconciled = false; error = "The private download storage is unavailable."; }
                    if (current.State == DownloadJobState.Running)
                    {
                        if (stoppingToken.IsCancellationRequested || !Enabled)
                            current = current with { State = DownloadJobState.Queued, Error = null };
                        else if (result is not null && error is null && !active.Cancellation.IsCancellationRequested && Authorized(current))
                        {
                            try
                            {
                                stored = store.Files.Finish(current, result);
                                current = current with
                                {
                                    State = DownloadJobState.Completed,
                                    BytesReceived = result.Bytes,
                                    TotalBytes = result.Bytes,
                                    FileName = result.FileName,
                                    ContentType = SafeContentType(result.ContentType),
                                    Error = null
                                };
                            }
                            catch { current = current with { State = DownloadJobState.Failed, Error = "The completed download could not be safely stored." }; }
                        }
                        else current = current with { State = DownloadJobState.Failed, Error = error ?? "Download permission or the playback profile changed." };
                    }
                    current = store.Update(job.Id, _ => current with { ReservedBytes = 0, StoredBytes = stored, UpdatedUtc = DateTimeOffset.UtcNow })!;
                    if (current.DeleteRequested || current.State == DownloadJobState.Cancelled) Cleanup(current);
                    else if (current.State == DownloadJobState.Completed) CompactCompleted(current);
                }
            }
            catch
            {
                _reconciled = false;
                logger.LogWarning("Siphon download state could not be committed; retained storage will be checked before scheduling.");
            }
            finally
            {
                _active.Remove(job.Id);
                active.Cancellation.Dispose();
                _gate.Release();
            }
        }
    }

    private static string SafeContentType(string value)
        => value is "video/mp4" or "video/x-matroska" or "video/webm" ? value : "application/octet-stream";
    private void CompactCompleted(DownloadJob job)
    {
        try
        {
            var stored = store.Files.RetainCompleted(job);
            if (stored != job.StoredBytes) store.Update(job.Id, current => current with { StoredBytes = stored });
        }
        catch { logger.LogWarning("Siphon completed download staging could not be safely removed."); }
    }

    private void Cleanup(DownloadJob job)
    {
        try
        {
            store.Files.Delete(job);
            if (job.DeleteRequested) store.RemoveDeleted(job.Id);
            else if (job.StoredBytes != 0) store.Update(job.Id, current => current with { StoredBytes = 0 });
            RevokeTickets(job.Id);
        }
        catch { logger.LogWarning("Siphon retained download storage could not be safely removed."); }
    }
}
