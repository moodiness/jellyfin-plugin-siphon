using Jellyfin.Plugin.Siphon.Identity;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Identity;

public sealed class ContentIdentityTests
{
    [Theory]
    [InlineData("tt1234567", "Imdb", "tt1234567", "movie:tt1234567")]
    [InlineData("imdb:tt1234567", "Imdb", "tt1234567", "movie:tt1234567")]
    [InlineData("tmdb:42", "Tmdb", "42", "movie:tmdb:42")]
    [InlineData("tmdb:movie:42", "Tmdb", "42", "movie:tmdb:42")]
    [InlineData("tvdb:42", "Tvdb", "42", "movie:tvdb:42")]
    [InlineData("mal:42", "MyAnimeList", "42", "movie:mal:42")]
    [InlineData("myanimelist:42", "MyAnimeList", "42", "movie:mal:42")]
    public void KnownProviderNamespacesNeverShareNumericIdentity(string id, string provider, string value, string key)
    {
        var ids = ContentIdentity.ProviderIds(id);
        Assert.Equal(new KeyValuePair<string, string>(provider, value), Assert.Single(ids));
        Assert.Equal(key, ContentIdentity.Key("movie", id, "installation", ids));
    }

    [Fact]
    public void SeriesNamespacesRemainDistinctFromMoviesAndImdbKeysStayStable()
    {
        Assert.Equal("series:tt1234567", ContentIdentity.Key("series", "tt1234567", "a", ContentIdentity.ProviderIds("tt1234567")));
        Assert.Equal("series:tmdb:42", ContentIdentity.Key("series", "tmdb:tv:42", "a", ContentIdentity.ProviderIds("tmdb:tv:42")));
        Assert.StartsWith("movie:opaque:", ContentIdentity.Key("movie", "tmdb:tv:42", "a", ContentIdentity.ProviderIds("tmdb:tv:42")));
        Assert.Equal(new StreamIdentity("movie", "tmdb:tv:42"), Assert.Single(ContentIdentity.MovieAliases("movie", "tmdb:tv:42", ContentIdentity.ProviderIds("tmdb:tv:42"))));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("catalog:opaque/雪?token=x")]
    [InlineData("tmdb:../42")]
    [InlineData("tmdb:-42")]
    [InlineData("tvdb:0")]
    [InlineData("mal:42:1")]
    [InlineData("tt1234567:1:1")]
    public void OpaqueAndMalformedKnownIdsAreScopedWithoutPathInjection(string id)
    {
        var ids = ContentIdentity.ProviderIds(id);
        Assert.Empty(ids);
        var key = ContentIdentity.Key("movie", id, "one", ids);
        Assert.Matches("^movie:opaque:[A-F0-9]{64}$", key);
        Assert.Equal(key, ContentIdentity.Key("movie", id, "one", ids));
        Assert.NotEqual(key, ContentIdentity.Key("movie", id, "two", ids));
        Assert.NotEqual(key, ContentIdentity.Key("movie", id + "x", "one", ids));
        Assert.Equal(new StreamIdentity("movie", id), Assert.Single(ContentIdentity.MovieAliases("movie", id, ids)));
    }

    [Fact]
    public void ExplicitProviderConflictsOverrideInferredIdsWithoutInventingCrossProviderNumbers()
    {
        var ids = ContentIdentity.ProviderIds("tmdb:42", new Dictionary<string, string> { ["Tmdb"] = "99", ["Imdb"] = "tt1234567" });
        Assert.Equal("99", ids["Tmdb"]);
        Assert.Equal("movie:tmdb:99", ContentIdentity.Key("movie", "tmdb:42", "a", ids));
        Assert.Equal("movie:tt1234567", ContentIdentity.Key("movie", "opaque", "a", ids));
        var aliases = ContentIdentity.MovieAliases("movie", "tmdb:42", ids).ToArray();
        Assert.Equal(new StreamIdentity("movie", "tmdb:42"), aliases[0]);
        Assert.Contains(new StreamIdentity("movie", "tmdb:99"), aliases);
        Assert.Contains(new StreamIdentity("movie", "tmdb:movie:99"), aliases);
        Assert.Contains(new StreamIdentity("movie", "tt1234567"), aliases);
        Assert.DoesNotContain(new StreamIdentity("movie", "tmdb:movie:42"), aliases);
        Assert.DoesNotContain(new StreamIdentity("movie", "tvdb:99"), aliases);
    }

    [Fact]
    public void ExplicitProviderAliasesNormalizeOnlyTheirOwnNamespace()
    {
        var ids = ContentIdentity.ProviderIds("opaque", new Dictionary<string, string>
        {
            ["tmdb_id"] = "00042",
            ["moviedb_id"] = "99",
            ["tvdb"] = "tmdb:42",
            ["mal"] = "myanimelist:16",
            ["imdb_id"] = "tt1234567"
        });
        Assert.Equal("42", ids["Tmdb"]);
        Assert.Equal("16", ids["MyAnimeList"]);
        Assert.Equal("tt1234567", ids["Imdb"]);
        Assert.False(ids.ContainsKey("Tvdb"));
    }

    [Fact]
    public void MovieAliasesPreserveResourceTypeAndEpisodeLookupsAreNeverFabricated()
    {
        var ids = ContentIdentity.ProviderIds("opaque", new Dictionary<string, string> { ["MyAnimeList"] = "16", ["Tmdb"] = "42" });
        Assert.Equal(new[]
        {
            new StreamIdentity("anime", "opaque"), new StreamIdentity("anime", "tmdb:42"), new StreamIdentity("anime", "tmdb:movie:42"),
            new StreamIdentity("anime", "mal:16"), new StreamIdentity("anime", "myanimelist:16")
        }, ContentIdentity.MovieAliases("anime", "opaque", ids));
        Assert.Equal(new StreamIdentity("series", "exact:episode/雪"), Assert.Single(ContentIdentity.MovieAliases("series", "exact:episode/雪", ids)));
    }
}
