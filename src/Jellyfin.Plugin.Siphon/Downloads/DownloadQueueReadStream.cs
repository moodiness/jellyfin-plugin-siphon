namespace Jellyfin.Plugin.Siphon.Downloads;

/// <summary>Rechecks authority for each file read, including range responses already in flight.</summary>
internal sealed class DownloadQueueReadStream(Stream inner, Func<bool> authorized) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length { get { Check(); return inner.Length; } }
    public override long Position { get => inner.Position; set { Check(); inner.Position = value; } }
    private void Check()
    {
        if (!authorized()) throw new IOException("The download capability is no longer authorized.");
    }
    public override int Read(byte[] buffer, int offset, int count) { Check(); return inner.Read(buffer, offset, count); }
    public override int Read(Span<byte> buffer) { Check(); return inner.Read(buffer); }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Check();
        return inner.ReadAsync(buffer, offset, count, cancellationToken);
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Check();
        return inner.ReadAsync(buffer, cancellationToken);
    }
    public override long Seek(long offset, SeekOrigin origin) { Check(); return inner.Seek(offset, origin); }
    public override void Flush() => inner.Flush();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
