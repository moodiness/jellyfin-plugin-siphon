using System.Collections;
using System.Runtime.CompilerServices;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Protocol;

namespace Jellyfin.Plugin.Siphon.Metadata;

/// <summary>Records evidence at acquisition/merge time; configuration is never historic evidence.</summary>
public static class MetadataProvenance
{
    private sealed record Receipt(MetadataObservation Observation);
    private static readonly ConditionalWeakTable<object, Receipt> Receipts = new();
    private static readonly MetadataObservation Unknown = new("Unknown", null, null);
    internal static readonly string[] Fields = ["Name", "SeriesName", "Year", "Description", "SeriesDescription", "PosterUrl", "SeasonPosterUrl", "BackdropUrl", "LogoUrl", "ThumbnailUrl", "Released", "SeriesReleased", "CommunityRating", "RunTimeTicks", "OfficialRating", "SeriesStatus", "Genres", "ProductionLocations", "People", "EpisodeCommunityRating", "EpisodeRunTimeTicks", "EpisodeOfficialRating", "EpisodeBackdropUrl", "EpisodeLogoUrl", "EpisodeGenres", "EpisodeProductionLocations", "EpisodePeople", "ProviderIds", "EpisodeProviderIds"];
    private static readonly string[] ParentFields = ["SeriesName", "SeriesDescription", "SeriesReleased", "Year", "CommunityRating", "RunTimeTicks", "OfficialRating", "PosterUrl", "BackdropUrl", "LogoUrl", "Genres", "ProductionLocations", "People", "SeriesStatus", "ProviderIds"];

    public static void ObserveAddon(StremioMeta value, string installationId, bool selected = false)
        => Observe(value, new(selected ? "SelectedAddon" : "Addon", Guid.TryParse(installationId, out var id) ? id.ToString("N") : null, DateTimeOffset.UtcNow));

    internal static void Observe(object value, MetadataObservation observation)
    {
        Receipts.Remove(value);
        Receipts.Add(value, new(observation));
    }

    internal static MetadataObservation Observation(object? value)
        => value is not null && Receipts.TryGetValue(value, out var receipt) ? receipt.Observation : Unknown;

    public static Dictionary<string, MetadataFieldProvenance> Clone(Dictionary<string, MetadataFieldProvenance>? value)
        => Fields.Where(field => value?.ContainsKey(field) == true).ToDictionary(field => field,
            field => new MetadataFieldProvenance(value![field].Sources.Take(4).ToArray(), value[field].PreservationReason), StringComparer.Ordinal);

    internal static object? Value(ManagedItem item, string field) => field switch
    {
        "Name" => item.Name,
        "SeriesName" => item.SeriesName,
        "Year" => item.Year,
        "Description" => item.Description,
        "SeriesDescription" => item.SeriesDescription,
        "PosterUrl" => item.PosterUrl,
        "SeasonPosterUrl" => item.SeasonPosterUrl,
        "BackdropUrl" => item.BackdropUrl,
        "LogoUrl" => item.LogoUrl,
        "ThumbnailUrl" => item.ThumbnailUrl,
        "Released" => item.Released,
        "SeriesReleased" => item.SeriesReleased,
        "CommunityRating" => item.CommunityRating,
        "RunTimeTicks" => item.RunTimeTicks,
        "OfficialRating" => item.OfficialRating,
        "SeriesStatus" => item.SeriesStatus,
        "Genres" => item.Genres,
        "ProductionLocations" => item.ProductionLocations,
        "People" => item.People,
        "EpisodeCommunityRating" => item.EpisodeCommunityRating,
        "EpisodeRunTimeTicks" => item.EpisodeRunTimeTicks,
        "EpisodeOfficialRating" => item.EpisodeOfficialRating,
        "EpisodeBackdropUrl" => item.EpisodeBackdropUrl,
        "EpisodeLogoUrl" => item.EpisodeLogoUrl,
        "EpisodeGenres" => item.EpisodeGenres,
        "EpisodeProductionLocations" => item.EpisodeProductionLocations,
        "EpisodePeople" => item.EpisodePeople,
        "ProviderIds" => item.ProviderIds,
        "EpisodeProviderIds" => item.EpisodeProviderIds,
        _ => null
    };

    internal static bool Present(object? value) => value switch { null => false, string text => !string.IsNullOrWhiteSpace(text), ICollection list => list.Count > 0, _ => true };
    internal static bool Same(object? left, object? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is Dictionary<string, string> ids && right is Dictionary<string, string> other)
            return ids.Count == other.Count && ids.All(pair => other.TryGetValue(pair.Key, out var value) && pair.Value == value);
        return left is ICollection a && right is ICollection b
            ? a.Cast<object>().SequenceEqual(b.Cast<object>()) : Equals(left, right);
    }

    internal static ManagedItem Merge(ManagedItem result, ManagedItem incoming, ManagedItem previous, string retainedReason = "IncomingValueMissing")
    {
        var fields = new Dictionary<string, MetadataFieldProvenance>(StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            var value = Value(result, field);
            if (!Present(value)) continue;
            var fresh = incoming.MetadataProvenance.GetValueOrDefault(field) ?? new([Unknown]);
            var old = previous.MetadataProvenance.GetValueOrDefault(field) ?? new([Unknown]);
            fields[field] = Same(value, Value(incoming, field)) ? fresh
                : Same(value, Value(previous, field)) ? old with { PreservationReason = retainedReason }
                : Combine(fresh, old) with { PreservationReason = "MergedWithRetainedValues" };
        }
        return result with { MetadataProvenance = fields };
    }

    internal static MetadataFieldProvenance Combine(MetadataFieldProvenance first, MetadataFieldProvenance second)
    {
        var sources = first.Sources.Concat(second.Sources).GroupBy(source => (source.Origin, source.InstallationId))
            .Select(group => group.MinBy(source => source.ObservedAtUtc ?? DateTimeOffset.MinValue)!).Take(5).ToArray();
        // A fifth contributor must remain visibly unknown rather than silently claiming completeness.
        return new(sources.Length <= 4 ? sources : sources.Take(3).Append(Unknown).ToArray());
    }

    internal static ManagedItem CopyParent(ManagedItem result, ManagedItem parent)
    {
        var fields = Clone(result.MetadataProvenance);
        foreach (var field in ParentFields)
        {
            var source = field switch { "SeriesName" => "Name", "SeriesDescription" => "Description", "SeriesReleased" => "Released", _ => field };
            if (parent.Type == "series" && parent.SeriesName is not null) source = field;
            if (!Same(Value(result, field), Value(parent, source))) continue;
            if (Present(Value(result, field))) fields[field] = parent.MetadataProvenance.GetValueOrDefault(source) ?? new([Unknown]);
            else fields.Remove(field);
        }
        return result with { MetadataProvenance = fields };
    }

    internal static ManagedItem Retain(ManagedItem result, ManagedItem previous, IEnumerable<string> fields, string reason)
    {
        var provenance = Clone(result.MetadataProvenance);
        foreach (var field in fields)
        {
            if (Present(Value(result, field))) provenance[field] = (previous.MetadataProvenance.GetValueOrDefault(field) ?? new([Unknown])) with { PreservationReason = reason };
            else provenance.Remove(field);
        }
        return result with { MetadataProvenance = provenance };
    }

    internal static ManagedItem Retain(ManagedItem result, ManagedItemSnapshot previous, IEnumerable<string> fields, string reason)
    {
        var provenance = Clone(result.MetadataProvenance);
        foreach (var field in fields)
        {
            if (Present(Value(result, field)))
            {
                var evidence = previous.MetadataProvenance.GetValueOrDefault(field);
                provenance[field] = new(evidence?.Sources.ToArray() ?? [Unknown], reason);
            }
            else provenance.Remove(field);
        }
        return result with { MetadataProvenance = provenance };
    }

    internal static ManagedItem Set(ManagedItem item, string field, MetadataObservation observation)
        => Set(item, field, new MetadataFieldProvenance([observation]));

    internal static ManagedItem Set(ManagedItem item, string field, MetadataFieldProvenance evidence)
    {
        var provenance = Clone(item.MetadataProvenance);
        provenance[field] = evidence;
        return item with { MetadataProvenance = provenance };
    }

    internal static ManagedItem Contribute(ManagedItem item, string field, MetadataObservation observation)
    {
        var provenance = Clone(item.MetadataProvenance);
        var incoming = new MetadataFieldProvenance([observation]);
        provenance[field] = provenance.TryGetValue(field, out var previous)
            ? Combine(incoming, previous)
            : Present(Value(item, field)) ? Combine(incoming, new([Unknown])) : incoming;
        return item with { MetadataProvenance = provenance };
    }

    internal static ManagedItem FromAddon(ManagedItem item, StremioMeta full, StremioMeta fallback, StremioMeta identity)
    {
        var fields = new Dictionary<string, MetadataFieldProvenance>(StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            if (!Present(Value(item, field))) continue;
            if (field == "ProviderIds")
            {
                var fullIds = ContentIdentity.ProviderIds(full.Id, full.ProviderIds);
                var previewIds = ContentIdentity.ProviderIds(identity.Id, identity.ProviderIds);
                fields[field] = new(item.ProviderIds.Select(pair =>
                    fullIds.TryGetValue(pair.Key, out var current) && current == pair.Value ? Observation(full)
                    : previewIds.TryGetValue(pair.Key, out var original) && original == pair.Value ? Observation(identity)
                    : Unknown).Distinct().Take(3).ToArray());
                continue;
            }
            var incoming = MetaValue(full, field);
            var previous = MetaValue(fallback, field);
            fields[field] = Value(item, field) is ICollection collection
                ? CollectionEvidence(collection, incoming, previous, Observation(full), Observation(fallback))
                : new([Present(incoming) ? Observation(full) : Present(previous) ? Observation(fallback) : Unknown]);
        }
        return item with { MetadataProvenance = fields };
    }

    internal static ManagedItem FromEpisode(ManagedItem item, StremioVideo full, StremioVideo? fallback, StremioMeta source, StremioMeta fallbackSource)
    {
        var fields = Clone(item.MetadataProvenance);
        foreach (var field in new[] { "Name", "Description", "Released", "ThumbnailUrl", "EpisodeBackdropUrl", "EpisodeLogoUrl", "EpisodeCommunityRating", "EpisodeRunTimeTicks", "EpisodeOfficialRating", "EpisodeGenres", "EpisodeProductionLocations", "EpisodePeople", "EpisodeProviderIds" })
        {
            if (!Present(Value(item, field))) { fields.Remove(field); continue; }
            var incoming = VideoValue(full, field);
            var previous = fallback is null ? null : VideoValue(fallback, field);
            fields[field] = Value(item, field) is ICollection collection
                ? CollectionEvidence(collection, incoming, previous, Observation(source), Observation(fallbackSource))
                : new([Present(incoming) ? Observation(source) : Present(previous) ? Observation(fallbackSource) : Unknown]);
        }
        return item with { MetadataProvenance = fields };
    }

    private static MetadataFieldProvenance CollectionEvidence(ICollection retained, object? incoming, object? previous,
        MetadataObservation source, MetadataObservation fallbackSource)
    {
        var hasIncoming = false;
        var hasPrevious = false;
        var hasUnknown = false;
        void Record(bool current, bool fallback)
        {
            hasIncoming |= current;
            hasPrevious |= fallback;
            hasUnknown |= !current && !fallback;
        }

        switch (retained)
        {
            case string[] values:
                var currentValues = (incoming as IEnumerable<string> ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var fallbackValues = (previous as IEnumerable<string> ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var value in values)
                {
                    var current = currentValues.Contains(value);
                    Record(current, !current && fallbackValues.Contains(value));
                }
                break;
            case Dictionary<string, string> ids:
                var currentIds = incoming as Dictionary<string, string>;
                var fallbackIds = previous as Dictionary<string, string>;
                foreach (var (provider, id) in ids)
                {
                    var current = currentIds is not null && currentIds.TryGetValue(provider, out var currentId) && currentId == id;
                    Record(current, !current && fallbackIds is not null && fallbackIds.TryGetValue(provider, out var fallbackId) && fallbackId == id);
                }
                break;
            case ManagedPerson[] people:
                var currentPeople = incoming as ManagedPerson[] ?? [];
                var fallbackPeople = previous as ManagedPerson[] ?? [];
                foreach (var person in people)
                {
                    var current = Array.Find(currentPeople, value => value.Type == person.Type && value.Name.Equals(person.Name, StringComparison.OrdinalIgnoreCase));
                    var fallback = Array.Find(fallbackPeople, value => value.Type == person.Type && value.Name.Equals(person.Name, StringComparison.OrdinalIgnoreCase));
                    // Credits merge by identity, but missing role/artwork can still retain fallback evidence.
                    Record(current is not null, fallback is not null && (current is null
                        || !Present(current.Role) && Present(person.Role) && person.Role == fallback.Role
                        || !Present(current.PhotoUrl) && Present(person.PhotoUrl) && person.PhotoUrl == fallback.PhotoUrl));
                }
                break;
        }

        MetadataFieldProvenance? evidence = hasIncoming ? new([source]) : null;
        if (hasPrevious) evidence = evidence is null ? new([fallbackSource]) : Combine(evidence, new([fallbackSource]));
        if (hasUnknown) evidence = evidence is null ? new([Unknown]) : Combine(evidence, new([Unknown]));
        return evidence ?? new([Unknown]);
    }

    private static object? MetaValue(StremioMeta item, string field) => field switch
    {
        "Name" => item.Name,
        "Description" => item.Description,
        "Year" => CatalogSyncService.GetYear(item),
        "Released" => item.Released,
        "PosterUrl" => item.Poster,
        "BackdropUrl" => item.Background,
        "LogoUrl" => item.Logo,
        "CommunityRating" => item.CommunityRating,
        "RunTimeTicks" => item.RunTimeTicks,
        "OfficialRating" => item.OfficialRating,
        "SeriesStatus" => item.Status,
        "Genres" => item.Genres,
        "ProductionLocations" => item.ProductionLocations,
        "People" => item.People,
        "ProviderIds" => item.ProviderIds,
        _ => null
    };

    private static object? VideoValue(StremioVideo item, string field) => field switch
    {
        "Name" => item.Name,
        "Description" => item.Description,
        "Released" => item.Released,
        "ThumbnailUrl" => item.Thumbnail,
        "EpisodeBackdropUrl" => item.Background,
        "EpisodeLogoUrl" => item.Logo,
        "EpisodeCommunityRating" => item.CommunityRating,
        "EpisodeRunTimeTicks" => item.RunTimeTicks,
        "EpisodeOfficialRating" => item.OfficialRating,
        "EpisodeGenres" => item.Genres,
        "EpisodeProductionLocations" => item.ProductionLocations,
        "EpisodePeople" => item.People,
        "EpisodeProviderIds" => item.ProviderIds,
        _ => null
    };
}
