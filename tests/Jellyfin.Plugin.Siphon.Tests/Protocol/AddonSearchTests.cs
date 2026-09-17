using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Protocol;
using Jellyfin.Plugin.Siphon.Search;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Protocol;

public sealed class AddonSearchTests
{
    [Fact]
    public void LegacySearchExtrasRespectRequiredSavedFiltersWithoutSubscriptionSelection()
    {
        using var document = JsonDocument.Parse("""{"id":"search","catalogs":[{"type":"movie","id":"titles","extraSupported":["search","genre"],"extraRequired":["search","genre"]}]}""");
        var catalog = Assert.Single(StremioJson.Manifest(document.RootElement).Catalogs);
        Assert.Null(AddonSearchPolicy.Extras(catalog, null, "Alien"));
        var extras = AddonSearchPolicy.Extras(catalog, new CatalogSubscription
        {
            Enabled = false,
            Extras = [new ExtraParameter { Name = "genre", Value = "Science Fiction" }, new ExtraParameter { Name = "search", Value = "old saved search" }]
        }, "Alien & 雪");
        Assert.Equal("Alien & 雪", extras!["search"]);
        Assert.Equal("Science Fiction", extras["genre"]);
        Assert.Contains("search=Alien%20%26%20%E9%9B%AA", StremioClient.ResourceUri("https://example.com/manifest.json", "catalog", "movie", "titles", extras).OriginalString);
    }

    [Fact]
    public void RequiredDefaultIsUsedButMissingRequiredInputDoesNotInventAnOption()
    {
        var catalog = new StremioCatalog { Type = "series", Extras = [new("search", false, [], null), new("genre", true, ["Drama", "Comedy"], 1)] };
        Assert.Null(AddonSearchPolicy.Extras(catalog, null, "Title"));
        catalog.Extras[1] = new("genre", true, ["Drama", "Comedy"], 1, "Drama");
        Assert.Equal("Drama", AddonSearchPolicy.Extras(catalog, null, "Title")!["genre"]);
        Assert.Null(AddonSearchPolicy.Extras(new StremioCatalog { Type = "search", Extras = catalog.Extras }, null, "Title"));
    }

    [Fact]
    public void IncludesOverrideExcludesButDoNotOverrideMediaRestriction()
    {
        var movies = new SearchProviderQuery { SearchTerm = "Title", IncludeItemTypes = [BaseItemKind.Movie], ExcludeItemTypes = [BaseItemKind.Movie] };
        Assert.True(AddonSearchPolicy.Allows(movies, "movie"));
        Assert.False(AddonSearchPolicy.Allows(movies, "series"));
        Assert.False(AddonSearchPolicy.Allows(new SearchProviderQuery { SearchTerm = "Title", IncludeItemTypes = [BaseItemKind.Movie], MediaTypes = [MediaType.Audio] }, "movie"));
        Assert.False(AddonSearchPolicy.Allows(new SearchProviderQuery { SearchTerm = "Title", ExcludeItemTypes = [BaseItemKind.Series] }, "series"));
    }

    [Fact]
    public void SeriesDiscoveryWithEmbeddedVideosCreatesOnlyAShellAndPromotesSameContentIdentity()
    {
        var meta = new StremioMeta { Id = "tt1234567", Type = "series", Name = "Found series", Videos = [new("episode-one", "Pilot", 1, 1, null)] };
        var previews = new Dictionary<string, ManagedItem>();
        CatalogSyncService.ConvertMetadata(meta, meta, "addon", ["siphon:preview:series"], new Dictionary<(string, string), string>(), previews,
            new Dictionary<string, ManagedItem>(), 500, allowSeriesPreview: true);
        var preview = Assert.Single(previews).Value;
        Assert.True(preview.IsSearchPreview);
        Assert.Null(preview.Season);
        var expanded = new Dictionary<string, ManagedItem>();
        CatalogSyncService.ConvertMetadata(meta, meta, "addon", ["siphon:manual:series"],
            new Dictionary<(string, string), string> { [("series", meta.Id)] = preview.ContentKey }, expanded, previews, 500);
        var episode = Assert.Single(expanded).Value;
        Assert.Equal(preview.ContentKey, episode.ContentKey);
        Assert.Equal("episode-one", episode.VideoId);
        Assert.False(episode.IsSearchPreview);
        Assert.Equal(1, episode.Season);
    }
}
