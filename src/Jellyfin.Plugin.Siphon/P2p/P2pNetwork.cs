using System.Net;
using System.Net.Sockets;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MonoTorrent;
using MonoTorrent.Connections;
using MonoTorrent.Connections.Dht;
using MonoTorrent.Connections.Peer;
using MonoTorrent.Connections.Tracker;
using MonoTorrent.Trackers;

namespace Jellyfin.Plugin.Siphon.P2p;

internal sealed class P2pNetwork(SsrfPolicy policy) : IDisposable
{
    private readonly Dictionary<string, Uri> _trackers = new(StringComparer.Ordinal);
    private readonly List<HttpClient> _clients = [];
    private readonly List<IPeerConnection> _peers = [];
    private readonly CancellationTokenSource _shutdown = new();

    internal async Task PrepareAsync(IEnumerable<string> trackers, CancellationToken cancellationToken)
    {
        foreach (var text in trackers)
        {
            P2pSource.ValidateTracker(text);
            var uri = new Uri(text);
            var addresses = await ResolveAsync(uri.Host, cancellationToken).ConfigureAwait(false);
            // MonoTorrent's UDP tracker implementation performs its own DNS lookup; give it only a vetted literal.
            _trackers[text] = uri.Scheme == "udp" ? new UriBuilder(uri) { Host = addresses[0].ToString() }.Uri : uri;
            _trackers[uri.AbsoluteUri] = _trackers[text];
        }
    }

    internal Factories CreateFactories(string root) => Factories.Default
        .WithHttpClientCreator(CreateHttpClient)
        .WithPeerConnectionCreator("ipv4", CreatePeer)
        .WithPeerConnectionCreator("ipv6", CreatePeer)
        .WithPeerConnectionListenerCreator(endpoint => new GuardedPeerListener(endpoint, policy, TrackPeer))
        .WithDhtListenerCreator(endpoint => new GuardedDhtListener(endpoint, policy))
        .WithTrackerCreator("http", CreateTracker)
        .WithTrackerCreator("https", CreateTracker)
        .WithTrackerCreator("udp", CreateTracker)
        .WithPieceWriterCreator(maximum => new GuardedPieceWriter(root, maximum));

    private IPeerConnection CreatePeer(Uri uri)
    {
        // Peer lists contain literals. Reject hostnames rather than permit a second unchecked DNS resolution.
        if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || !policy.IsAllowed(uri.Host, address))
            throw new HttpRequestException("P2P peer destination is not permitted.");
        return TrackPeer(new SocketPeerConnection(uri, new SocketConnector()));
    }

    private ITracker CreateTracker(Uri uri)
    {
        if (!_trackers.TryGetValue(uri.OriginalString, out var approved) && !_trackers.TryGetValue(uri.AbsoluteUri, out approved))
            throw new HttpRequestException("Torrent metadata cannot introduce additional trackers.");
        if (approved.Scheme == "udp")
        {
            var address = IPAddress.Parse(approved.Host.Trim('[', ']'));
            if (!policy.IsAllowed(uri.Host, address)) throw new HttpRequestException("P2P tracker destination is not permitted.");
            return new Tracker(new GuardedUdpTracker(uri, new IPEndPoint(address, approved.Port), _shutdown.Token));
        }

        return new Tracker(new HttpTrackerConnection(approved, _ => CreateHttpClient(AddressFamily.Unspecified), AddressFamily.InterNetwork));
    }

    private HttpClient CreateHttpClient(AddressFamily family)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxResponseHeadersLength = 32,
            ConnectCallback = async (context, token) =>
            {
                var host = context.DnsEndPoint.Host;
                var addresses = await ResolveAsync(host, token).ConfigureAwait(false);
                Exception? error = null;
                foreach (var address in addresses.Where(address => family == AddressFamily.Unspecified || address.AddressFamily == family))
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception exception) when (exception is SocketException or OperationCanceledException)
                    {
                        socket.Dispose();
                        token.ThrowIfCancellationRequested();
                        error = exception;
                    }
                }

                throw new HttpRequestException("P2P tracker connection failed.", error);
            }
        };
        var client = new HttpClient(new GuardedHttpHandler(policy) { InnerHandler = handler }) { Timeout = TimeSpan.FromSeconds(20) };
        lock (_clients)
        {
            if (_shutdown.IsCancellationRequested)
            {
                client.Dispose();
                throw new ObjectDisposedException(nameof(P2pNetwork));
            }
            _clients.Add(client);
        }
        return client;
    }

    private async Task<IPAddress[]> ResolveAsync(string host, CancellationToken token)
    {
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !policy.IsAllowed(host, address)))
            throw new HttpRequestException("P2P destination is not permitted.");
        return addresses;
    }

    private IPeerConnection TrackPeer(IPeerConnection connection)
    {
        connection = new GuardedPeerConnection(connection);
        lock (_peers)
        {
            if (_shutdown.IsCancellationRequested)
            {
                connection.Dispose();
                throw new ObjectDisposedException(nameof(P2pNetwork));
            }
            _peers.RemoveAll(peer => peer.Disposed);
            _peers.Add(connection);
            return connection;
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        lock (_peers)
        {
            foreach (var peer in _peers) peer.Dispose();
            _peers.Clear();
        }
        lock (_clients)
        {
            foreach (var client in _clients) client.Dispose();
            _clients.Clear();
        }
    }

    private sealed class GuardedHttpHandler(SsrfPolicy policy) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            policy.ValidateUri(request.RequestUri ?? throw new HttpRequestException("Missing P2P tracker URI."));
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            try
            {
                response.EnsureSuccessStatusCode();
                await response.Content.LoadIntoBufferAsync(1024 * 1024, cancellationToken).ConfigureAwait(false);
                PeerWireGuard.ValidateEncoding(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }
}

internal sealed class GuardedDhtListener : IDhtListener
{
    private readonly DhtListener _inner;
    private readonly SsrfPolicy _policy;
    public GuardedDhtListener(IPEndPoint endpoint, SsrfPolicy policy)
    {
        _policy = policy;
        _inner = new DhtListener(endpoint);
        _inner.MessageReceived += (data, peer) =>
        {
            if (_policy.IsAllowed(peer.Address.ToString(), peer.Address)) MessageReceived?.Invoke(data, peer);
        };
    }

    public event Action<ReadOnlyMemory<byte>, IPEndPoint>? MessageReceived;
    public event EventHandler<EventArgs> StatusChanged { add => _inner.StatusChanged += value; remove => _inner.StatusChanged -= value; }
    public ListenerStatus Status => _inner.Status;
    public IPEndPoint? LocalEndPoint => _inner.LocalEndPoint;
    public void Start() => _inner.Start();
    public void Stop() => _inner.Stop();
    public Task SendAsync(ReadOnlyMemory<byte> buffer, IPEndPoint endpoint)
    {
        // Ignoring forbidden DHT destinations is equivalent to an unreachable DHT node.
        return _policy.IsAllowed(endpoint.Address.ToString(), endpoint.Address) ? _inner.SendAsync(buffer, endpoint) : Task.CompletedTask;
    }
}

internal sealed class GuardedPeerListener : IPeerConnectionListener
{
    private readonly PeerConnectionListener _inner;
    public GuardedPeerListener(IPEndPoint endpoint, SsrfPolicy policy, Func<IPeerConnection, IPeerConnection> track)
    {
        _inner = new PeerConnectionListener(endpoint);
        _inner.ConnectionReceived += (_, args) =>
        {
            var peer = args.Connection.EndPoint;
            if (peer is not null && policy.IsAllowed(peer.Address.ToString(), peer.Address))
            {
                try { ConnectionReceived?.Invoke(this, new PeerConnectionEventArgs(track(args.Connection), args.InfoHash)); }
                catch (ObjectDisposedException) { args.Connection.Dispose(); }
            }
            else args.Connection.Dispose();
        };
    }

    public event EventHandler<PeerConnectionEventArgs>? ConnectionReceived;
    public event EventHandler<EventArgs> StatusChanged { add => _inner.StatusChanged += value; remove => _inner.StatusChanged -= value; }
    public ListenerStatus Status => _inner.Status;
    public IPEndPoint? LocalEndPoint => _inner.LocalEndPoint;
    public IPEndPoint PreferredLocalEndPoint => _inner.PreferredLocalEndPoint;
    public void Start() => _inner.Start();
    public void Stop() => _inner.Stop();
}
