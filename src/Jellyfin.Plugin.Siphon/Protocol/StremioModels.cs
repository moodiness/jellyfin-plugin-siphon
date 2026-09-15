using System.Text.Json;
using Jellyfin.Plugin.Siphon.Identity;

namespace Jellyfin.Plugin.Siphon.Protocol;

public sealed class StremioManifest
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public List<StremioResource> Resources { get; init; } = [];
    public List<string> Types { get; init; } = [];
    public List<string>? IdPrefixes { get; init; }
    public List<StremioCatalog> Catalogs { get; init; } = [];
}

public sealed record StremioResource(string Name, List<string>? Types = null, List<string>? IdPrefixes = null, bool IsObject = false);
public sealed record StremioExtra(string Name, bool IsRequired, List<string> Options, int? OptionsLimit);
public sealed class StremioCatalog
{
    public string Type { get; init; } = "";
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public List<StremioExtra> Extras { get; init; } = [];
    public IReadOnlyList<StremioExtra> GetExtras() => Extras;
}
public sealed class StremioMeta
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public string Name { get; init; } = "";
    public Dictionary<string, string> ProviderIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Description { get; init; }
    public string? Poster { get; init; }
    public string? Released { get; init; }
    public List<string> Genres { get; init; } = [];
    public string? ReleaseInfo { get; init; }
    public string? Year { get; init; }
    public List<StremioVideo> Videos { get; init; } = [];
    public bool VideosComplete { get; init; } = true;
}
public sealed record StremioVideo(string Id, string Name, int? Season, int? Episode, string? Released)
{
    public Dictionary<string, string> ProviderIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Description { get; init; }
    public string? Thumbnail { get; init; }
}
public sealed record StremioStream(string? Url, string Name, string Description, IReadOnlyDictionary<string, string> RequestHeaders, string? FileName, long? Size, IReadOnlyList<string>? CountryWhitelist, bool UnsupportedHeaders);

/// <summary>Bounded, tolerant parsing; malformed array members are skipped, not coerced.</summary>
public static class StremioJson
{
    private static JsonElement Field(JsonElement e, string key, string? alias = null)
    {
        if (e.ValueKind != JsonValueKind.Object) return default;
        if (e.TryGetProperty(key, out var value)) return value;
        return alias is not null && e.TryGetProperty(alias, out value) ? value : default;
    }
    private static string? Text(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    private static string? Text(JsonElement e, string key, string? alias = null) => Text(Field(e, key, alias));
    private static int? Number(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) ? n : null;
    private static IEnumerable<JsonElement> Array(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Array) return [];
        if (e.GetArrayLength() > 10000) throw new StremioException("Addon array exceeds the item limit.");
        return e.EnumerateArray();
    }
    private static List<string>? Strings(JsonElement e) => e.ValueKind == JsonValueKind.Array ? Array(e).Select(Text).OfType<string>().ToList() : null;
    private static JsonElement FirstField(JsonElement e, params string[] names)
    {
        foreach (var name in names)
        {
            var value = Field(e, name);
            if (value.ValueKind != JsonValueKind.Undefined) return value;
        }
        return default;
    }
    private static Dictionary<string, string> ExternalIds(JsonElement e, string id)
    {
        var ids = Field(e, "ids");
        var explicitIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Add("Imdb", FirstField(ids, "imdb", "Imdb", "imdb_id", "imdbId"), FirstField(e, "imdb_id", "imdbId"));
        Add("Tmdb", FirstField(ids, "tmdb", "Tmdb", "tmdb_id", "tmdbId", "moviedb", "moviedb_id"), FirstField(e, "tmdb_id", "tmdbId", "moviedb_id", "moviedbId"));
        Add("Tvdb", FirstField(ids, "tvdb", "Tvdb", "tvdb_id", "tvdbId"), FirstField(e, "tvdb_id", "tvdbId"));
        Add("MyAnimeList", FirstField(ids, "mal", "myanimelist", "MyAnimeList", "mal_id"), FirstField(e, "mal_id", "malId", "myanimelist_id", "myanimelistId"));
        return ContentIdentity.ProviderIds(id, explicitIds);

        void Add(string provider, JsonElement canonical, JsonElement alias)
        {
            var value = ids.ValueKind == JsonValueKind.Null ? ids : canonical.ValueKind != JsonValueKind.Undefined ? canonical : alias;
            if (value.ValueKind == JsonValueKind.Undefined) return;
            explicitIds[provider] = Text(value) ?? (value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "");
        }
    }
    public static StremioManifest Manifest(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(Text(root, "id"))) throw new StremioException("Invalid addon manifest.");
        var resources = new List<StremioResource>();
        foreach (var r in Array(Field(root, "resources")))
        {
            if (Text(r) is { } name) resources.Add(new(name));
            else if (Text(r, "name") is { } resourceName) resources.Add(new(resourceName, Strings(Field(r, "types")), Strings(Field(r, "idPrefixes", "id_prefixes")), true));
        }
        var catalogs = new List<StremioCatalog>();
        foreach (var c in Array(Field(root, "catalogs")))
        {
            if (Text(c, "type") is not { Length: > 0 } type || Text(c, "id") is not { Length: > 0 } id) continue;
            var extras = new List<StremioExtra>();
            var canonical = Field(c, "extra");
            if (canonical.ValueKind != JsonValueKind.Undefined)
            {
                foreach (var e in Array(canonical))
                    if (Text(e, "name") is { Length: > 0 } name) extras.Add(new(name, Field(e, "isRequired", "is_required").ValueKind == JsonValueKind.True, Strings(Field(e, "options")) ?? [], Number(Field(e, "optionsLimit", "options_limit"))));
            }
            else
            {
                var required = Strings(Field(c, "extraRequired", "extra_required")) ?? [];
                foreach (var name in (Strings(Field(c, "extraSupported", "extra_supported")) ?? []).Concat(required).Distinct(StringComparer.Ordinal)) extras.Add(new(name, required.Contains(name, StringComparer.Ordinal), [], null));
            }
            catalogs.Add(new() { Type = type, Id = id, Name = Text(c, "name") ?? id, Extras = extras });
        }
        return new() { Id = Text(root, "id")!, Name = Text(root, "name") ?? "Addon", Description = Text(root, "description") ?? "", Resources = resources, Types = Strings(Field(root, "types")) ?? [], IdPrefixes = Strings(Field(root, "idPrefixes", "id_prefixes")), Catalogs = catalogs };
    }
    public static StremioMeta? Meta(JsonElement e)
    {
        if (Text(e, "id") is not { Length: > 0 } id || Text(e, "type") is not { Length: > 0 } type || Text(e, "name") is not { Length: > 0 } name) return null;
        var videos = new List<StremioVideo>();
        var videoElements = Array(Field(e, "videos")).ToArray();
        foreach (var v in videoElements)
            if (Text(v, "id") is { Length: > 0 } videoId) videos.Add(new(videoId, Text(v, "title", "name") ?? name, Number(Field(v, "season")), Number(Field(v, "episode", "number")), Text(v, "released", "firstAired"))
            {
                ProviderIds = ExternalIds(v, ""),
                Description = Text(v, "description", "overview"),
                Thumbnail = Text(v, "thumbnail")
            });
        var year = Field(e, "year");
        return new()
        {
            Id = id,
            Type = type,
            Name = name,
            ProviderIds = ExternalIds(e, id),
            Description = Text(e, "description", "overview"),
            Poster = Text(e, "poster"),
            Released = Text(e, "released", "firstAired"),
            Genres = Strings(Field(e, "genres")) ?? [],
            ReleaseInfo = Text(e, "releaseInfo", "release_info"),
            Year = Text(year) ?? Number(year)?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Videos = videos,
            VideosComplete = videos.Count == videoElements.Length && videos.All(v => v.Season is >= 0 and <= 9999 && v.Episode is >= 1 and <= 9999)
        };
    }
    public static StremioCatalogPage Catalog(JsonElement root)
    {
        var entries = RequiredArray(root, "metas").ToArray();
        var items = entries.Select(Meta).OfType<StremioMeta>().ToArray();
        return new StremioCatalogPage(items, entries.Length, items.Length == entries.Length);
    }
    public static StremioMeta? MetaResponse(JsonElement root) => Meta(Field(root, "meta"));
    private static IEnumerable<JsonElement> RequiredArray(JsonElement root, string name)
    {
        var e = Field(root, name);
        if (e.ValueKind != JsonValueKind.Array) throw new StremioException("Invalid addon response shape.");
        return Array(e);
    }
    public static IReadOnlyList<StremioStream> Streams(JsonElement root)
    {
        var result = new List<StremioStream>();
        foreach (var e in RequiredArray(root, "streams"))
        {
            if (Text(e, "url") is not { Length: > 0 } url) continue;
            var hints = Field(e, "behaviorHints", "behavior_hints");
            var proxy = Field(hints, "proxyHeaders", "proxy_headers");
            var request = Field(proxy, "request");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var unsupported = request.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object);
            if (request.ValueKind == JsonValueKind.Object)
                foreach (var h in request.EnumerateObject())
                    if (Text(h.Value) is { } value) headers[h.Name] = value; else unsupported = true;
            var response = Field(proxy, "response");
            unsupported |= response.ValueKind == JsonValueKind.Object && response.EnumerateObject().Any();
            var size = Field(hints, "videoSize", "video_size");
            result.Add(new(url, Text(e, "name") ?? "", Text(e, "description", "title") ?? "", headers, Text(hints, "filename"), size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var n) && n >= 0 ? n : null, Strings(Field(hints, "countryWhitelist", "country_whitelist")), unsupported));
        }
        return result;
    }
}
public sealed class StremioException(string message) : Exception(message);
