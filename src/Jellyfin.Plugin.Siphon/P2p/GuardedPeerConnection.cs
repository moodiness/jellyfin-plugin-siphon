using System.Net;
using MonoTorrent.BEncoding;
using MonoTorrent.Connections.Peer;
using ReusableTasks;

namespace Jellyfin.Plugin.Siphon.P2p;

/// <summary>Rejects oversized BEP-10 metadata advertisements before MonoTorrent allocates their buffers.</summary>
internal sealed class GuardedPeerConnection(IPeerConnection inner) : IPeerConnection
{
    private readonly PeerWireGuard _guard = new();
    public ReadOnlyMemory<byte> AddressBytes => inner.AddressBytes;
    public bool CanReconnect => inner.CanReconnect;
    public bool Disposed => inner.Disposed;
    public IPEndPoint? EndPoint => inner.EndPoint;
    public bool IsIncoming => inner.IsIncoming;
    public Uri Uri => inner.Uri;
    public void Dispose() => inner.Dispose();
    public ReusableTask ConnectAsync() => inner.ConnectAsync();
    public ReusableTask<int> SendAsync(Memory<byte> buffer) => inner.SendAsync(buffer);
    public async ReusableTask<int> ReceiveAsync(Memory<byte> buffer)
    {
        var count = await inner.ReceiveAsync(buffer).ConfigureAwait(false);
        try { _guard.Accept(buffer.Span[..count]); }
        catch { inner.Dispose(); throw; }
        return count;
    }
}

internal sealed class PeerWireGuard
{
    internal const int MaximumMetadataBytes = 16 * 1024 * 1024;
    private int _handshakeRemaining = 68;
    private int _lengthBytes;
    private int _frameLength;
    private int _remaining;
    private int _position;
    private byte _message;
    private byte[]? _extension;

    internal void Accept(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (_handshakeRemaining > 0)
            {
                var count = Math.Min(data.Length, _handshakeRemaining);
                _handshakeRemaining -= count;
                data = data[count..];
                continue;
            }
            if (_lengthBytes < 4)
            {
                _frameLength = (_frameLength << 8) | data[0];
                _lengthBytes++;
                data = data[1..];
                if (_lengthBytes != 4) continue;
                if (_frameLength < 0 || _frameLength > 2 * 1024 * 1024) throw new IOException("P2P peer message exceeds its limit.");
                _remaining = _frameLength;
                if (_remaining == 0) ResetFrame();
                continue;
            }
            if (_position < 2)
            {
                if (_position == 0) _message = data[0];
                else if (_message == 20 && data[0] == 0)
                {
                    if (_frameLength > 64 * 1024) throw new IOException("P2P extended handshake is too large.");
                    _extension = new byte[_frameLength - 2];
                }
                _position++;
                _remaining--;
                data = data[1..];
            }
            else
            {
                var count = Math.Min(data.Length, _remaining);
                if (_extension is not null) data[..count].CopyTo(_extension.AsSpan(_position - 2));
                _position += count;
                _remaining -= count;
                data = data[count..];
            }
            if (_remaining == 0)
            {
                if (_extension is not null) ValidateMetadataAdvertisement(_extension);
                ResetFrame();
            }
        }
    }

    private void ResetFrame()
    {
        _lengthBytes = 0;
        _frameLength = 0;
        _position = 0;
        _remaining = 0;
        _extension = null;
    }

    private static void ValidateMetadataAdvertisement(ReadOnlySpan<byte> bytes)
    {
        ValidateEncoding(bytes);
        var dictionary = BEncodedValue.Decode<BEncodedDictionary>(bytes);
        if (dictionary.TryGetValue("metadata_size", out var advertised)
            && (advertised is not BEncodedNumber number || number.Number < 0 || number.Number > MaximumMetadataBytes))
            throw new IOException("P2P metadata exceeds its memory limit.");
    }

    internal static void ValidateEncoding(ReadOnlySpan<byte> bytes)
    {
        // Limit nesting before invoking the library's recursive bencoding decoder.
        var depth = 0;
        for (var index = 0; index < bytes.Length;)
        {
            var value = bytes[index++];
            if (value is (byte)'d' or (byte)'l')
            {
                if (++depth > 32) throw new IOException("P2P handshake nesting exceeds its limit.");
            }
            else if (value == (byte)'e') depth--;
            else if (value == (byte)'i')
            {
                var end = bytes[index..].IndexOf((byte)'e');
                if (end is < 1 or > 21) throw new IOException("Invalid P2P handshake integer.");
                index += end + 1;
            }
            else if (value >= '0' && value <= '9')
            {
                var length = value - '0';
                while (index < bytes.Length && bytes[index] >= '0' && bytes[index] <= '9')
                {
                    if (length > MaximumMetadataBytes) throw new IOException("Invalid P2P handshake string.");
                    length = length * 10 + bytes[index++] - '0';
                }
                if (index >= bytes.Length || bytes[index++] != ':' || length > bytes.Length - index)
                    throw new IOException("Invalid P2P handshake string.");
                index += length;
            }
            else throw new IOException("Invalid P2P handshake encoding.");
            if (depth < 0) throw new IOException("Invalid P2P handshake nesting.");
        }
        if (depth != 0) throw new IOException("Invalid P2P handshake nesting.");
    }
}
