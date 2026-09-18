using System.Collections.ObjectModel;
using Jellyfin.Plugin.Siphon.Identity;

namespace Jellyfin.Plugin.Siphon.Infrastructure;

/// <summary>A deeply read-only view of one owned item. Mutable working copies are explicit.</summary>
public sealed class ManagedItemSnapshot
{
    private readonly ManagedItem _item;

    internal ManagedItemSnapshot(ManagedItem owned)
    {
        _item = owned;
        Owners = Array.AsReadOnly(owned.Owners);
        MissingOwners = Array.AsReadOnly(owned.MissingOwners);
        Genres = Array.AsReadOnly(owned.Genres);
        ProductionLocations = Array.AsReadOnly(owned.ProductionLocations);
        People = Array.AsReadOnly(owned.People);
        EpisodeGenres = Array.AsReadOnly(owned.EpisodeGenres);
        EpisodeProductionLocations = Array.AsReadOnly(owned.EpisodeProductionLocations);
        EpisodePeople = Array.AsReadOnly(owned.EpisodePeople);
        StreamIdentities = Array.AsReadOnly(owned.StreamIdentities);
        ProviderIds = new ReadOnlyDictionary<string, string>(owned.ProviderIds);
        EpisodeProviderIds = new ReadOnlyDictionary<string, string>(owned.EpisodeProviderIds);
        MetadataProvenance = new ReadOnlyDictionary<string, MetadataFieldSnapshot>(owned.MetadataProvenance.ToDictionary(
            pair => pair.Key, pair => new MetadataFieldSnapshot(pair.Value), StringComparer.Ordinal));
    }

    /// <summary>Copies caller-owned data; later mutations cannot affect this view.</summary>
    public static ManagedItemSnapshot CopyOf(ManagedItem item) => new(SiphonStateStore.Clone(item));

    /// <summary>Returns an isolated working copy, including all nested collections and provenance.</summary>
    public ManagedItem ToMutable() => SiphonStateStore.Clone(_item);

    public string Key => _item.Key;
    public string Type => _item.Type;
    public string ContentId => _item.ContentId;
    public string ContentKey => _item.ContentKey;
    public string VideoId => _item.VideoId;
    public string Name => _item.Name;
    public string? SeriesName => _item.SeriesName;
    public int? Year => _item.Year;
    public int? Season => _item.Season;
    public int? Episode => _item.Episode;
    public string Path => _item.Path;
    public DateTimeOffset? MissingSinceUtc => _item.MissingSinceUtc;
    public bool IsSearchPreview => _item.IsSearchPreview;
    public DateTimeOffset? PreviewExpiresUtc => _item.PreviewExpiresUtc;
    public string? SearchAddonId => _item.SearchAddonId;
    public string? SearchResourceType => _item.SearchResourceType;
    public string? SearchMetadataAddonId => _item.SearchMetadataAddonId;
    public string? Description => _item.Description;
    public string? SeriesDescription => _item.SeriesDescription;
    public string? PosterUrl => _item.PosterUrl;
    public string? SeasonPosterUrl => _item.SeasonPosterUrl;
    public string? BackdropUrl => _item.BackdropUrl;
    public string? LogoUrl => _item.LogoUrl;
    public string? ThumbnailUrl => _item.ThumbnailUrl;
    public string? Released => _item.Released;
    public string? SeriesReleased => _item.SeriesReleased;
    public float? CommunityRating => _item.CommunityRating;
    public long? RunTimeTicks => _item.RunTimeTicks;
    public string? OfficialRating => _item.OfficialRating;
    public string? SeriesStatus => _item.SeriesStatus;
    public float? EpisodeCommunityRating => _item.EpisodeCommunityRating;
    public long? EpisodeRunTimeTicks => _item.EpisodeRunTimeTicks;
    public string? EpisodeOfficialRating => _item.EpisodeOfficialRating;
    public string? EpisodeBackdropUrl => _item.EpisodeBackdropUrl;
    public string? EpisodeLogoUrl => _item.EpisodeLogoUrl;
    public IReadOnlyList<string> Owners { get; }
    public IReadOnlyList<string> MissingOwners { get; }
    public IReadOnlyList<string> Genres { get; }
    public IReadOnlyList<string> ProductionLocations { get; }
    public IReadOnlyList<ManagedPerson> People { get; }
    public IReadOnlyList<string> EpisodeGenres { get; }
    public IReadOnlyList<string> EpisodeProductionLocations { get; }
    public IReadOnlyList<ManagedPerson> EpisodePeople { get; }
    public IReadOnlyList<StreamIdentity> StreamIdentities { get; }
    public IReadOnlyDictionary<string, string> ProviderIds { get; }
    public IReadOnlyDictionary<string, string> EpisodeProviderIds { get; }
    public IReadOnlyDictionary<string, MetadataFieldSnapshot> MetadataProvenance { get; }
}

/// <summary>Current field evidence with no mutable source array exposed.</summary>
public sealed class MetadataFieldSnapshot
{
    internal MetadataFieldSnapshot(MetadataFieldProvenance owned)
    {
        Sources = Array.AsReadOnly(owned.Sources);
        PreservationReason = owned.PreservationReason;
    }

    public IReadOnlyList<MetadataObservation> Sources { get; }
    public string? PreservationReason { get; }
}
