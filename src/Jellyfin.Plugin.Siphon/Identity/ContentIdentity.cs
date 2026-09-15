using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Siphon.Identity;

/// <summary>Separates content identity from exact addon resource identifiers.</summary>
public static class ContentIdentity
{
    private static readonly (string Name, string Prefix, string[] Aliases)[] Providers =
    [
        ("Imdb", "imdb", ["imdb_id", "imdbId"]),
        ("Tmdb", "tmdb", ["tmdb_id", "tmdbId", "moviedb", "moviedb_id", "moviedbId"]),
        ("Tvdb", "tvdb", ["tvdb_id", "tvdbId"]),
        ("MyAnimeList", "mal", ["mal", "mal_id", "malId", "myanimelist_id", "myanimelistId"])
    ];

    public static Dictionary<string, string> ProviderIds(string id, IReadOnlyDictionary<string, string>? explicitIds = null)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (TryParse(id, out var originalProvider, out var originalValue, out _)) result[originalProvider] = originalValue;
        if (explicitIds is null) return result;

        foreach (var provider in Providers)
        {
            if (!TryExplicit(explicitIds, provider.Name, provider.Aliases, out var value)) continue;
            if (Normalize(provider.Name, value) is { } normalized) result[provider.Name] = normalized;
        }

        return result;
    }

    public static string Key(string mediaKind, string originalId, string addonInstallationId, IReadOnlyDictionary<string, string> ids)
    {
        if (mediaKind is not ("movie" or "series")) throw new ArgumentException("Unsupported library media kind.", nameof(mediaKind));
        var normalized = ProviderIds(originalId, ids);
        if (TryParse(originalId, out var originalProvider, out _, out var originalKind)
            && (originalKind is null || originalKind == mediaKind)
            && normalized.TryGetValue(originalProvider, out var originalValue))
        {
            return KnownKey(mediaKind, originalProvider, originalValue);
        }

        // A typed TMDB identifier cannot be reinterpreted as the other media namespace.
        if (originalKind is not null && originalKind != mediaKind) return OpaqueKey(mediaKind, originalId, addonInstallationId);
        foreach (var provider in Providers)
            if (normalized.TryGetValue(provider.Name, out var value)) return KnownKey(mediaKind, provider.Name, value);
        return OpaqueKey(mediaKind, originalId, addonInstallationId);
    }

    public static IEnumerable<StreamIdentity> MovieAliases(string originalType, string originalId, IReadOnlyDictionary<string, string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { originalId };
        yield return new(originalType, originalId);
        if (originalType is not ("movie" or "anime")) yield break;
        if (TryParse(originalId, out _, out _, out var originalKind) && originalKind == "series") yield break;

        var normalized = ProviderIds(originalId, ids);
        foreach (var provider in Providers)
        {
            if (!normalized.TryGetValue(provider.Name, out var value)) continue;
            if (provider.Name == "Imdb" && seen.Add(value)) yield return new(originalType, value);
            var prefixed = provider.Prefix + ":" + value;
            if (seen.Add(prefixed)) yield return new(originalType, prefixed);
            var alternate = provider.Name switch
            {
                "Tmdb" => "tmdb:movie:" + value,
                "MyAnimeList" => "myanimelist:" + value,
                _ => null
            };
            if (alternate is not null && seen.Add(alternate)) yield return new(originalType, alternate);
        }
    }

    private static string KnownKey(string kind, string provider, string value)
        => kind + ":" + (provider == "Imdb" ? value : Providers.First(p => p.Name == provider).Prefix + ":" + value);

    private static string OpaqueKey(string kind, string originalId, string installationId)
        => kind + ":opaque:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installationId.Length + ":" + installationId + originalId.Length + ":" + originalId)));

    private static bool TryExplicit(IReadOnlyDictionary<string, string> ids, string name, string[] aliases, out string value)
    {
        if (TryField(ids, name, out value)) return true;
        foreach (var alias in aliases)
            if (TryField(ids, alias, out value)) return true;
        return false;
    }

    private static bool TryField(IReadOnlyDictionary<string, string> ids, string name, out string value)
    {
        if (ids.TryGetValue(name, out value!)) return true;
        foreach (var pair in ids)
        {
            if (!pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = pair.Value;
            return true;
        }

        value = "";
        return false;
    }

    private static string? Normalize(string provider, string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (TryParse(value, out var parsedProvider, out var parsedValue, out _)) return parsedProvider == provider ? parsedValue : null;
        return provider == "Imdb" ? null : Numeric(value);
    }

    private static bool TryParse(string id, out string provider, out string value, out string? mediaKind)
    {
        provider = "";
        value = "";
        mediaKind = null;
        if (IsImdb(id))
        {
            provider = "Imdb";
            value = id;
            return true;
        }

        var colon = id.IndexOf(':');
        if (colon < 1) return false;
        var prefix = id.AsSpan(0, colon);
        foreach (var known in Providers)
        {
            if (!prefix.Equals(known.Prefix, StringComparison.OrdinalIgnoreCase)
                && !(known.Name == "MyAnimeList" && prefix.Equals("myanimelist", StringComparison.OrdinalIgnoreCase))) continue;
            var candidate = id[(colon + 1)..];
            if (known.Name == "Tmdb")
            {
                if (candidate.StartsWith("movie:", StringComparison.OrdinalIgnoreCase)) { mediaKind = "movie"; candidate = candidate[6..]; }
                else if (candidate.StartsWith("tv:", StringComparison.OrdinalIgnoreCase)) { mediaKind = "series"; candidate = candidate[3..]; }
            }

            var normalized = known.Name == "Imdb" ? IsImdb(candidate) ? candidate : null : Numeric(candidate);
            if (normalized is null) { mediaKind = null; return false; }
            provider = known.Name;
            value = normalized;
            return true;
        }

        return false;
    }

    private static bool IsImdb(string id)
        => id.Length is >= 9 and <= 12 && id.StartsWith("tt", StringComparison.Ordinal) && id.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0;

    private static string? Numeric(string value)
    {
        if (value.Length == 0 || value.AsSpan().IndexOfAnyExceptInRange('0', '9') >= 0) return null;
        var first = 0;
        while (first < value.Length && value[first] == '0') first++;
        return first == value.Length ? null : first == 0 ? value : value[first..];
    }
}
