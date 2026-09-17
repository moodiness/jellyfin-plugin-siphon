using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MonoTorrent;
using MonoTorrent.Connections.Tracker;
using MonoTorrent.Messages.UdpTracker;
using MonoTorrent.Trackers;
using ReusableTasks;

namespace Jellyfin.Plugin.Siphon.P2p;

/// <summary>Uses released MonoTorrent protocol messages, with pinned endpoints and cancellable sockets.</summary>
internal sealed class GuardedUdpTracker(Uri uri, IPEndPoint endpoint, CancellationToken shutdown) : ITrackerConnection
{
    public Uri Uri { get; } = uri;
    public bool CanScrape => true;

    public async ReusableTask<AnnounceResponse> AnnounceAsync(AnnounceRequest parameters, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var client = CreateClient();
            var connection = await ConnectAsync(client, timeout.Token).ConfigureAwait(false);
            var peers = new Dictionary<InfoHash, IList<PeerInfo>>();
            TimeSpan interval = TimeSpan.FromMinutes(30);
            foreach (var hash in Hashes(parameters.InfoHashes))
            {
                var port = parameters.GetReportedAddress(endpoint.AddressFamily == AddressFamily.InterNetwork ? "ipv4" : "ipv6").port;
                var response = await ExchangeAsync(client, new AnnounceMessage(TransactionId(), connection, parameters, hash, port), timeout.Token, Uri.PathAndQuery).ConfigureAwait(false);
                if (response is not AnnounceResponseMessage announce) throw new IOException("Unexpected UDP tracker response.");
                peers.Add(hash, announce.Peers);
                interval = announce.Interval;
            }

            return new AnnounceResponse(TrackerState.Ok, peers, minUpdateInterval: interval);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new AnnounceResponse(TrackerState.Offline, failureMessage: "P2P tracker announce failed.");
        }
    }

    public async ReusableTask<ScrapeResponse> ScrapeAsync(ScrapeRequest parameters, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var client = CreateClient();
            var connection = await ConnectAsync(client, timeout.Token).ConfigureAwait(false);
            var hashes = Hashes(parameters.InfoHashes).ToList();
            var response = await ExchangeAsync(client, new ScrapeMessage(TransactionId(), connection, hashes), timeout.Token).ConfigureAwait(false);
            if (response is not ScrapeResponseMessage scrape || scrape.Scrapes.Count != hashes.Count) throw new IOException("Invalid UDP tracker scrape.");
            var result = new Dictionary<InfoHash, ScrapeInfo>();
            for (var index = 0; index < hashes.Count; index++)
                result.Add(hashes[index], new ScrapeInfo(scrape.Scrapes[index].Seeds, scrape.Scrapes[index].Complete, scrape.Scrapes[index].Leeches));
            return new ScrapeResponse(TrackerState.Ok, result);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new ScrapeResponse(TrackerState.Offline, failureMessage: "P2P tracker scrape failed.");
        }
    }

    private UdpClient CreateClient()
    {
        shutdown.ThrowIfCancellationRequested();
        var client = new UdpClient(endpoint.AddressFamily);
        client.Connect(endpoint);
        return client;
    }

    private static async Task<long> ConnectAsync(UdpClient client, CancellationToken token)
    {
        var request = new RandomConnectMessage();
        var response = await ExchangeAsync(client, request, token).ConfigureAwait(false);
        return response is ConnectResponseMessage connect ? connect.ConnectionId : throw new IOException("Invalid UDP tracker connection response.");
    }

    private static async Task<UdpTrackerMessage> ExchangeAsync(UdpClient client, UdpTrackerMessage message, CancellationToken token, string? urlData = null)
    {
        ReadOnlyMemory<byte> bytes = message.Encode();
        if (!string.IsNullOrEmpty(urlData))
        {
            // BEP-41 retains private UDP tracker passkeys in PATH/QUERY instead of silently discarding them.
            var url = Encoding.UTF8.GetBytes(urlData);
            var packet = new byte[bytes.Length + url.Length + 2 * ((url.Length + 254) / 255)];
            bytes.Span.CopyTo(packet);
            var offset = bytes.Length;
            for (var index = 0; index < url.Length;)
            {
                var count = Math.Min(255, url.Length - index);
                packet[offset++] = 2;
                packet[offset++] = (byte)count;
                url.AsSpan(index, count).CopyTo(packet.AsSpan(offset));
                offset += count;
                index += count;
            }
            bytes = packet;
        }
        await client.SendAsync(bytes, token).ConfigureAwait(false);
        while (true)
        {
            var datagram = await client.ReceiveAsync(token).ConfigureAwait(false);
            var response = UdpTrackerMessage.DecodeMessage(datagram.Buffer, MessageType.Response, datagram.RemoteEndPoint.AddressFamily);
            if (response.TransactionId != message.TransactionId) continue;
            if (response is ErrorMessage) throw new IOException("UDP tracker rejected the request.");
            return response;
        }
    }

    private static int TransactionId() => RandomNumberGenerator.GetInt32(int.MaxValue);
    private static IEnumerable<InfoHash> Hashes(InfoHashes hashes)
    {
        if (hashes.V1 is not null) yield return hashes.V1;
        if (hashes.V2 is not null) yield return hashes.V2;
    }

    private sealed class RandomConnectMessage : ConnectMessage
    {
        public RandomConnectMessage() => TransactionId = RandomNumberGenerator.GetInt32(int.MaxValue);
    }
}
