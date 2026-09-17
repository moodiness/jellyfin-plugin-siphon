using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Metadata;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Identity;

public sealed class MetadataProviderTests
{
    [Fact]
    public async Task ExplicitTestUsesSavedCredentialsWhenDisabledAndNeverExposesUpstreamFailure()
    {
        var config = new PluginConfiguration { TmdbReadAccessToken = "synthetic-test-token" };
        var origin = new Origin((uri, headers) =>
        {
            Assert.Equal("api.themoviedb.org", uri.Host);
            Assert.Equal("Bearer synthetic-test-token", headers!["Authorization"]);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("private upstream failure body") };
        });
        using var client = new MetadataProviderClient(new(() => config), origin, null!);
        var result = await client.TestAsync("Tmdb", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("AuthenticationFailed", result.Code);
        Assert.Equal(1, origin.Calls);
        var serialized = JsonSerializer.Serialize(client.GetStatus());
        Assert.DoesNotContain("synthetic-test-token", serialized);
        Assert.DoesNotContain("private upstream failure body", serialized);
    }

    [Fact]
    public async Task MissingCredentialsAndRetryAfterNeverSpendAdditionalRequests()
    {
        var config = new PluginConfiguration();
        var origin = new Origin((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return response;
        });
        using var client = new MetadataProviderClient(new(() => config), origin, null!);
        Assert.Equal("MissingCredentials", (await client.TestAsync("MdbList", CancellationToken.None)).Code);
        Assert.Equal(0, origin.Calls);
        config.MdbListApiKey = "synthetic-key";
        Assert.Equal("RateLimited", (await client.TestAsync("MdbList", CancellationToken.None)).Code);
        Assert.Equal("RateLimited", (await client.TestAsync("MdbList", CancellationToken.None)).Code);
        Assert.Equal(1, origin.Calls);
    }

    [Fact]
    public async Task SuccessfulHttpResponseWithoutDocumentedPayloadIsNotAuthenticationSuccess()
    {
        var config = new PluginConfiguration { TmdbReadAccessToken = "synthetic-token" };
        using var client = new MetadataProviderClient(new(() => config), new Origin((_, _) => new(HttpStatusCode.OK) { Content = new StringContent("{}") }), null!);
        var result = await client.TestAsync("Tmdb", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("InvalidResponse", result.Code);
    }

    [Fact]
    public async Task DisabledProvidersStillPreserveLockedProxyImagesAndIndexedPeople()
    {
        var old = MetadataPolicyTests.EpisodeItem() with { Type = "movie", PosterUrl = "https://images.example/old.jpg", People = [new("Original actor", "Actor", PhotoUrl: "https://images.example/person.jpg")] };
        var fresh = old with { PosterUrl = "https://images.example/new.jpg", People = [new("Replacement actor", "Actor")], Description = "Fresh addon plot" };
        var library = DispatchProxy.Create<ILibraryManager, NativeLibraryProxy>();
        ((NativeLibraryProxy)(object)library).Item = new Movie { IsLocked = true, DateLastSaved = DateTime.UtcNow };
        var service = new MetadataEnrichmentService(new(() => new PluginConfiguration()), null!, library, null!, new PreviousState(old));
        var result = Assert.Single(await service.EnrichAsync([fresh], CancellationToken.None));
        Assert.Equal(old.PosterUrl, result.PosterUrl);
        Assert.Same(old.People, result.People);
        Assert.Equal(fresh.Description, result.Description);
        Assert.Same(fresh.StreamIdentities, result.StreamIdentities);
    }

    [Fact]
    public async Task CastOnlyLockDoesNotFreezeUnrelatedArtwork()
    {
        var old = MetadataPolicyTests.EpisodeItem() with { Type = "movie", PosterUrl = "https://images.example/old.jpg", People = [new("Original actor", "Actor")] };
        var fresh = old with { PosterUrl = "https://images.example/new.jpg", People = [new("Replacement actor", "Actor")] };
        var library = DispatchProxy.Create<ILibraryManager, NativeLibraryProxy>();
        ((NativeLibraryProxy)(object)library).Item = new Movie { LockedFields = [MetadataField.Cast], DateLastSaved = DateTime.UtcNow };
        var service = new MetadataEnrichmentService(new(() => new PluginConfiguration()), null!, library, null!, new PreviousState(old));
        var result = Assert.Single(await service.EnrichAsync([fresh], CancellationToken.None));
        Assert.Equal(fresh.PosterUrl, result.PosterUrl);
        Assert.Same(old.People, result.People);
    }

    [Fact]
    public async Task DocumentedProviderRecordsShareParentMetadataAndFetchOnlyMissingEpisodeSeason()
    {
        var config = new PluginConfiguration
        {
            EnableTmdbMetadata = true,
            EnableFanartMetadata = true,
            EnableMdbListMetadata = true,
            TmdbReadAccessToken = "synthetic-token",
            FanartApiKey = "synthetic-key",
            MdbListApiKey = "synthetic-key",
            MetadataLanguage = "fr"
        };
        var paths = new List<string>();
        var origin = new Origin((uri, _) =>
        {
            paths.Add(uri.AbsolutePath);
            var json = uri.AbsolutePath switch
            {
                "/3/tv/123" => """{"id":123,"name":"Provider series","overview":"Series synopsis","poster_path":"/series.jpg","vote_count":2,"vote_average":6,"external_ids":{"imdb_id":"tt0137523","tvdb_id":456}}""",
                "/3/configuration" => """{"images":{"secure_base_url":"https://image.tmdb.org/t/p/"}}""",
                "/v3.2/tv/456" => """{"thetvdb_id":"456","tvposter":[{"url":"https://images.example/french.jpg","lang":"fr","likes":"1"},{"url":"https://images.example/english.jpg","lang":"en","likes":"100"}]}""",
                "/imdb/show/tt0137523" => """{"title":"Series","type":"show","ids":{"imdb":"tt0137523","tmdb":123,"tvdb":456},"score":82}""",
                "/3/tv/123/season/1" => """{"id":1001,"season_number":1,"episodes":[{"id":1002,"season_number":1,"episode_number":2,"name":"Episode name","overview":"Episode synopsis","air_date":"2020-01-02","still_path":"/episode.jpg","runtime":45,"vote_count":2,"vote_average":9}]}""",
                _ => throw new InvalidOperationException("Unexpected provider resource.")
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        });
        var first = MetadataPolicyTests.EpisodeItem() with { ProviderIds = new(StringComparer.OrdinalIgnoreCase) { ["Tmdb"] = "123" } };
        var complete = first with
        {
            Key = "complete",
            Season = 2,
            Description = "Existing episode plot",
            Released = "2021-01-01",
            ThumbnailUrl = "https://images.example/existing.jpg",
            EpisodeRunTimeTicks = TimeSpan.TicksPerMinute * 40,
            EpisodeCommunityRating = 7,
            EpisodePeople = [new("Existing actor", "Actor")]
        };
        using var client = new MetadataProviderClient(new(() => config), origin, null!);
        var library = DispatchProxy.Create<ILibraryManager, NativeLibraryProxy>();
        var service = new MetadataEnrichmentService(new(() => config), client, library, null!, new PreviousState(first));
        var enriched = await service.EnrichAsync([first, complete], CancellationToken.None);
        Assert.Equal("Series synopsis", enriched[0].SeriesDescription);
        Assert.Equal(enriched[0].SeriesDescription, enriched[1].SeriesDescription);
        Assert.Equal(8.2f, enriched[0].CommunityRating);
        Assert.Equal("https://images.example/french.jpg", enriched[0].PosterUrl);
        Assert.Equal("Episode synopsis", enriched[0].Description);
        Assert.Equal(9f, enriched[0].EpisodeCommunityRating);
        Assert.Equal("Existing episode plot", enriched[1].Description);
        Assert.Equal(7f, enriched[1].EpisodeCommunityRating);
        Assert.Equal("456", enriched[1].ProviderIds["Tvdb"]);
        Assert.Equal("1002", enriched[0].EpisodeProviderIds["Tmdb"]);
        Assert.DoesNotContain("/3/tv/123/season/2", paths);
        Assert.Equal(1, paths.Count(path => path == "/3/tv/123"));
        Assert.Same(first.StreamIdentities, enriched[0].StreamIdentities);
    }

    [Fact]
    public async Task MismatchedProviderIdentityCannotOverwriteAnExistingRating()
    {
        var config = new PluginConfiguration
        {
            EnableMdbListMetadata = true,
            MdbListApiKey = "synthetic-key",
            MetadataLanguage = "fr",
            MetadataUpdateMode = "RefreshSelected",
            MetadataRefreshFields = ["Ratings"]
        };
        var item = MetadataPolicyTests.EpisodeItem() with
        {
            Type = "movie",
            CommunityRating = 7,
            ProviderIds = new() { ["Imdb"] = "tt0137523" }
        };
        var origin = new Origin((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"title":"Unrelated movie","type":"movie","ids":{"imdb":"tt9999999"},"score":91}""")
        });
        using var client = new MetadataProviderClient(new(() => config), origin, null!);
        var library = DispatchProxy.Create<ILibraryManager, NativeLibraryProxy>();
        var service = new MetadataEnrichmentService(new(() => config), client, library, null!, new PreviousState(item));
        var enriched = await service.EnrichAsync([item], CancellationToken.None);
        Assert.Equal(7, enriched[0].CommunityRating);
        Assert.Equal("InvalidResponse", client.GetStatus().Single(status => status.Provider == "MdbList").LastResult?.Code);
    }

    private sealed class Origin(Func<Uri, IReadOnlyDictionary<string, string>?, HttpResponseMessage> respond) : ISafeHttpClient
    {
        public int Calls { get; private set; }
        public Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(uri, headers));
        }
    }

    public class NativeLibraryProxy : DispatchProxy
    {
        public BaseItem? Item { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "GetNewItemId" => Guid.Empty,
            "GetItemById" => Item,
            "GetVirtualFolders" => new List<VirtualFolderInfo>(),
            _ => throw new InvalidOperationException("Unexpected native lookup.")
        };
    }

    private sealed class PreviousState(ManagedItem item) : ISiphonStateStore
    {
        public ManagedItem? FindByKey(string key) => key == item.Key ? item : null;
        public IReadOnlyList<ManagedItem> GetItems() => throw new InvalidOperationException("Full-state cloning is not needed for locks.");
        public ManagedItem? FindByPath(string path) => throw new NotSupportedException();
        public ManagedItem? FindByContentKey(string key) => throw new NotSupportedException();
        public Task SaveAsync(IReadOnlyList<ManagedItem> items, CancellationToken ct) => throw new NotSupportedException();
    }
}
