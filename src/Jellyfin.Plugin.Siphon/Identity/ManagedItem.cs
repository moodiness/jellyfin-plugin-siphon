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
    public DateTimeOffset? MissingSinceUtc { get; init; }
    // Keep historical ownership: absence requires a complete snapshot from every owner.
    public string[] MissingOwners { get; init; } = [];
    public string? Description { get; init; }
    public string? SeriesDescription { get; init; }
    public string? PosterUrl { get; init; }
    public string? SeasonPosterUrl { get; init; }
    public string? BackdropUrl { get; init; }
    public string? LogoUrl { get; init; }
    public string? ThumbnailUrl { get; init; }
    public string? Released { get; init; }
    public string? SeriesReleased { get; init; }
    public float? CommunityRating { get; init; }
    public long? RunTimeTicks { get; init; }
    public string? OfficialRating { get; init; }
    public string? SeriesStatus { get; init; }
    public string[] Genres { get; init; } = [];
    public string[] ProductionLocations { get; init; } = [];
    public ManagedPerson[] People { get; init; } = [];
    public float? EpisodeCommunityRating { get; init; }
    public long? EpisodeRunTimeTicks { get; init; }
    public string? EpisodeOfficialRating { get; init; }
    public string? EpisodeBackdropUrl { get; init; }
    public string? EpisodeLogoUrl { get; init; }
    public string[] EpisodeGenres { get; init; } = [];
    public string[] EpisodeProductionLocations { get; init; } = [];
    public ManagedPerson[] EpisodePeople { get; init; } = [];
    public StreamIdentity[] StreamIdentities { get; init; } = [];
}

/// <summary>Exact addon-provided resource identity, never inferred from Jellyfin numbering.</summary>
public sealed record StreamIdentity(string Type, string VideoId);

/// <summary>An addon-provided credit, kept separate for a series and its episodes.</summary>
public sealed record ManagedPerson(string Name, string Type, string? Role = null, string? PhotoUrl = null);
