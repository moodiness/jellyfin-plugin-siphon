using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Siphon.Downloads;

[JsonConverter(typeof(JsonStringEnumConverter<DownloadJobState>))]
internal enum DownloadJobState { Queued, Running, Paused, Completed, Failed, Cancelled }

// Only stable native identities and digests belong in this ledger, never a resolved source.
internal sealed record DownloadJob
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public Guid ItemId { get; init; }
    public Guid MediaSourceId { get; init; }
    public string ItemKey { get; init; } = string.Empty;
    public string SourceId { get; init; } = string.Empty;
    public string ProfileKey { get; init; } = string.Empty;
    public string TransportKey { get; init; } = string.Empty;
    public string StorageKey { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public DownloadJobState State { get; init; }
    public long BytesReceived { get; init; }
    public long? TotalBytes { get; init; }
    public long StoredBytes { get; init; }
    public long ReservedBytes { get; init; }
    public int Priority { get; init; }
    public string Phase { get; init; } = "Waiting";
    public string[]? AudioLanguages { get; init; }
    public string[]? SubtitleLanguages { get; init; }
    public double? BytesPerSecond { get; init; }
    public double? EstimatedSecondsRemaining { get; init; }
    public string? FileName { get; init; }
    public string? ContentType { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public string? Error { get; init; }
    public bool DeleteRequested { get; init; }
}

public sealed record DownloadJobSummary(Guid Id, Guid ItemId, string MediaSourceId, string Name, string State,
    long BytesReceived, long? TotalBytes, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, string? Error, bool CanRetry,
    int Priority, string Phase, double? BytesPerSecond, double? EstimatedSecondsRemaining, DateTimeOffset? NextEligibleUtc,
    string[]? AudioLanguages, string[]? SubtitleLanguages);
public sealed record DownloadQueueResponse(bool Enabled, bool Allowed, IReadOnlyList<DownloadJobSummary> Jobs, long UsedBytes, long MaximumBytes,
    long UserMaximumBytes, long ReservedBytes, bool WindowEnabled, int WindowStartUtcHour, int WindowEndUtcHour);
public sealed record DownloadQueueRequest(Guid ItemId, string MediaSourceId, string[]? AudioLanguages = null, string[]? SubtitleLanguages = null, int Priority = 0);
public sealed record DownloadPriorityRequest(int Priority);
public sealed record DownloadPreparationRequest(Guid ItemId, string MediaSourceId);
public sealed record DownloadPreparation(string Kind, string[] AudioLanguages, string[] SubtitleLanguages,
    long? EstimatedSourceBytes, long? EstimatedTemporaryBytes, string[] Warnings);
public sealed record DownloadBatchPreviewRequest(Guid ItemId);
public sealed record DownloadBatchSource(string MediaSourceId, string Name, long? Size, string Kind);
public sealed record DownloadBatchItem(Guid ItemId, string Name, int? SeasonNumber, int? EpisodeNumber, DownloadBatchSource[] Sources);
public sealed record DownloadBatchPreview(string Token, DateTimeOffset ExpiresAtUtc, DownloadBatchItem[] Items, bool Truncated, int MaximumJobs);
public sealed record DownloadBatchRequest(string Token, DownloadQueueRequest[] Items);
public sealed record DownloadBatchRejection(Guid ItemId, string Message, string Code);
public sealed record DownloadBatchResult(DownloadJobSummary[] Jobs, DownloadBatchRejection[] Rejected);
public sealed record DownloadQueueHealth(bool Enabled, int Queued, int Running, int Paused, int Failed, int Completed,
    long StoredBytes, long ReservedBytes, long MaximumBytes);
public sealed record DownloadFileTicket(string Url, DateTimeOffset ExpiresAtUtc);

internal sealed class DownloadQueueException(int statusCode, string message, string? code = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code ?? statusCode switch
    {
        400 => "DownloadInvalidRequest",
        403 => "DownloadPermissionDenied",
        404 => "DownloadSourceUnavailable",
        429 => "DownloadBusy",
        _ => "DownloadUnavailable"
    };
}
