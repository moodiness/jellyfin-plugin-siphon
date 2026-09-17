using System.Globalization;
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
public sealed record StremioExtra(string Name, bool IsRequired, List<string> Options, int? OptionsLimit, string? Default = null);
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
    public string?[] SeasonPosters { get; init; } = [];
    public string? Background { get; init; }
    public string? Logo { get; init; }
    public float? CommunityRating { get; init; }
    public long? RunTimeTicks { get; init; }
    public string? OfficialRating { get; init; }
    public string? Status { get; init; }
    public string[] ProductionLocations { get; init; } = [];
    public ManagedPerson[] People { get; init; } = [];
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
    public string? Background { get; init; }
    public string? Logo { get; init; }
    public float? CommunityRating { get; init; }
    public long? RunTimeTicks { get; init; }
    public string? OfficialRating { get; init; }
    public string[] Genres { get; init; } = [];
    public string[] ProductionLocations { get; init; } = [];
    public ManagedPerson[] People { get; init; } = [];
}
public sealed record StremioStream(string? Url, string Name, string Description, IReadOnlyDictionary<string, string> RequestHeaders, string? FileName, long? Size, IReadOnlyList<string>? CountryWhitelist, bool UnsupportedHeaders);

/// <summary>Bounded, tolerant parsing; malformed array members are skipped, not coerced.</summary>
public static class StremioJson
{
    private static readonly string[] RuntimeSuffixes = ["minutes", "minute", "mins", "min", "m"];
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
    private static string? MetadataText(JsonElement e, int maximum = 1024)
    {
        var value = Text(e)?.Trim();
        return value is { Length: > 0 } && value.Length <= maximum ? value : null;
    }
    private static string? MetadataText(JsonElement e, string key, string? alias = null, int maximum = 1024)
        => MetadataText(Field(e, key), maximum) ?? (alias is null ? null : MetadataText(Field(e, alias), maximum));
    private static string[] MetadataStrings(JsonElement e)
    {
        if (MetadataText(e, 512) is { } single) return [single];
        return Array(e).Select(value => MetadataText(value, 512)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(256).ToArray();
    }
    private static string[] MetadataStrings(JsonElement e, string key, string alias)
    {
        var values = MetadataStrings(Field(e, key));
        return values.Length > 0 ? values : MetadataStrings(Field(e, alias));
    }
    private static decimal? DecimalNumber(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDecimal(out var number)) return number;
        return MetadataText(e, 32) is { } text && decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out number) ? number : null;
    }
    private static float? Rating(JsonElement e)
    {
        var rating = DecimalNumber(Field(e, "imdbRating"));
        return rating is >= 0 and <= 10 ? (float)rating.Value : null;
    }
    private static long? Runtime(JsonElement e)
    {
        var runtime = Field(e, "runtime");
        var minutes = DecimalNumber(runtime);
        if (minutes is null && MetadataText(runtime, 48) is { } text)
        {
            var duration = text.AsSpan();
            decimal hours = 0;
            var hourSeparator = duration.IndexOfAny('h', 'H');
            if (hourSeparator >= 0)
            {
                if (!decimal.TryParse(duration[..hourSeparator].Trim(), NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out hours) || hours > 168) return null;
                duration = duration[(hourSeparator + 1)..].Trim();
                if (duration.IsEmpty) minutes = hours * 60;
            }
            foreach (var suffix in RuntimeSuffixes)
            {
                if (!duration.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                if (decimal.TryParse(duration[..^suffix.Length].Trim(), NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var parsed) && parsed <= 10080) minutes = hours * 60 + parsed;
                break;
            }
        }
        return minutes is > 0 and <= 10080 ? decimal.ToInt64(decimal.Round(minutes.Value * TimeSpan.TicksPerMinute)) : null;
    }
    private static string? Certification(JsonElement e)
        => MetadataText(Field(e, "app_extras"), "certification", maximum: 64) ?? MetadataText(e, "certification", maximum: 64);
    private static ManagedPerson[] People(JsonElement e)
    {
        var credits = new List<ManagedPerson>();
        var extras = Field(e, "app_extras");
        Add(Field(extras, "cast"), "Actor");
        Add(Field(e, "cast"), "Actor");
        Add(Field(extras, "directors"), "Director");
        Add(Field(e, "director"), "Director");
        Add(Field(extras, "writers"), "Writer");
        Add(Field(e, "writer"), "Writer");
        Add(Field(extras, "producers"), "Producer");
        Add(Field(e, "producer"), "Producer");
        return credits.ToArray();

        void Add(JsonElement field, string type)
        {
            if (field.ValueKind == JsonValueKind.String) AddCredit(field);
            else foreach (var value in Array(field)) AddCredit(value);

            void AddCredit(JsonElement value)
            {
                if (credits.Count >= 256) return;
                var name = MetadataText(value, 512) ?? MetadataText(value, "name", maximum: 512);
                if (name is null) return;
                var role = MetadataText(value, "character", "role", 512);
                var photo = MetadataText(value, "photo", maximum: 8192);
                var existing = credits.FindIndex(person => person.Type == type && person.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (existing < 0) credits.Add(new(name, type, role, photo));
                else credits[existing] = credits[existing] with
                {
                    Role = credits[existing].Role ?? role,
                    PhotoUrl = credits[existing].PhotoUrl ?? photo
                };
            }
        }
    }
    private static string? Status(JsonElement e)
    {
        var explicitStatus = MetadataText(e, "status", maximum: 64)?.ToLowerInvariant();
        if (explicitStatus is not null)
        {
            return explicitStatus switch
            {
                "continuing" or "ongoing" or "returning series" => "Continuing",
                "ended" or "canceled" or "cancelled" => "Ended",
                "unreleased" or "planned" or "in production" => "Unreleased",
                _ => null
            };
        }
        var release = MetadataText(e, "releaseInfo", "release_info", 32);
        if (release is { Length: 5 } && release[4] == '-' && ValidYear(release.AsSpan(0, 4))) return "Continuing";
        if (release is { Length: 9 } && release[4] == '-' && ValidYear(release.AsSpan(0, 4)) && ValidYear(release.AsSpan(5, 4))) return "Ended";
        return null;

        static bool ValidYear(ReadOnlySpan<char> text)
            => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 1800 and <= 2200;
    }
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
                    if (Text(e, "name") is { Length: > 0 } name) extras.Add(new(name, Field(e, "isRequired", "is_required").ValueKind == JsonValueKind.True, Strings(Field(e, "options")) ?? [], Number(Field(e, "optionsLimit", "options_limit")), MetadataText(e, "default", maximum: 512)));
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
        var id = Text(e, "id");
        var type = Text(e, "type");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(type) || MetadataText(e, "name", maximum: 512) is not { } name) return null;
        var videos = new List<StremioVideo>();
        var videoElements = Array(Field(e, "videos")).ToArray();
        foreach (var v in videoElements)
            if (Text(v, "id") is { } videoId && !string.IsNullOrWhiteSpace(videoId)) videos.Add(new(videoId, MetadataText(v, "title", "name", 512) ?? "", Number(Field(v, "season")), Number(Field(v, "episode", "number")), MetadataText(v, "released", "firstAired", 64))
            {
                ProviderIds = ExternalIds(v, ""),
                Description = MetadataText(v, "description", "overview", 16384),
                Thumbnail = MetadataText(v, "thumbnail", maximum: 8192),
                Background = MetadataText(v, "background", maximum: 8192),
                Logo = MetadataText(v, "logo", maximum: 8192),
                CommunityRating = Rating(v),
                RunTimeTicks = Runtime(v),
                OfficialRating = Certification(v),
                Genres = MetadataStrings(v, "genres", "genre"),
                ProductionLocations = MetadataStrings(Field(v, "country")),
                People = People(v)
            });
        var year = Field(e, "year");
        return new()
        {
            Id = id,
            Type = type,
            Name = name,
            ProviderIds = ExternalIds(e, id),
            Description = MetadataText(e, "description", "overview", 16384),
            Poster = MetadataText(e, "poster", maximum: 8192),
            SeasonPosters = Array(Field(Field(e, "app_extras"), "seasonPosters")).Take(10000)
                .Select(poster => MetadataText(poster, 8192)).ToArray(),
            Background = MetadataText(e, "background", maximum: 8192),
            Logo = MetadataText(e, "logo", maximum: 8192),
            CommunityRating = Rating(e),
            RunTimeTicks = Runtime(e),
            OfficialRating = Certification(e),
            Status = Status(e),
            ProductionLocations = MetadataStrings(Field(e, "country")),
            People = People(e),
            Released = MetadataText(e, "released", "firstAired", 64),
            Genres = MetadataStrings(e, "genres", "genre").ToList(),
            ReleaseInfo = MetadataText(e, "releaseInfo", "release_info", 32),
            Year = MetadataText(year, 4) ?? Number(year)?.ToString(CultureInfo.InvariantCulture),
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
