using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Siphon.Metadata;

/// <summary>One policy for provider snapshots and native fields; locks always take precedence.</summary>
public static class MetadataPolicy
{
    public static bool CanUpdate(BaseItem target, PluginConfiguration config, string field, bool missing, bool initialize = false)
    {
        if (target.IsLocked) return false;
        var locked = field switch
        {
            "People" => MetadataField.Cast,
            "Ratings" => MetadataField.OfficialRating,
            _ => Enum.TryParse<MetadataField>(field, out var value) ? value : (MetadataField?)null
        };
        if (locked.HasValue && target.LockedFields.Contains(locked.Value)) return false;
        return initialize || target.DateLastSaved == DateTime.MinValue || (field == "Name" && missing) || Wants(config, field, missing);
    }

    internal static bool Wants(PluginConfiguration config, string field, bool missing)
        => config.MetadataUpdateMode == "RefreshSelected"
            ? config.MetadataRefreshFields.Contains(field, StringComparer.Ordinal)
            : missing;

    internal static ManagedItem Merge(ManagedItem item, MetadataValues value, PluginConfiguration config, bool episode = false)
    {
        string? Text(string field, string? current, string? incoming)
            => !string.IsNullOrWhiteSpace(incoming) && Wants(config, field, string.IsNullOrWhiteSpace(current)) ? incoming : current;
        T? Number<T>(string field, T? current, T? incoming) where T : struct
            => incoming.HasValue && Wants(config, field, !current.HasValue) ? incoming : current;
        T[] Array<T>(string field, T[] current, T[] incoming)
            => incoming.Length > 0 && Wants(config, field, current.Length == 0) ? incoming : current;
        if (episode)
            return item with
            {
                Name = Text("Name", item.Name, value.Name) ?? item.Name,
                Description = Text("Overview", item.Description, value.Overview),
                Released = Text("ReleaseDate", item.Released, value.Released),
                ThumbnailUrl = Text("Images", item.ThumbnailUrl, value.Poster),
                EpisodeCommunityRating = Number("Ratings", item.EpisodeCommunityRating, value.Rating),
                EpisodeRunTimeTicks = Number("Runtime", item.EpisodeRunTimeTicks, value.Runtime),
                EpisodePeople = Array("People", item.EpisodePeople, value.People)
            };
        return item with
        {
            Name = item.Type == "series" ? item.Name : Text("Name", item.Name, value.Name) ?? item.Name,
            SeriesName = item.Type == "series" ? Text("Name", item.SeriesName, value.Name) : item.SeriesName,
            Description = item.Type == "series" ? item.Description : Text("Overview", item.Description, value.Overview),
            SeriesDescription = item.Type == "series" ? Text("Overview", item.SeriesDescription, value.Overview) : item.SeriesDescription,
            Released = item.Type == "series" ? item.Released : Text("ReleaseDate", item.Released, value.Released),
            SeriesReleased = item.Type == "series" ? Text("ReleaseDate", item.SeriesReleased, value.Released) : item.SeriesReleased,
            Year = Number("ReleaseDate", item.Year, value.Year),
            CommunityRating = Number("Ratings", item.CommunityRating, value.Rating),
            RunTimeTicks = Number("Runtime", item.RunTimeTicks, value.Runtime),
            PosterUrl = Text("Images", item.PosterUrl, value.Poster),
            BackdropUrl = Text("Images", item.BackdropUrl, value.Backdrop),
            LogoUrl = Text("Images", item.LogoUrl, value.Logo),
            Genres = Array("Genres", item.Genres, value.Genres),
            People = Array("People", item.People, value.People),
            ProductionLocations = Array("ProductionLocations", item.ProductionLocations, value.Locations),
            SeriesStatus = Text("Status", item.SeriesStatus, value.Status)
        };
    }
}

internal sealed record MetadataValues
{
    public string? Name { get; init; }
    public string? Overview { get; init; }
    public string? Released { get; init; }
    public int? Year { get; init; }
    public float? Rating { get; init; }
    public long? Runtime { get; init; }
    public string? Poster { get; init; }
    public string? Backdrop { get; init; }
    public string? Logo { get; init; }
    public string? Status { get; init; }
    public string[] Genres { get; init; } = [];
    public string[] Locations { get; init; } = [];
    public ManagedPerson[] People { get; init; } = [];

    // Earlier providers win text; specialized artwork/rating providers override explicitly.
    public MetadataValues FillFrom(MetadataValues other) => this with
    {
        Name = Name ?? other.Name,
        Overview = Overview ?? other.Overview,
        Released = Released ?? other.Released,
        Year = Year ?? other.Year,
        Rating = Rating ?? other.Rating,
        Runtime = Runtime ?? other.Runtime,
        Poster = Poster ?? other.Poster,
        Backdrop = Backdrop ?? other.Backdrop,
        Logo = Logo ?? other.Logo,
        Status = Status ?? other.Status,
        Genres = Genres.Length > 0 ? Genres : other.Genres,
        Locations = Locations.Length > 0 ? Locations : other.Locations,
        People = People.Length > 0 ? People : other.People
    };
}
