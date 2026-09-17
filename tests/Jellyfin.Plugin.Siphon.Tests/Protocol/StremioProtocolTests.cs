using System.Text.Json;
using Jellyfin.Plugin.Siphon.Protocol;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Protocol;

public sealed class StremioProtocolTests
{
    [Fact]
    public void ResourceObjectsOverrideManifestRestrictionsAndCatalogsUseTheirOwnIdentity()
    {
        using var json = JsonDocument.Parse("""{"id":"a","types":["movie"],"idPrefixes":["tt"],"resources":["meta",{"name":"stream","types":["series"],"idPrefixes":["custom:"]},null,19],"catalogs":[{"type":"mixed","id":"trending"}]}""");
        var manifest = StremioJson.Manifest(json.RootElement);
        Assert.True(AddonRegistry.Supports(manifest, "meta", "movie", "tt123"));
        Assert.False(AddonRegistry.Supports(manifest, "meta", "series", "tt123"));
        Assert.True(AddonRegistry.Supports(manifest, "stream", "series", "custom:123"));
        Assert.False(AddonRegistry.Supports(manifest, "stream", "series", "tt123"));
        Assert.True(AddonRegistry.Supports(manifest, "catalog", "mixed", "trending"));
        Assert.False(AddonRegistry.Supports(manifest, "catalog", "movie", "trending"));
    }
    [Fact]
    public void CanonicalExtrasOverrideLegacyAndRequiredLegacyFieldsAreRetained()
    {
        using var json = JsonDocument.Parse("""{"id":"a","catalogs":[{"id":"one","type":"mixed","extra":[],"extraRequired":["search"]},{"id":"two","type":"movie","extraSupported":["genre"],"extraRequired":["search"]},{"id":"three","type":"series","extra":[{"name":"genre","isRequired":false,"is_required":true,"optionsLimit":2,"options_limit":9}]}]}""");
        var catalogs = StremioJson.Manifest(json.RootElement).Catalogs;
        Assert.Empty(catalogs[0].GetExtras());
        Assert.True(catalogs[1].GetExtras().Single(e => e.Name == "search").IsRequired);
        Assert.False(catalogs[2].GetExtras()[0].IsRequired);
        Assert.Equal(2, catalogs[2].GetExtras()[0].OptionsLimit);
    }
    [Fact]
    public void ResourceUrlsPreserveOpaqueInstallationAndEncodeEachSegment()
    {
        var url = StremioClient.ResourceUri("https://example.org/a%2Fb/manifest.json?token=x%2By", "catalog", "mixed", "a/b", new Dictionary<string, string> { ["search"] = "a&b /雪" });
        Assert.Equal("https://example.org/a%2Fb/catalog/mixed/a%2Fb/search=a%26b%20%2F%E9%9B%AA.json?token=x%2By", url.OriginalString);
        Assert.Throws<StremioException>(() => StremioClient.ManifestUri("https://example.org/manifest.json/"));
        Assert.Throws<StremioException>(() => StremioClient.ManifestUri("https://example.org/%6Danifest.json"));
    }
    [Fact]
    public void MixedCatalogRetainsPerMetaTypesAndCanonicalAliases()
    {
        using var json = JsonDocument.Parse("""{"metas":[null,{"id":"tt1234567","type":"movie","name":"Movie","imdb_id":"tt1234567","imdbId":"tt7654321","releaseInfo":"2020","release_info":"1999"},{"id":"tmdb:tv:42","type":"series","name":"Series","tvdb_id":99,"videos":[{"id":"opaque:pilot/雪","title":"Pilot","season":1,"episode":1}]},{"id":"broken","name":"Missing type"}]}""");
        var page = StremioJson.Catalog(json.RootElement);
        Assert.Equal(4, page.RawCount);
        Assert.False(page.IsComplete);
        var metas = page.Items;
        Assert.Equal(new[] { "movie", "series" }, metas.Select(m => m.Type));
        Assert.Equal("tt1234567", metas[0].ProviderIds["Imdb"]);
        Assert.Equal("2020", metas[0].ReleaseInfo);
        Assert.Equal("Pilot", metas[1].Videos[0].Name);
        Assert.Equal("42", metas[1].ProviderIds["Tmdb"]);
        Assert.Equal("99", metas[1].ProviderIds["Tvdb"]);
        Assert.Equal("opaque:pilot/雪", metas[1].Videos[0].Id);
        Assert.Empty(metas[1].Videos[0].ProviderIds);
    }
    [Fact]
    public void UnknownSourcesAreSkippedAndHeaderConditionsRemainVisible()
    {
        using var json = JsonDocument.Parse("""{"streams":[{"infoHash":"abc"},{"externalUrl":"https://example.org"},{"url":"https://example.org/v","title":"Legacy description","description":"Canonical","behaviorHints":{"proxyHeaders":{"request":{"Authorization":"Bearer secret"},"response":{"X-Test":"yes"}}}}]}""");
        var stream = Assert.Single(StremioJson.Streams(json.RootElement));
        Assert.Equal("Canonical", stream.Description);
        Assert.True(stream.UnsupportedHeaders);
        Assert.Equal("Bearer secret", stream.RequestHeaders["Authorization"]);
    }
    [Fact]
    public void StremioInstallationUrlsBecomeHttpsWithoutChangingOpaqueTokens()
    {
        const string installation = "stremio://example.org/a%2Fb/manifest.json?token=x%2By";
        Assert.Equal("https://example.org/a%2Fb/manifest.json?token=x%2By", StremioClient.ManifestUri(installation).OriginalString);
        Assert.Equal("https://example.org/a%2Fb/stream/anime/mal%3A42%3Apilot%2F%E9%9B%AA.json?token=x%2By", StremioClient.ResourceUri(installation, "stream", "anime", "mal:42:pilot/雪").OriginalString);
        Assert.Throws<StremioException>(() => StremioClient.ManifestUri("stremio://user:pass@example.org/manifest.json"));
        Assert.Throws<StremioException>(() => StremioClient.ManifestUri("stremio://example.org/manifest.json#token"));
    }
    [Fact]
    public void ExplicitCanonicalProviderIdsWinConflictsAndRetainLargeNumericIds()
    {
        using var json = JsonDocument.Parse("""{"id":"tmdb:movie:42","type":"movie","name":"Movie","tmdb_id":7,"moviedb_id":8,"imdb_id":"tt1234567","ids":{"tmdb":9007199254740993,"tvdb":"00099","mal":16},"description":"Plot","poster":"https://example.org/poster.jpg","released":"2024-01-02","genres":["Drama",null,7]}""");
        var meta = Assert.IsType<StremioMeta>(StremioJson.Meta(json.RootElement));
        Assert.Equal("tmdb:movie:42", meta.Id);
        Assert.Equal("9007199254740993", meta.ProviderIds["Tmdb"]);
        Assert.Equal("99", meta.ProviderIds["Tvdb"]);
        Assert.Equal("16", meta.ProviderIds["MyAnimeList"]);
        Assert.Equal("tt1234567", meta.ProviderIds["Imdb"]);
        Assert.Equal("Plot", meta.Description);
        Assert.Equal("https://example.org/poster.jpg", meta.Poster);
        Assert.Equal("2024-01-02", meta.Released);
        Assert.Equal(new[] { "Drama" }, meta.Genres);
    }
    [Fact]
    public void CanonicalNullIdsDoNotResurrectAliasValues()
    {
        using var json = JsonDocument.Parse("""{"metas":[{"id":"opaque","type":"movie","name":"Movie","imdb_id":null,"imdbId":"tt1234567","tmdb_id":null,"moviedb_id":42,"ids":{"tvdb":null,"mal":null},"tvdb_id":99,"mal_id":16},{"id":"another","type":"movie","name":"Other","ids":null,"imdb_id":"tt1234567"},{"id":"legacy","type":"movie","name":"Legacy","moviedb_id":42,"mal_id":"16"}]}""");
        var metas = StremioJson.Catalog(json.RootElement).Items;
        Assert.Empty(metas[0].ProviderIds);
        Assert.Empty(metas[1].ProviderIds);
        Assert.Equal("42", metas[2].ProviderIds["Tmdb"]);
        Assert.Equal("16", metas[2].ProviderIds["MyAnimeList"]);
    }
    [Fact]
    public void EpisodeProviderIdsComeOnlyFromExplicitEpisodeMetadata()
    {
        using var json = JsonDocument.Parse("""{"id":"tt1234567","type":"series","name":"Show","videos":[{"id":"tt1234567","season":1,"episode":1},{"id":"exact:episode/id","season":1,"episode":2,"ids":{"tvdb":567,"imdb":"tt7654321"},"description":"Episode plot","thumbnail":"https://example.org/episode.jpg"}]}""");
        var meta = Assert.IsType<StremioMeta>(StremioJson.Meta(json.RootElement));
        Assert.Empty(meta.Videos[0].ProviderIds);
        Assert.Equal("exact:episode/id", meta.Videos[1].Id);
        Assert.Equal("567", meta.Videos[1].ProviderIds["Tvdb"]);
        Assert.Equal("tt7654321", meta.Videos[1].ProviderIds["Imdb"]);
        Assert.Equal("Episode plot", meta.Videos[1].Description);
        Assert.Equal("https://example.org/episode.jpg", meta.Videos[1].Thumbnail);
    }
    [Fact]
    public void RuntimeUnitsAndRatingsRemainSpecificToEachEpisode()
    {
        using var json = JsonDocument.Parse("""
            {"id":"show","type":"series","name":"Show","runtime":44,"imdbRating":"8.1",
             "videos":[{"id":"episode:1","season":1,"episode":1,"runtime":"47.5 min","imdbRating":9.2},
                       {"id":"episode:2","season":1,"episode":2,"runtime":"NaN","imdbRating":11}]}
            """);
        var meta = Assert.IsType<StremioMeta>(StremioJson.Meta(json.RootElement));
        Assert.Equal(TimeSpan.FromMinutes(44).Ticks, meta.RunTimeTicks);
        Assert.Equal(TimeSpan.FromMinutes(47.5).Ticks, meta.Videos[0].RunTimeTicks);
        Assert.Equal(9.2f, meta.Videos[0].CommunityRating);
        Assert.Null(meta.Videos[1].RunTimeTicks);
        Assert.Null(meta.Videos[1].CommunityRating);
    }

    [Fact]
    public void SparseCreditsMergeWithoutLosingRolesOrDifferentCreditTypes()
    {
        using var json = JsonDocument.Parse("""
            {"id":"movie","type":"movie","name":"Movie","cast":["A. Actor","B. Actor"],
             "director":"A. Actor","app_extras":{"cast":[{"name":"a. actor","character":"The narrator"}]}}
            """);
        var meta = Assert.IsType<StremioMeta>(StremioJson.Meta(json.RootElement));
        var actors = meta.People.Where(person => person.Type == "Actor").ToArray();
        Assert.Equal(2, actors.Length);
        Assert.Equal("The narrator", Assert.Single(actors, person => person.Name.Equals("A. Actor", StringComparison.OrdinalIgnoreCase)).Role);
        Assert.Equal("A. Actor", Assert.Single(meta.People, person => person.Type == "Director").Name);
    }

    [Fact]
    public void MissingSeasonArtworkDoesNotShiftLaterSeasons()
    {
        using var json = JsonDocument.Parse("""
            {"id":"show","type":"series","name":"Show","app_extras":{"seasonPosters":[
              "https://example.org/specials.jpg",null,"https://example.org/season2.jpg",7,"https://example.org/season4.jpg"]}}
            """);
        var meta = Assert.IsType<StremioMeta>(StremioJson.Meta(json.RootElement));
        Assert.Null(meta.SeasonPosters[1]);
        Assert.Equal("https://example.org/season2.jpg", meta.SeasonPosters[2]);
        Assert.Null(meta.SeasonPosters[3]);
        Assert.Equal("https://example.org/season4.jpg", meta.SeasonPosters[4]);
    }

    [Fact]
    public void BlankIdentitiesMakeCatalogAndEpisodeSnapshotsIncomplete()
    {
        using var json = JsonDocument.Parse("""
            {"metas":[{"id":" ","type":"movie","name":"Invalid movie"},
              {"id":"opaque","type":" ","name":"Invalid type"},
              {"id":"show","type":"series","name":"Show","videos":[
                {"id":" ","season":1,"episode":1},
                {"id":"episode:2","season":1,"episode":2}]}]}
            """);
        var page = StremioJson.Catalog(json.RootElement);
        Assert.False(page.IsComplete);
        var show = Assert.Single(page.Items);
        Assert.Equal("show", show.Id);
        Assert.False(show.VideosComplete);
        Assert.Equal("episode:2", Assert.Single(show.Videos).Id);
    }

    [Theory]
    [InlineData("2h32min", 152d)]
    [InlineData("2h", 120d)]
    [InlineData("1H 0.5 MIN", 60.5)]
    [InlineData("168h", 10080d)]
    [InlineData("168h1min", null)]
    [InlineData("9999999999999999999999999999h", null)]
    [InlineData("2h9999999999999999999999999999min", null)]
    public void HourDurationsAreConvertedWithoutOverflow(string runtime, double? minutes)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { id = "movie", type = "movie", name = "Movie", runtime }));
        var meta = Assert.IsType<StremioMeta>(StremioJson.Meta(json.RootElement));
        Assert.Equal(minutes is { } value ? TimeSpan.FromMinutes(value).Ticks : (long?)null, meta.RunTimeTicks);
    }
}
