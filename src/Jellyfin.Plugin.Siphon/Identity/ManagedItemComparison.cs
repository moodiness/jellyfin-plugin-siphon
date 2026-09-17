using System.Security.Cryptography;
using System.Text.Json;

namespace Jellyfin.Plugin.Siphon.Identity;

/// <summary>Compares publication content, not collection identities or absence bookkeeping.</summary>
public static class ManagedItemComparison
{
    public static string Fingerprint(ManagedItem item)
    {
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        JsonSerializer.Serialize(stream, item with
        {
            ProviderIds = CanonicalIds(item.ProviderIds),
            EpisodeProviderIds = CanonicalIds(item.EpisodeProviderIds),
            MissingSinceUtc = null,
            MissingOwners = []
        });
        stream.FlushFinalBlock();
        return Convert.ToHexString(hash.Hash!);
    }

    private static Dictionary<string, string> CanonicalIds(Dictionary<string, string> ids)
        => ids.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key.ToUpperInvariant(), pair => pair.Value, StringComparer.Ordinal);

    public static bool Equals(ManagedItem left, ManagedItem right)
        => ReferenceEquals(left, right) || (left.Key == right.Key
            && left.Type == right.Type && left.ContentId == right.ContentId && left.ContentKey == right.ContentKey
            && left.VideoId == right.VideoId && left.Name == right.Name && left.SeriesName == right.SeriesName
            && left.Year == right.Year && left.Season == right.Season && left.Episode == right.Episode && left.Path == right.Path
            && left.Description == right.Description && left.SeriesDescription == right.SeriesDescription
            && left.PosterUrl == right.PosterUrl && left.SeasonPosterUrl == right.SeasonPosterUrl
            && left.BackdropUrl == right.BackdropUrl && left.LogoUrl == right.LogoUrl && left.ThumbnailUrl == right.ThumbnailUrl
            && left.Released == right.Released && left.SeriesReleased == right.SeriesReleased
            && left.CommunityRating == right.CommunityRating && left.RunTimeTicks == right.RunTimeTicks
            && left.OfficialRating == right.OfficialRating && left.SeriesStatus == right.SeriesStatus
            && left.EpisodeCommunityRating == right.EpisodeCommunityRating && left.EpisodeRunTimeTicks == right.EpisodeRunTimeTicks
            && left.EpisodeOfficialRating == right.EpisodeOfficialRating && left.EpisodeBackdropUrl == right.EpisodeBackdropUrl
            && left.EpisodeLogoUrl == right.EpisodeLogoUrl
            && DictionaryEquals(left.ProviderIds, right.ProviderIds) && DictionaryEquals(left.EpisodeProviderIds, right.EpisodeProviderIds)
            && left.Owners.AsSpan().SequenceEqual(right.Owners)
            && left.Genres.AsSpan().SequenceEqual(right.Genres)
            && left.ProductionLocations.AsSpan().SequenceEqual(right.ProductionLocations)
            && left.People.AsSpan().SequenceEqual(right.People)
            && left.EpisodeGenres.AsSpan().SequenceEqual(right.EpisodeGenres)
            && left.EpisodeProductionLocations.AsSpan().SequenceEqual(right.EpisodeProductionLocations)
            && left.EpisodePeople.AsSpan().SequenceEqual(right.EpisodePeople)
            && left.StreamIdentities.AsSpan().SequenceEqual(right.StreamIdentities));

    private static bool DictionaryEquals(Dictionary<string, string> left, Dictionary<string, string> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var (key, value) in left)
        {
            // State reloads may use a different dictionary comparer.
            if (!right.Any(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase) && entry.Value == value)) return false;
        }
        return true;
    }
}
