using Jellyfin.Plugin.Siphon.Playback;

namespace Jellyfin.Plugin.Siphon.Downloads;

/// <summary>A transfer owns only its job directory; the budget covers temporary and final files.</summary>
public interface IDownloadTransfer
{
    Task<DownloadPreparation> PrepareAsync(ResolvedStream source, CancellationToken cancellationToken);
    Task<DownloadTransferResult> TransferAsync(ResolvedStream source, string directory, long maximumBytes, DownloadTransferOptions options,
        Func<DownloadTransferProgress, Task> progress, CancellationToken cancellationToken);
}

public sealed record DownloadTransferOptions(string[]? AudioLanguages = null, string[]? SubtitleLanguages = null)
{
    internal bool HasSelection => AudioLanguages is not null || SubtitleLanguages is not null;
    internal static bool ValidLanguages(string[]? values) => values is null || values.Length <= 32
        && values.All(value => value is { Length: > 0 and <= 64 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
        && values.Distinct(StringComparer.OrdinalIgnoreCase).Count() == values.Length;
}
public sealed record DownloadTransferProgress(long BytesReceived, long? TotalBytes, long StoredBytes, string Phase = "Receiving");
public sealed record DownloadTransferResult(string FileName, string ContentType, long Bytes);
