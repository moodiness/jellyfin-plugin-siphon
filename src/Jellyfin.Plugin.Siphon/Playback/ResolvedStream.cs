namespace Jellyfin.Plugin.Siphon.Playback;

/// <summary>A server-only stream candidate; URLs and headers never enter Jellyfin response DTOs.</summary>
public sealed record ResolvedStream(
    string Id,
    string Name,
    Uri Url,
    IReadOnlyDictionary<string, string> RequestHeaders,
    string? FileName,
    long? Size)
{
    public Guid UserId { get; init; }
    public P2p.P2pSource? P2p { get; init; }
}
