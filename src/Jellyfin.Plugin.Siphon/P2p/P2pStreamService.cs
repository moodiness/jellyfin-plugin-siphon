using System.Net;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using MonoTorrent.Client;

namespace Jellyfin.Plugin.Siphon.P2p;

/// <summary>One isolated engine and scratch directory per reader: no tracker or peer state crosses users.</summary>
public sealed class P2pStreamService(ConfigurationAccessor configuration, SiphonPaths paths, SsrfPolicy policy, ILogger<P2pStreamService> logger) : BackgroundService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly string _root = Path.Combine(paths.DataDirectory, "p2p");
    private bool _stopping;

    public async Task<P2pReadLease> OpenAsync(P2pSource source, Guid userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!configuration.Current.EnableP2p) throw new InvalidOperationException("P2P playback is disabled.");
        if (userId == Guid.Empty) throw new UnauthorizedAccessException("P2P playback requires an authenticated user.");
        var magnet = source.ToMagnet();
        Session session;
        lock (_gate)
        {
            var settings = Limits.Read(configuration.Current);
            if (_stopping || !settings.Enabled) throw new InvalidOperationException("P2P playback is disabled.");
            if (_sessions.Count >= settings.Streams) throw new InvalidOperationException("The P2P concurrent stream limit has been reached.");
            P2pStorage.CheckPath(_root, _root);
            Directory.CreateDirectory(_root);
            var slot = Enumerable.Range(0, settings.Streams).First(index => _sessions.Values.All(active => active.Slot != index));
            session = new Session(Guid.NewGuid().ToString("N"), slot, settings, cancellationToken);
            session.Directory = Path.Combine(_root, "session-" + session.Id);
            Directory.CreateDirectory(session.Directory);
            File.Create(Path.Combine(session.Directory, ".siphon-p2p-v1")).Dispose();
            _sessions.Add(session.Id, session);
        }

        try
        {
            session.Network = new P2pNetwork(policy);
            using var metadataTimeout = CancellationTokenSource.CreateLinkedTokenSource(session.Cancellation.Token);
            metadataTimeout.CancelAfter(TimeSpan.FromSeconds(session.Limits.MetadataSeconds));
            await session.Network.PrepareAsync(source.Trackers, metadataTimeout.Token).ConfigureAwait(false);
            var dht = session.Limits.Dht && source.Trackers.Length == 0;
            var port = session.Limits.Port == 0 ? 0 : session.Limits.Port + session.Slot;
            var endpoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Any, port) };
            if (System.Net.Sockets.Socket.OSSupportsIPv6) endpoints["ipv6"] = new(IPAddress.IPv6Any, port);
            var settings = new EngineSettingsBuilder
            {
                AllowedEncryption = [MonoTorrent.Connections.EncryptionType.PlainText],
                CacheDirectory = session.Directory,
                AutoSaveLoadDhtCache = false,
                AutoSaveLoadFastResume = false,
                AutoSaveLoadMagnetLinkMetadata = false,
                AllowLocalPeerDiscovery = false,
                AllowPortForwarding = false,
                DhtEndPoint = dht ? new IPEndPoint(IPAddress.Any, 0) : null,
                ListenEndPoints = endpoints,
                MaximumConnections = 40,
                MaximumHalfOpenConnections = 8,
                MaximumOpenFiles = 8,
                MaximumDownloadRate = Math.Max(1, session.Limits.DownloadBytes / session.Limits.Streams),
                MaximumUploadRate = Math.Max(1, session.Limits.UploadBytes / session.Limits.Streams),
                DiskCacheBytes = 1024 * 1024,
                UsePartialFiles = false,
                WebSeedDelay = TimeSpan.MaxValue
            }.ToSettings();
            session.Engine = new ClientEngine(settings, session.Network.CreateFactories(Path.Combine(session.Directory, "content")));
            session.State = "Metadata";
            var metadataTask = session.Engine.DownloadMetadataAsync(magnet, metadataTimeout.Token);
            // The released API waits for a tracker stop announce during cancellation. Bound our caller independently.
            _ = metadataTask.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var bytes = await metadataTask.WaitAsync(metadataTimeout.Token).ConfigureAwait(false);
            if (bytes.Length > PeerWireGuard.MaximumMetadataBytes) throw new IOException("P2P metadata is too large.");
            PeerWireGuard.ValidateEncoding(bytes.Span);
            var torrent = Torrent.Load(bytes.Span);
            if ((magnet.InfoHashes.V1 is not null && !magnet.InfoHashes.V1.Equals(torrent.InfoHashes.V1))
                || (magnet.InfoHashes.V2 is not null && !magnet.InfoHashes.V2.Equals(torrent.InfoHashes.V2)))
                throw new IOException("P2P metadata hash does not match the requested source.");
            if (torrent.Files.Count is 0 or > 10000) throw new IOException("P2P torrent file count is not supported.");
            var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long reservation = 0;
            foreach (var file in torrent.Files)
            {
                P2pStorage.ValidateRelativePath(file.Path);
                if (!uniquePaths.Add(file.Path) || file.Length < 0) throw new IOException("P2P torrent contains conflicting file paths.");
                reservation = checked(reservation + file.Length);
            }
            var selectedSource = source.FileIndex is int index ? source with { FileIndex = P2pStorage.MapFileIndex(torrent, bytes.Span, index) } : source;
            var selection = P2pStorage.SelectFile(torrent.Files.Select(file => (file.Path, file.Length)).ToArray(), selectedSource);
            lock (_gate)
            {
                session.Cancellation.Token.ThrowIfCancellationRequested();
                var limit = Limits.Read(configuration.Current);
                if (!limit.Enabled || limit != session.Limits) throw new InvalidOperationException("P2P configuration changed. Retry playback.");
                var inactiveBytes = Directory.EnumerateFileSystemEntries(_root)
                    .Where(entry => !_sessions.Values.Any(active => active.Directory == entry))
                    .Sum(entry => Directory.Exists(entry) ? P2pStorage.Size(entry) : new FileInfo(entry).Length);
                if (reservation > limit.CacheBytes - inactiveBytes - _sessions.Values.Sum(active => active.ReservedBytes))
                    throw new IOException("The torrent exceeds the available P2P disk reservation. Clean inactive cache or raise the limit.");
                session.ReservedBytes = reservation;
            }

            session.Manager = await session.Engine.AddStreamingAsync(torrent, Path.Combine(session.Directory, "content"), new TorrentSettingsBuilder
            {
                AllowDht = dht && !torrent.IsPrivate,
                AllowPeerExchange = dht && !torrent.IsPrivate,
                AllowInitialSeeding = false,
                CreateContainingDirectory = false,
                MaximumConnections = 40
            }.ToSettings()).ConfigureAwait(false);
            foreach (var file in session.Manager.Files)
            {
                P2pStorage.CheckPath(session.Directory, file.FullPath);
                await session.Manager.SetFilePriorityAsync(file, Priority.DoNotDownload).ConfigureAwait(false);
            }

            var selected = session.Manager.Files[selection];
            await session.Manager.SetFilePriorityAsync(selected, Priority.Normal).ConfigureAwait(false);
            session.Cancellation.Token.ThrowIfCancellationRequested();
            await session.Manager.StartAsync().ConfigureAwait(false);
            var content = await session.Manager.StreamProvider!.CreateStreamAsync(selected, prebuffer: false, session.Cancellation.Token).ConfigureAwait(false);
            session.Content = new ActivityStream(content, session.Cancellation.Token, () => Interlocked.Exchange(ref session.LastActivityTicks, DateTime.UtcNow.Ticks));
            session.State = "Streaming";
            return new P2pReadLease(session.Content, selected.Length, Path.GetFileName(selected.Path), () => CloseAsync(session));
        }
        catch
        {
            session.OpenCompleted.TrySetResult();
            await CloseAsync(session).ConfigureAwait(false);
            throw;
        }
        finally
        {
            session.OpenCompleted.TrySetResult();
        }
    }

    public P2pStatus GetStatus()
    {
        lock (_gate)
        {
            var limit = Limits.Read(configuration.Current);
            return new P2pStatus(limit.Enabled, _sessions.Count, limit.Streams, _sessions.Values.Sum(session => session.ReservedBytes),
                P2pStorage.Size(_root), limit.CacheBytes, limit.DownloadBytes, limit.UploadBytes, limit.Dht,
                _sessions.Values.Select(session => new P2pSessionStatus(session.Id, session.State, session.ReservedBytes,
                    session.Manager?.Monitor.DownloadRate ?? 0, session.Manager?.Monitor.UploadRate ?? 0)).ToArray());
        }
    }

    public P2pCleanupResult Cleanup()
    {
        lock (_gate)
        {
            long bytes = 0;
            var count = 0;
            foreach (var directory in InactiveDirectories())
            {
                var size = P2pStorage.Size(directory);
                P2pStorage.Delete(_root, directory);
                bytes = checked(bytes + size);
                count++;
            }

            return new P2pCleanupResult(count, bytes, _sessions.Count);
        }
    }

    private IEnumerable<string> InactiveDirectories()
    {
        P2pStorage.CheckPath(_root, _root);
        if (!Directory.Exists(_root)) return [];
        return Directory.EnumerateDirectories(_root, "session-*")
            .Where(directory => Guid.TryParseExact(Path.GetFileName(directory)[8..], "N", out _)
                && !_sessions.Values.Any(session => session.Directory == directory)
                && File.Exists(Path.Combine(directory, ".siphon-p2p-v1"))).ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                Session[] expired;
                lock (_gate)
                {
                    var settings = Limits.Read(configuration.Current);
                    expired = _sessions.Values.Where(session => session.Cancellation.IsCancellationRequested || settings != session.Limits
                        || DateTime.UtcNow - new DateTime(Interlocked.Read(ref session.LastActivityTicks), DateTimeKind.Utc) > TimeSpan.FromMinutes(settings.IdleMinutes)).ToArray();
                }

                foreach (var session in expired) await CloseAsync(session).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            Session[] remaining;
            lock (_gate) { _stopping = true; remaining = _sessions.Values.ToArray(); }
            await Task.WhenAll(remaining.Select(session => CloseAsync(session).AsTask())).ConfigureAwait(false);
        }
    }

    private ValueTask CloseAsync(Session session)
    {
        lock (_gate)
        {
            session.CloseTask ??= CloseCoreAsync(session);
            return new ValueTask(session.CloseTask);
        }
    }

    private async Task CloseCoreAsync(Session session)
    {
        // Yield before taking the service lock again or waiting for an in-progress opener.
        await Task.Yield();
        session.Cancellation.Cancel();
        await session.OpenCompleted.Task.ConfigureAwait(false);
        try
        {
            session.Content?.Dispose();
            if (session.Manager is not null) await session.Manager.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning("P2P session stop failed ({ErrorType}); closing its transports.", exception.GetType().Name);
        }
        finally
        {
            session.Network?.Dispose();
            session.Engine?.Dispose();
            lock (_gate)
            {
                try { P2pStorage.Delete(_root, session.Directory); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("P2P scratch cleanup failed ({ErrorType}); administrator cleanup may be required.", exception.GetType().Name);
                }
                _sessions.Remove(session.Id);
            }

            session.Cancellation.Dispose();
        }
    }

    private sealed class Session(string id, int slot, Limits limits, CancellationToken cancellationToken)
    {
        public string Id { get; } = id;
        public int Slot { get; } = slot;
        public Limits Limits { get; } = limits;
        public string Directory { get; set; } = string.Empty;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        public TaskCompletionSource OpenCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long LastActivityTicks = DateTime.UtcNow.Ticks;
        public long ReservedBytes { get; set; }
        public string State { get; set; } = "Opening";
        public P2pNetwork? Network { get; set; }
        public ClientEngine? Engine { get; set; }
        public TorrentManager? Manager { get; set; }
        public ActivityStream? Content { get; set; }
        public Task? CloseTask { get; set; }
    }

    private sealed record Limits(bool Enabled, int Streams, long CacheBytes, int DownloadBytes, int UploadBytes, int IdleMinutes, int MetadataSeconds, bool Dht, int Port, string PrivateHosts)
    {
        public static Limits Read(PluginConfiguration config) => new(config.EnableP2p, config.P2pMaxConcurrentStreams, (long)config.P2pMaxCacheMiB * 1024 * 1024,
            checked(config.P2pDownloadLimitKiB * 1024), checked(config.P2pUploadLimitKiB * 1024), config.P2pIdleMinutes,
            config.P2pMetadataTimeoutSeconds, config.P2pEnableDht, config.P2pListenPort,
            string.Join('\n', config.AllowedPrivateHosts.Order(StringComparer.OrdinalIgnoreCase)));
    }
}

public sealed record P2pStatus(bool Enabled, int ActiveStreams, int MaximumStreams, long ReservedBytes, long CacheBytes, long MaximumCacheBytes,
    long DownloadLimitBytesPerSecond, long UploadLimitBytesPerSecond, bool DhtEnabled, P2pSessionStatus[] Sessions);
public sealed record P2pSessionStatus(string Id, string State, long ReservedBytes, long DownloadBytesPerSecond, long UploadBytesPerSecond);
public sealed record P2pCleanupResult(int RemovedDirectories, long RemovedBytes, int SkippedActive);
