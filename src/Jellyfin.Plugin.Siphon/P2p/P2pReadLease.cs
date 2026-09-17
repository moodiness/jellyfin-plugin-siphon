namespace Jellyfin.Plugin.Siphon.P2p;

public sealed class P2pReadLease : IAsyncDisposable
{
    private readonly Func<ValueTask> _release;
    private int _disposed;
    internal P2pReadLease(Stream content, long length, string fileName, Func<ValueTask> release)
    {
        Content = content;
        Length = length;
        FileName = fileName;
        _release = release;
    }

    public Stream Content { get; }
    public long Length { get; }
    public string FileName { get; }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await Content.DisposeAsync().ConfigureAwait(false); }
        finally { await _release().ConfigureAwait(false); }
    }
}

internal sealed class ActivityStream(Stream inner, CancellationToken lifetime, Action touch) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin)
    {
        lifetime.ThrowIfCancellationRequested();
        touch();
        return inner.Seek(offset, origin);
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, lifetime).GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        touch();
        var result = await inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        touch();
        return result;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
