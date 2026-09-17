using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Siphon.Downloads;

[JsonConverter(typeof(JsonStringEnumConverter<DownloadJobState>))]
internal enum DownloadJobState { Queued, Running, Completed, Failed, Cancelled }

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
    public string? FileName { get; init; }
    public string? ContentType { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public string? Error { get; init; }
    public bool DeleteRequested { get; init; }
}

public sealed record DownloadJobSummary(Guid Id, Guid ItemId, string MediaSourceId, string Name, string State,
    long BytesReceived, long? TotalBytes, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, string? Error, bool CanRetry);
public sealed record DownloadQueueResponse(bool Enabled, bool Allowed, IReadOnlyList<DownloadJobSummary> Jobs, long UsedBytes, long MaximumBytes);
public sealed record DownloadQueueRequest(Guid ItemId, string MediaSourceId);
public sealed record DownloadFileTicket(string Url, DateTimeOffset ExpiresAtUtc);

internal sealed class DownloadQueueException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
