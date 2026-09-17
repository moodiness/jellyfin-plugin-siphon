using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Metadata;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Siphon.Diagnostics;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Siphon/Items/{itemId:guid}/Inspection")]
public sealed class ItemInspectionController(ILibraryManager library, ISiphonStateStore state, ConfigurationAccessor configuration) : ControllerBase
{
    /// <summary>Local state and native publication receipts only. Never requests or exposes upstream resources.</summary>
    [HttpGet]
    public ActionResult<ItemInspectionResponse> Get(Guid itemId)
    {
        var target = library.GetItemById(itemId);
        if (target is not (Movie or Episode or Series or Season)) return NotFound();
        var key = target.GetProviderId("Siphon");
        if (string.IsNullOrWhiteSpace(key)) return NotFound();
        var contentKey = target is Season && key.LastIndexOf(":season:", StringComparison.Ordinal) is var separator && separator > 0
            ? key[..separator] : key;
        var managed = state.FindByKey(key) ?? (target is Series or Season ? state.FindByContentKey(contentKey) : null);
        if (managed is null) return NotFound();
        var now = DateTimeOffset.UtcNow;
        var fields = new List<ItemInspectionField>(32);
        var episode = target is Episode;
        var series = target is Series;
        var config = configuration.Current;

        void Add(string field, string policy, object? value, string managedField, bool includeManaged = true)
        {
            var locked = !MetadataPolicy.CanUpdate(target, config, policy, true, initialize: true);
            var receipt = ItemMetadataOwnership.Read(target, field);
            var changed = receipt is not null && receipt.Fingerprint != ItemMetadataOwnership.Fingerprint(value);
            var evidence = changed ? null : receipt?.Provenance;
            var present = MetadataProvenance.Present(value);
            var reason = locked ? target.IsLocked ? "ItemLocked" : "FieldLocked"
                : changed ? field.StartsWith("Image", StringComparison.Ordinal) ? "NativeArtworkChangedOrLocalized" : "NativeValueChangedSincePublication"
                : receipt is null ? "NoNativePublicationEvidence"
                : !MetadataPolicy.Wants(config, policy, !present) ? "MetadataUpdatePolicy"
                : evidence?.PreservationReason;
            if (field == "RunTimeTicks" && target is Video { Container: not null }
                && target.GetProviderId(Playback.NativeVersionService.SourceProvider) is not null)
                reason = locked ? reason : "NativeVersionRuntimePreserved";
            fields.Add(Describe("Native." + field, present, evidence, changed ? "ManualOrNative" : null, locked, reason, now));
            if (includeManaged)
                fields.Add(Describe("Managed." + managedField, MetadataProvenance.Present(MetadataProvenance.Value(managed, managedField)),
                    managed.MetadataProvenance.GetValueOrDefault(managedField), null, locked, null, now));
        }

        if (target is not Season)
        {
            Add("Name", "Name", target.Name, series ? "SeriesName" : "Name");
            Add("Overview", "Overview", target.Overview, series ? "SeriesDescription" : "Description");
            Add("PremiereDate", "ReleaseDate", target.PremiereDate, series ? "SeriesReleased" : "Released");
            Add("ProductionYear", "ReleaseDate", target.ProductionYear, "Year", !episode);
            Add("CommunityRating", "Ratings", target.CommunityRating, episode ? "EpisodeCommunityRating" : "CommunityRating");
            Add("RunTimeTicks", "Runtime", target.RunTimeTicks, episode ? "EpisodeRunTimeTicks" : "RunTimeTicks");
            Add("OfficialRating", "Ratings", target.OfficialRating, episode ? "EpisodeOfficialRating" : "OfficialRating");
            Add("Genres", "Genres", target.Genres, episode ? "EpisodeGenres" : "Genres");
            Add("ProductionLocations", "ProductionLocations", target.ProductionLocations, episode ? "EpisodeProductionLocations" : "ProductionLocations");
            Add("People", "People", ItemMetadataOwnership.People(library.GetPeople(target)), episode ? "EpisodePeople" : "People");
            if (target is Series show) Add("Status", "Status", show.Status?.ToString(), "SeriesStatus");
        }
        foreach (var imageType in target is Season ? new[] { ImageType.Primary } : new[] { ImageType.Primary, ImageType.Backdrop, ImageType.Logo })
        {
            var field = imageType switch
            {
                ImageType.Primary => target is Season ? "SeasonPosterUrl" : episode ? "ThumbnailUrl" : "PosterUrl",
                ImageType.Backdrop => episode ? "EpisodeBackdropUrl" : "BackdropUrl",
                _ => episode ? "EpisodeLogoUrl" : "LogoUrl"
            };
            // A content lookup can return an episode of another season. Do not display its artwork as this season's snapshot.
            Add("Image" + imageType, "Images", target.GetImageInfo(imageType, 0)?.Path, field,
                target is not Season season || season.IndexNumber == managed.Season);
        }
        return new ItemInspectionResponse(target.Id, target.GetType().Name, SyncDiagnostics.SafeName(target.Name), now,
            target.IsLocked, target.LockedFields.Select(field => field.ToString()).Take(32).ToArray(), fields.ToArray());
    }

    internal static ItemInspectionField Describe(string field, bool present, MetadataFieldProvenance? evidence,
        string? forcedOrigin, bool locked, string? reason, DateTimeOffset now)
    {
        var sources = (evidence?.Sources ?? []).Take(4).Select(SafeSource).Distinct().ToArray();
        var origins = sources.Select(source => source.Origin).Distinct(StringComparer.Ordinal).ToArray();
        var origin = forcedOrigin ?? (origins.Length == 1 ? origins[0] : origins.Length > 1 ? "Mixed" : "Unknown");
        var observed = sources.Where(source => source.ObservedAtUtc <= now).Select(source => source.ObservedAtUtc).Min();
        var created = sources.Where(source => source.CacheCreatedAtUtc <= now).Select(source => source.CacheCreatedAtUtc).Min();
        var expiry = sources.Select(source => source.CacheExpiresAtUtc).Min();
        return new(field, present, origin, sources.Select(SourceLabel).Distinct(StringComparer.Ordinal).ToArray(), observed,
            created, expiry, created.HasValue ? Math.Max(0, (now - created.Value).TotalSeconds) : null, locked,
            SafeReason(reason ?? evidence?.PreservationReason));
    }

    private static MetadataObservation SafeSource(MetadataObservation source)
    {
        var origin = source.Origin is "SelectedAddon" or "Addon" or "Tmdb" or "Tvdb" or "Fanart" or "MdbList" ? source.Origin : "Unknown";
        return source with { Origin = origin, InstallationId = Guid.TryParse(source.InstallationId, out var id) ? id.ToString("N") : null };
    }

    private static string SourceLabel(MetadataObservation source) => source.Origin switch
    {
        "SelectedAddon" => "Selected addon" + (source.InstallationId is null ? "" : " (" + source.InstallationId + ")"),
        "Addon" => "Addon" + (source.InstallationId is null ? "" : " (" + source.InstallationId + ")"),
        "Tmdb" => "TMDB",
        "Tvdb" => "TVDB",
        "Fanart" => "Fanart.tv",
        "MdbList" => "MDBList",
        _ => "Unknown"
    };

    private static string? SafeReason(string? value) => value is "ItemLocked" or "FieldLocked" or "NativeArtworkChangedOrLocalized"
        or "NativeValueChangedSincePublication" or "NoNativePublicationEvidence" or "MetadataUpdatePolicy"
        or "NativeVersionRuntimePreserved" or "IncomingValueMissing" or "MergedWithRetainedValues"
        or "NativeLockOrPreservationPolicy" ? value : null;
}

public sealed record ItemInspectionResponse(Guid ItemId, string ItemType, string Title, DateTimeOffset GeneratedAtUtc,
    bool IsLocked, string[] LockedFields, ItemInspectionField[] Fields);

public sealed record ItemInspectionField(string Field, bool HasValue, string Origin, string[] SourceLabels,
    DateTimeOffset? ObservedAtUtc, DateTimeOffset? CacheCreatedAtUtc, DateTimeOffset? CacheExpiresAtUtc, double? CacheAgeSeconds,
    bool IsLocked, string? PreservationReason);
