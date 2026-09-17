using Jellyfin.Plugin.Siphon.Playback;

namespace Jellyfin.Plugin.Siphon.Downloads;

/// <summary>A transfer owns only its job directory; the budget covers temporary and final files.</summary>
public interface IDownloadTransfer
{
    Task<DownloadTransferResult> TransferAsync(ResolvedStream source, string directory, long maximumBytes,
        Func<DownloadTransferProgress, Task> progress, CancellationToken cancellationToken);
}

public sealed record DownloadTransferProgress(long BytesReceived, long? TotalBytes, long StoredBytes);
public sealed record DownloadTransferResult(string FileName, string ContentType, long Bytes);
