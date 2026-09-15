namespace Jellyfin.Plugin.Siphon.Identity;

/// <summary>A playable movie or episode owned by Siphon, independent of expiring URLs.</summary>
public sealed record ManagedItem
{
    public required string Key { get; init; }
    public required string Type { get; init; }
    public required string ContentId { get; init; }
    public required string ContentKey { get; init; }
    public Dictionary<string, string> ProviderIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> EpisodeProviderIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public required string VideoId { get; init; }
    public required string Name { get; init; }
    public string? SeriesName { get; init; }
    public int? Year { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public required string Path { get; init; }
    public string[] Owners { get; init; } = [];
    public string? Description { get; init; }
    public string? SeriesDescription { get; init; }
    public string? PosterUrl { get; init; }
    public string? Released { get; init; }
    public string[] Genres { get; init; } = [];
    public StreamIdentity[] StreamIdentities { get; init; } = [];
}

/// <summary>Exact addon-provided resource identity, never inferred from Jellyfin numbering.</summary>
public sealed record StreamIdentity(string Type, string VideoId);
