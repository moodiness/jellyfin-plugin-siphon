using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Siphon.Metadata;

internal sealed class TmdbRecord
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? Name { get; set; }
    public string? Overview { get; set; }
    public string? ReleaseDate { get; set; }
    public string? FirstAirDate { get; set; }
    public string? AirDate { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public string? StillPath { get; set; }
    public float? VoteAverage { get; set; }
    public int VoteCount { get; set; }
    public int? Runtime { get; set; }
    public int[] EpisodeRunTime { get; set; } = [];
    public string? Status { get; set; }
    public NamedValue[] Genres { get; set; } = [];
    public NamedValue[] ProductionCountries { get; set; } = [];
    public TmdbCredits? Credits { get; set; }
    public TmdbPerson[] GuestStars { get; set; } = [];
    public TmdbPerson[] Crew { get; set; } = [];
    public TmdbIds? ExternalIds { get; set; }
    public TmdbRecord[] Episodes { get; set; } = [];
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
}
internal sealed class NamedValue { public string? Name { get; set; } }
internal sealed class TmdbIds { public string? ImdbId { get; set; } public int? TvdbId { get; set; } }
internal sealed class TmdbCredits { public TmdbPerson[] Cast { get; set; } = []; public TmdbPerson[] Crew { get; set; } = []; }
internal sealed class TmdbPerson
{
    public string? Name { get; set; }
    public string? Character { get; set; }
    public string? Job { get; set; }
    public string? ProfilePath { get; set; }
}
internal sealed class TmdbFind { public TmdbRecord[] MovieResults { get; set; } = []; public TmdbRecord[] TvResults { get; set; } = []; }
internal sealed class TmdbConfiguration { public TmdbImages? Images { get; set; } }
internal sealed class TmdbImages { public string? SecureBaseUrl { get; set; } }
internal sealed class TvdbEnvelope<T> { public string? Status { get; set; } public T? Data { get; set; } }
internal sealed class TvdbLogin { public string? Token { get; set; } }
internal sealed class TvdbRemoteResult { public TvdbRecord? Series { get; set; } public TvdbRecord? Movie { get; set; } }
internal sealed class TvdbRecord
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Overview { get; set; }
    public string? Image { get; set; }
    public string? FirstAired { get; set; }
    public string? Aired { get; set; }
    public string? Year { get; set; }
    public string? OriginalLanguage { get; set; }
    public int? Runtime { get; set; }
    public int? AverageRuntime { get; set; }
    public NamedValue[] Genres { get; set; } = [];
    public NamedValue? Status { get; set; }
    [JsonPropertyName("first_release")] public TvdbRelease? FirstRelease { get; set; }
    public TvdbTranslations? Translations { get; set; }
    public TvdbRemoteId[] RemoteIds { get; set; } = [];
    public int SeasonNumber { get; set; }
    public int Number { get; set; }
}
internal sealed class TvdbRelease { public string? Date { get; set; } }
internal sealed class TvdbRemoteId { public string? Id { get; set; } public string? SourceName { get; set; } }
internal sealed class TvdbTranslation { public string? Language { get; set; } public string? Name { get; set; } public string? Overview { get; set; } }
internal sealed class TvdbTranslations { public TvdbTranslation[] NameTranslations { get; set; } = []; public TvdbTranslation[] OverviewTranslations { get; set; } = []; }
internal sealed class FanartRecord
{
    [JsonPropertyName("tmdb_id")] public string? TmdbId { get; set; }
    [JsonPropertyName("imdb_id")] public string? ImdbId { get; set; }
    [JsonPropertyName("thetvdb_id")] public string? TvdbId { get; set; }
    public FanartImage[] Movieposter { get; set; } = [];
    public FanartImage[] Moviebackground { get; set; } = [];
    public FanartImage[] Hdmovielogo { get; set; } = [];
    public FanartImage[] Movielogo { get; set; } = [];
    public FanartImage[] Tvposter { get; set; } = [];
    public FanartImage[] Showbackground { get; set; } = [];
    public FanartImage[] Hdtvlogo { get; set; } = [];
    public FanartImage[] Clearlogo { get; set; } = [];
    public FanartImage[] Seasonposter { get; set; } = [];
}
internal sealed class FanartImage
{
    public string? Url { get; set; }
    public string? Lang { get; set; }
    public string? Likes { get; set; }
    public string? Season { get; set; }
}
internal sealed class MdbRecord
{
    public MdbIdentifiers? Ids { get; set; }
    public string? Type { get; set; }
    public string? Title { get; set; }
    public float? Score { get; set; }
}
internal sealed class MdbIdentifiers
{
    public string? Imdb { get; set; }
    public int? Tmdb { get; set; }
    public int? Tvdb { get; set; }
}
internal sealed class MdbUser { public string? Username { get; set; } [JsonPropertyName("user_id")] public int? UserId { get; set; } }
