using System.Security.Cryptography;
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

    internal static bool CanUpdateImage(BaseItem target, PluginConfiguration config, ImageType type,
        bool missingManagedValue = false, bool initialize = false, BaseItem? inheritedFrom = null)
        => CanUpdate(target, config, "Images",
            !target.HasImage(type) || (missingManagedValue && IsManagedImage(target, type, inheritedFrom)), initialize);

    internal static bool IsManagedImage(BaseItem target, ImageType type, BaseItem? inheritedFrom = null)
    {
        if (string.IsNullOrEmpty(target.GetProviderId("SiphonImage" + type))
            || target.GetImageInfo(type, 0) is not { } image) return false;
        if (!image.IsLocalFile)
            return Uri.TryCreate(image.Path, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https"
                && uri.AbsolutePath.Contains("/Siphon/image/", StringComparison.Ordinal);

        // Jellyfin localizes remote images and retains provider IDs after manual uploads.
        // Only an identical inherited poster proves that a legacy local image is a fallback.
        if (type != ImageType.Primary || inheritedFrom?.GetImageInfo(type, 0) is not { IsLocalFile: true } parent) return false;
        try
        {
            using var current = File.OpenRead(image.Path);
            using var inherited = File.OpenRead(parent.Path);
            return current.Length is > 0 and <= 8 * 1024 * 1024 && current.Length == inherited.Length
                && SHA256.HashData(current).AsSpan().SequenceEqual(SHA256.HashData(inherited));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
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
        var result = episode
            ? item with
            {
                Name = Text("Name", item.Name, value.Name) ?? item.Name,
                Description = Text("Overview", item.Description, value.Overview),
                Released = Text("ReleaseDate", item.Released, value.Released),
                ThumbnailUrl = Text("Images", item.ThumbnailUrl, value.Poster),
                EpisodeCommunityRating = Number("Ratings", item.EpisodeCommunityRating, value.Rating),
                EpisodeRunTimeTicks = Number("Runtime", item.EpisodeRunTimeTicks, value.Runtime),
                EpisodePeople = Array("People", item.EpisodePeople, value.People)
            }
            : item with
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
        return value.ApplyProvenance(result, item, config, episode);
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
    public Dictionary<string, MetadataFieldProvenance> Provenance { get; init; } = new(StringComparer.Ordinal);

    private static readonly string[] ValueFields = ["Name", "Overview", "Released", "Year", "Rating", "Runtime", "Poster", "Backdrop", "Logo", "Status", "Genres", "Locations", "People"];
    private object? Value(string field) => field switch
    {
        "Name" => Name,
        "Overview" => Overview,
        "Released" => Released,
        "Year" => Year,
        "Rating" => Rating,
        "Runtime" => Runtime,
        "Poster" => Poster,
        "Backdrop" => Backdrop,
        "Logo" => Logo,
        "Status" => Status,
        "Genres" => Genres,
        "Locations" => Locations,
        "People" => People,
        _ => null
    };

    internal MetadataValues Observed(object response)
        => this with
        {
            Provenance = ValueFields.Where(field => MetadataProvenance.Present(Value(field)))
            .ToDictionary(field => field, _ => new MetadataFieldProvenance([MetadataProvenance.Observation(response)]), StringComparer.Ordinal)
        };

    internal ManagedItem ApplyProvenance(ManagedItem result, ManagedItem previous, PluginConfiguration config, bool episode)
    {
        if (!ValueFields.Any(field => MetadataProvenance.Present(Value(field)))) return result;
        var provenance = MetadataProvenance.Clone(previous.MetadataProvenance);
        foreach (var field in ValueFields)
        {
            if (!MetadataProvenance.Present(Value(field))) continue;
            var target = field switch
            {
                "Name" => !episode && result.Type == "series" ? "SeriesName" : "Name",
                "Overview" => !episode && result.Type == "series" ? "SeriesDescription" : "Description",
                "Released" => !episode && result.Type == "series" ? "SeriesReleased" : "Released",
                "Rating" => episode ? "EpisodeCommunityRating" : "CommunityRating",
                "Runtime" => episode ? "EpisodeRunTimeTicks" : "RunTimeTicks",
                "Poster" => episode ? "ThumbnailUrl" : "PosterUrl",
                "Backdrop" => "BackdropUrl",
                "Logo" => "LogoUrl",
                "Status" => "SeriesStatus",
                "Locations" => "ProductionLocations",
                "People" => episode ? "EpisodePeople" : "People",
                _ => field
            };
            if (episode && field is "Year" or "Backdrop" or "Logo" or "Status" or "Genres" or "Locations") continue;
            var policy = field switch
            {
                "Released" or "Year" => "ReleaseDate",
                "Rating" => "Ratings",
                "Poster" or "Backdrop" or "Logo" => "Images",
                "Locations" => "ProductionLocations",
                _ => field
            };
            if (MetadataPolicy.Wants(config, policy, !MetadataProvenance.Present(MetadataProvenance.Value(previous, target))))
                provenance[target] = Provenance.GetValueOrDefault(field) ?? new([new("Unknown", null, null)]);
            else if (provenance.TryGetValue(target, out var retained))
                provenance[target] = retained with { PreservationReason = "MetadataUpdatePolicy" };
        }
        return result with { MetadataProvenance = provenance };
    }

    internal MetadataValues FromParent(ManagedItem parent)
        => this with
        {
            Provenance = ValueFields.ToDictionary(field => field, field =>
            parent.MetadataProvenance.GetValueOrDefault(field switch
            {
                "Name" => parent.Type == "series" ? "SeriesName" : "Name",
                "Overview" => parent.Type == "series" ? "SeriesDescription" : "Description",
                "Released" => parent.Type == "series" ? "SeriesReleased" : "Released",
                "Rating" => "CommunityRating",
                "Runtime" => "RunTimeTicks",
                "Poster" => "PosterUrl",
                "Backdrop" => "BackdropUrl",
                "Logo" => "LogoUrl",
                "Status" => "SeriesStatus",
                "Locations" => "ProductionLocations",
                _ => field
            }) ?? new MetadataFieldProvenance([new("Unknown", null, null)]), StringComparer.Ordinal)
        };

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
        People = People.Length > 0 ? People : other.People,
        Provenance = ValueFields.Where(field => MetadataProvenance.Present(Value(field)) || MetadataProvenance.Present(other.Value(field)))
            .ToDictionary(field => field, field => (MetadataProvenance.Present(Value(field)) ? Provenance : other.Provenance)
                .GetValueOrDefault(field) ?? new MetadataFieldProvenance([new("Unknown", null, null)]), StringComparer.Ordinal)
    };
}
