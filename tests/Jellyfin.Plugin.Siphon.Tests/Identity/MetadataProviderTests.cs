using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Metadata;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
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
        Assert.Equal(old.People, result.People);
        Assert.Equal(fresh.Description, result.Description);
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
        Assert.Equal(old.People, result.People);
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
                "/3/tv/123" => """{"id":123,"name":"Provider series","overview":"Series synopsis","poster_path":"/series.jpg","seasons":[{"id":2001,"season_number":2,"poster_path":"/season-two.jpg"}],"vote_count":2,"vote_average":6,"external_ids":{"imdb_id":"tt0137523","tvdb_id":456}}""",
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
        Assert.Equal("https://image.tmdb.org/t/p/original/season-two.jpg", enriched[1].SeasonPosterUrl);
        Assert.DoesNotContain("/3/tv/123/season/2", paths);
        Assert.Equal(1, paths.Count(path => path == "/3/tv/123"));
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

    [Fact]
    public async Task ExistingSeriesRatingAvoidsMdbRequestsEvenWhenEpisodeRatingsAreMissing()
    {
        var config = new PluginConfiguration { EnableMdbListMetadata = true, MdbListApiKey = "synthetic-key", MetadataLanguage = "en" };
        var first = MetadataPolicyTests.EpisodeItem() with { ProviderIds = new() { ["Imdb"] = "tt0137523" } };
        var rated = first with { Key = "rated-episode", Episode = 3, CommunityRating = 7 };
        var origin = new Origin((_, _) => new(HttpStatusCode.TooManyRequests));
        using var client = new MetadataProviderClient(new(() => config), origin, null!);
        var library = DispatchProxy.Create<ILibraryManager, NativeLibraryProxy>();
        var service = new MetadataEnrichmentService(new(() => config), client, library, null!, new PreviousState(first));
        var enriched = await service.EnrichAsync([first, rated], CancellationToken.None, forceRefresh: true);
        Assert.All(enriched, item => Assert.Equal(7f, item.CommunityRating));
        Assert.All(enriched, item => Assert.Null(item.EpisodeCommunityRating));
        Assert.Equal(0, origin.Calls);
    }

    [Theory]
    [InlineData("unselected")]
    [InlineData("locked")]
    [InlineData("native-rating")]
    public async Task MdbDoesNotSpendQuotaWhenRatingPolicyCannotUseItsResponse(string policy)
    {
        var config = new PluginConfiguration { EnableMdbListMetadata = true, MdbListApiKey = "synthetic-key", MetadataLanguage = "en" };
        if (policy == "unselected")
        {
            config.MetadataUpdateMode = "RefreshSelected";
            config.MetadataRefreshFields = ["Overview"];
        }
        var item = MetadataPolicyTests.EpisodeItem() with { Type = "movie", ProviderIds = new() { ["Imdb"] = "tt0137523" } };
        var origin = new Origin((_, _) => new(HttpStatusCode.TooManyRequests));
        using var client = new MetadataProviderClient(new(() => config), origin, null!);
        var library = DispatchProxy.Create<ILibraryManager, NativeLibraryProxy>();
        if (policy != "unselected")
            ((NativeLibraryProxy)(object)library).Item = new Movie
            {
                DateLastSaved = DateTime.UtcNow,
                CommunityRating = policy == "native-rating" ? 7 : null,
                LockedFields = policy == "locked" ? [MetadataField.OfficialRating] : []
            };
        var service = new MetadataEnrichmentService(new(() => config), client, library, null!, new PreviousState(item));
        var enriched = await service.EnrichAsync([item], CancellationToken.None, forceRefresh: true);
        Assert.Null(enriched[0].CommunityRating);
        Assert.Equal(0, origin.Calls);
    }

    [Fact]
    public async Task ParentSeasonPostersFillEachSeasonWithoutFetchingCompleteEpisodes()
    {
        var config = TmdbConfig();
        var first = CompleteEpisode() with { SeasonPosterUrl = null };
        var second = first with { Key = "second-season", Season = 2 };
        var paths = new List<string>();
        var origin = TmdbOrigin(paths, """{"id":123,"name":"Series","backdrop_path":"/series-background.jpg","seasons":[{"id":101,"season_number":1,"poster_path":"/season-one.jpg"},{"id":102,"season_number":2,"poster_path":"/season-two.jpg"},{"id":199,"season_number":99,"poster_path":"/unrelated.jpg"}]}""");
        using var client = new MetadataProviderClient(new(() => config), origin, null!);
        var library = DispatchProxy.Create<ILibraryManager, NativeLibraryProxy>();
        var service = new MetadataEnrichmentService(new(() => config), client, library, null!, new PreviousState(first, second));

        var result = await service.EnrichAsync([first, second], CancellationToken.None);

        Assert.Equal("https://image.tmdb.org/t/p/original/season-one.jpg", result[0].SeasonPosterUrl);
        Assert.Equal("https://image.tmdb.org/t/p/original/season-two.jpg", result[1].SeasonPosterUrl);
        Assert.All(result, item => Assert.Equal("https://image.tmdb.org/t/p/original/series-background.jpg", item.BackdropUrl));
        Assert.All(result, item => Assert.Null(item.EpisodeBackdropUrl));
        Assert.All(result, item => Assert.Equal(first.ThumbnailUrl, item.ThumbnailUrl));
        Assert.DoesNotContain(paths, path => path.Contains("/season/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SeasonResponsePosterIsUsedEvenWhenEpisodeMetadataIsComplete()
    {
        var config = TmdbConfig();
        var item = CompleteEpisode();
        var paths = new List<string>();
        var origin = TmdbOrigin(paths, """{"id":123,"name":"Series","seasons":[{"id":101,"season_number":1,"poster_path":null}]}""",
            """{"id":101,"season_number":1,"poster_path":"/season-one.jpg","episodes":[{"id":1002,"show_id":123,"season_number":1,"episode_number":2,"name":"Unneeded title","overview":"Unneeded plot","still_path":"/unneeded.jpg"}]}""");
        using var client = new MetadataProviderClient(new(() => config), origin, null!);
        var library = DispatchProxy.Create<ILibraryManager, NativeLibraryProxy>();
        var service = new MetadataEnrichmentService(new(() => config), client, library, null!, new PreviousState(item));

        var result = Assert.Single(await service.EnrichAsync([item], CancellationToken.None));

        Assert.Equal("https://image.tmdb.org/t/p/original/season-one.jpg", result.SeasonPosterUrl);
        Assert.Equal(item.Name, result.Name);
        Assert.Equal(item.Description, result.Description);
        Assert.Equal(item.ThumbnailUrl, result.ThumbnailUrl);
        Assert.Empty(result.EpisodeProviderIds);
        Assert.Equal(1, paths.Count(path => path == "/3/tv/123/season/1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FillMissingUpgradesManagedFallbacksToSeasonPosterAndDistinctEpisodeStills(bool polluted)
    {
        var config = TmdbConfig();
        var first = CompleteEpisode() with { Key = "first-episode", Episode = 1, PosterUrl = "https://images.example/series.jpg" };
        first = first with { SeasonPosterUrl = polluted ? first.PosterUrl : null, ThumbnailUrl = polluted ? first.PosterUrl : null };
        var second = first with { Key = "second-episode", Episode = 2 };
        var paths = new List<string>();
        var origin = TmdbOrigin(paths, """{"id":123,"name":"Series","seasons":[{"id":101,"season_number":1,"poster_path":"/season-one.jpg"}]}""",
            """{"id":101,"season_number":1,"episodes":[{"id":1002,"show_id":123,"season_number":1,"episode_number":2,"still_path":"/episode-two.jpg"},{"id":1001,"show_id":123,"season_number":1,"episode_number":1,"still_path":"/episode-one.jpg"}]}""");
        using var client = new MetadataProviderClient(new(() => config), origin, null!);
        var library = DispatchProxy.Create<ILibraryManager, NativeLibraryProxy>();
        AddNativeArtwork(library, first, "managed");
        AddNativeArtwork(library, second, "managed");
        var service = new MetadataEnrichmentService(new(() => config), client, library, null!, new PreviousState(first, second));

        var result = await service.EnrichAsync([first, second], CancellationToken.None);

        Assert.All(result, item => Assert.Equal("https://image.tmdb.org/t/p/original/season-one.jpg", item.SeasonPosterUrl));
        Assert.Equal("https://image.tmdb.org/t/p/original/episode-one.jpg", result[0].ThumbnailUrl);
        Assert.Equal("https://image.tmdb.org/t/p/original/episode-two.jpg", result[1].ThumbnailUrl);
        Assert.Equal("1001", result[0].EpisodeProviderIds["Tmdb"]);
        Assert.Equal("1002", result[1].EpisodeProviderIds["Tmdb"]);
        Assert.All(result, item => Assert.Equal(first.PosterUrl, item.PosterUrl));
        Assert.Equal(1, paths.Count(path => path == "/3/tv/123/season/1"));
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("locked")]
    [InlineData("specific")]
    [InlineData("unselected")]
    public async Task ProtectedChildArtworkDoesNotSpendSeasonRequests(string protection)
    {
        var config = TmdbConfig();
        if (protection == "unselected")
        {
            config.MetadataUpdateMode = "RefreshSelected";
            config.MetadataRefreshFields = ["Genres"];
        }
        var item = CompleteEpisode() with { PosterUrl = "https://images.example/series.jpg", ThumbnailUrl = null };
        if (protection == "specific")
            item = item with { SeasonPosterUrl = "https://images.example/old-season.jpg", ThumbnailUrl = "https://images.example/old-still.jpg" };
        var paths = new List<string>();
        var origin = TmdbOrigin(paths, """{"id":123,"name":"Series","seasons":[{"id":101,"season_number":1,"poster_path":"/season-one.jpg"}]}""");
        using var client = new MetadataProviderClient(new(() => config), origin, null!);
        var library = DispatchProxy.Create<ILibraryManager, NativeLibraryProxy>();
        AddNativeArtwork(library, item, protection);
        var service = new MetadataEnrichmentService(new(() => config), client, library, null!, new PreviousState(item));

        var result = Assert.Single(await service.EnrichAsync([item], CancellationToken.None));

        Assert.Equal(item.SeasonPosterUrl, result.SeasonPosterUrl);
        Assert.Equal(item.ThumbnailUrl, result.ThumbnailUrl);
        Assert.DoesNotContain(paths, path => path.Contains("/season/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("manual")]
    [InlineData("locked")]
    [InlineData("specific")]
    public async Task IncomingArtworkRepairsOwnedFallbacksWithoutFreezingParentGapsOrReplacingProtectedChildren(string protection)
    {
        var old = CompleteEpisode() with { PosterUrl = "https://images.example/series.jpg", ThumbnailUrl = null };
        if (protection == "specific")
            old = old with { SeasonPosterUrl = "https://images.example/old-season.jpg", ThumbnailUrl = "https://images.example/old-still.jpg" };
        var fresh = old with
        {
            SeasonPosterUrl = "https://images.example/new-season.jpg",
            ThumbnailUrl = "https://images.example/new-still.jpg",
            BackdropUrl = "https://images.example/background.jpg",
            LogoUrl = "https://images.example/logo.png"
        };
        var library = DispatchProxy.Create<ILibraryManager, NativeLibraryProxy>();
        AddNativeArtwork(library, old, protection);
        var service = new MetadataEnrichmentService(new(() => new PluginConfiguration()), null!, library, null!, new PreviousState(old));

        var result = Assert.Single(await service.EnrichAsync([fresh], CancellationToken.None));

        Assert.Equal(protection == "managed" ? fresh.SeasonPosterUrl : old.SeasonPosterUrl, result.SeasonPosterUrl);
        Assert.Equal(protection == "managed" ? fresh.ThumbnailUrl : old.ThumbnailUrl, result.ThumbnailUrl);
        Assert.Equal(fresh.BackdropUrl, result.BackdropUrl);
        Assert.Equal(fresh.LogoUrl, result.LogoUrl);
        Assert.Null(result.EpisodeBackdropUrl);
        Assert.Null(result.EpisodeLogoUrl);
    }

    [Fact]
    public async Task CachedParentPosterCopiesRemainEligibleForChildArtworkRepair()
    {
        var directory = Path.Combine(Path.GetTempPath(), "siphon-enrichment-images-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var parentPath = Path.Combine(directory, "parent.jpg");
            var childPath = Path.Combine(directory, "child.jpg");
            File.WriteAllBytes(parentPath, [1, 2, 3, 4]);
            File.WriteAllBytes(childPath, [1, 2, 3, 4]);
            var config = TmdbConfig();
            var item = CompleteEpisode() with { PosterUrl = "https://images.example/series.jpg", ThumbnailUrl = null };
            var library = DispatchProxy.Create<ILibraryManager, NativeLibraryProxy>();
            AddNativeArtwork(library, item, "managed", childPath);
            var parent = new Series { Id = library.GetNewItemId("siphon:series:" + item.ContentKey, typeof(Series)), DateLastSaved = new DateTime(2026, 1, 1) };
            parent.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = parentPath }, 0);
            ((NativeLibraryProxy)(object)library).Items[parent.Id] = parent;
            var origin = TmdbOrigin([], """{"id":123,"name":"Series","seasons":[{"id":101,"season_number":1,"poster_path":"/season-one.jpg"}]}""",
                """{"id":101,"season_number":1,"episodes":[{"id":1002,"show_id":123,"season_number":1,"episode_number":2,"still_path":"/episode-two.jpg"}]}""");
            using var client = new MetadataProviderClient(new(() => config), origin, null!);
            var service = new MetadataEnrichmentService(new(() => config), client, library, null!, new PreviousState(item));

            var result = Assert.Single(await service.EnrichAsync([item], CancellationToken.None));

            Assert.Equal("https://image.tmdb.org/t/p/original/season-one.jpg", result.SeasonPosterUrl);
            Assert.Equal("https://image.tmdb.org/t/p/original/episode-two.jpg", result.ThumbnailUrl);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryDistinguishesUserLocksFromOrdinaryImagePolicyPreservation(bool locked)
    {
        var directory = Path.Combine(Path.GetTempPath(), "siphon-enrichment-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = DispatchProxy.Create<IApplicationPaths, MetadataCacheTests.PathsProxy>();
            ((MetadataCacheTests.PathsProxy)(object)paths).Directory = directory;
            using var diagnostics = new SyncDiagnostics(new SiphonPaths(paths), NullLogger<SyncDiagnostics>.Instance);
            var old = CompleteEpisode() with { ContentKey = "series:tt0137523", ThumbnailUrl = null };
            var fresh = old with { ThumbnailUrl = "https://images.example/new-still.jpg", SeasonPosterUrl = "https://images.example/new-season.jpg" };
            var library = DispatchProxy.Create<ILibraryManager, NativeLibraryProxy>();
            AddNativeArtwork(library, old, locked ? "locked" : "manual");
            diagnostics.BeginRun("Catalogs");
            diagnostics.RecordDecision(old.ContentKey, old.SeriesName!, "Pending", "Scheduled");
            var service = new MetadataEnrichmentService(new(() => new PluginConfiguration()), null!, library, null!, new PreviousState(old), diagnostics);

            await service.EnrichAsync([fresh], CancellationToken.None);

            var run = Assert.Single(diagnostics.GetHistory());
            var decision = Assert.Single(diagnostics.GetHistoryDetails(run.Id, 0, 10)!.Items);
            Assert.Equal(locked, decision.Reasons.Contains("MetadataLocked"));
            Assert.Equal(locked ? "Preserved" : "Pending", decision.Action);
            Assert.Equal(old.ContentKey, decision.ContentKey);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("""{"id":999,"season_number":1,"poster_path":"/wrong.jpg","episodes":[]}""")]
    [InlineData("""{"id":101,"season_number":2,"poster_path":"/wrong.jpg","episodes":[]}""")]
    [InlineData("""{"id":101,"season_number":1,"poster_path":"/wrong.jpg","episodes":[{"id":1002,"show_id":999,"season_number":1,"episode_number":2,"still_path":"/wrong.jpg"}]}""")]
    public async Task UnrelatedSeasonOrSeriesResponsesCannotSupplyChildArtwork(string response)
    {
        var config = TmdbConfig();
        var item = CompleteEpisode() with { ThumbnailUrl = null };
        var origin = TmdbOrigin([], """{"id":123,"name":"Series","seasons":[{"id":101,"season_number":1}]}""", response);
        using var client = new MetadataProviderClient(new(() => config), origin, null!);
        var library = DispatchProxy.Create<ILibraryManager, NativeLibraryProxy>();
        var service = new MetadataEnrichmentService(new(() => config), client, library, null!, new PreviousState(item));

        var result = Assert.Single(await service.EnrichAsync([item], CancellationToken.None));

        Assert.Null(result.SeasonPosterUrl);
        Assert.Null(result.ThumbnailUrl);
        Assert.Empty(result.EpisodeProviderIds);
        Assert.Equal("InvalidResponse", client.GetStatus().Single(status => status.Provider == "Tmdb").LastResult?.Code);
    }

    [Fact]
    public async Task MatchingEpisodeNumberDoesNotReplaceDifferentKnownEpisodeIdentity()
    {
        var config = TmdbConfig();
        var item = CompleteEpisode() with { ThumbnailUrl = null, SeasonPosterUrl = "https://images.example/season.jpg", EpisodeProviderIds = new() { ["Tmdb"] = "2002" } };
        var origin = TmdbOrigin([], """{"id":123,"name":"Series"}""",
            """{"id":101,"season_number":1,"episodes":[{"id":1002,"show_id":123,"season_number":1,"episode_number":2,"still_path":"/wrong.jpg"}]}""");
        using var client = new MetadataProviderClient(new(() => config), origin, null!);
        var library = DispatchProxy.Create<ILibraryManager, NativeLibraryProxy>();
        var service = new MetadataEnrichmentService(new(() => config), client, library, null!, new PreviousState(item));

        var result = Assert.Single(await service.EnrichAsync([item], CancellationToken.None));

        Assert.Null(result.ThumbnailUrl);
        Assert.Equal("2002", result.EpisodeProviderIds["Tmdb"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SelectedAddonOnlyFillsNumberedSeasonArtAndPreservesEveryOtherField(bool refreshSelected, bool force)
    {
        var config = SelectedAddonConfig();
        if (refreshSelected)
        {
            config.MetadataUpdateMode = "RefreshSelected";
            config.MetadataRefreshFields = ["Name", "Overview", "ReleaseDate", "Genres", "Ratings", "Runtime", "People", "Images"];
        }
        var first = CompleteEpisode() with
        {
            SeriesName = "Addon series",
            SeriesDescription = "Addon series plot",
            SeriesReleased = "2020-01-01",
            Year = 2020,
            CommunityRating = 8,
            RunTimeTicks = TimeSpan.TicksPerMinute * 50,
            OfficialRating = "TV-14",
            Genres = ["Drama"],
            ProductionLocations = ["France"],
            People = [new("Addon actor", "Actor")],
            SeriesStatus = "Continuing",
            PosterUrl = "https://images.example/addon-parent.jpg",
            BackdropUrl = "https://images.example/addon-background.jpg",
            LogoUrl = "https://images.example/addon-logo.png",
            EpisodeBackdropUrl = "https://images.example/addon-episode-background.jpg",
            EpisodeLogoUrl = "https://images.example/addon-episode-logo.png",
            EpisodeGenres = ["Mystery"],
            EpisodeProductionLocations = ["Italy"],
            EpisodeOfficialRating = "TV-PG",
            ProviderIds = new() { ["Imdb"] = "tt0137523" }
        };
        first = first with { SeasonPosterUrl = first.PosterUrl };
        var special = first with { Key = "special", Season = 0, SeasonPosterUrl = null, SeriesDescription = null, ThumbnailUrl = null };
        var complete = first with { Key = "complete-season", Season = 2, SeasonPosterUrl = "https://images.example/addon-season-two.jpg" };
        var requests = new List<Uri>();
        var origin = new Origin((uri, _) =>
        {
            requests.Add(uri);
            var json = uri.AbsolutePath switch
            {
                "/3/find/tt0137523" => """{"tv_results":[{"id":123}]}""",
                "/3/tv/123" => """{"id":123,"name":"Wrong title","overview":"Wrong plot","first_air_date":"1990-01-01","poster_path":"/wrong-parent.jpg","backdrop_path":"/wrong-background.jpg","vote_count":99,"vote_average":1,"genres":[{"name":"Wrong genre"}],"credits":{"cast":[{"name":"Wrong actor"}]},"seasons":[{"id":102,"season_number":2,"poster_path":"/wrong-season-two.jpg"},{"id":101,"season_number":1,"poster_path":"/season-one.jpg"},{"id":100,"season_number":0,"poster_path":"/specials.jpg"}]}""",
                "/3/configuration" => """{"images":{"secure_base_url":"https://image.tmdb.org/t/p/"}}""",
                _ => throw new InvalidOperationException("Unexpected selected-source request.")
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(json) };
        });
        using var client = new MetadataProviderClient(new(() => config), origin, null!);
        var library = DispatchProxy.Create<ILibraryManager, NativeLibraryProxy>();
        var service = new MetadataEnrichmentService(new(() => config), client, library, null!, new PreviousState(first, special, complete));

        var result = await service.EnrichAsync([first, special, complete], CancellationToken.None, forceRefresh: force);

        AssertSameMetadataValues(first with { SeasonPosterUrl = "https://image.tmdb.org/t/p/original/season-one.jpg" }, result[0]);
        AssertSameMetadataValues(special with { SeasonPosterUrl = "https://image.tmdb.org/t/p/original/specials.jpg" }, result[1]);
        AssertSameMetadataValues(complete, result[2]);
        Assert.Equal(new[] { "/3/find/tt0137523", "/3/tv/123", "/3/configuration" }, requests.Select(uri => uri.AbsolutePath));
        Assert.All(requests, uri => Assert.Equal("api.themoviedb.org", uri.Host));
        Assert.DoesNotContain(requests, uri => uri.Query.Contains("append_to_response", StringComparison.Ordinal));
    }

    private static void AssertSameMetadataValues(ManagedItem expected, ManagedItem actual)
        => Assert.Equivalent(expected, actual with { MetadataProvenance = expected.MetadataProvenance }, strict: true);

    [Theory]
    [InlineData("movie")]
    [InlineData("complete")]
    [InlineData("manual")]
    [InlineData("locked")]
    [InlineData("unselected")]
    [InlineData("disabled")]
    public async Task SelectedAddonIneligibleSeasonArtSpendsNoRequestsEvenOnFullRefresh(string reason)
    {
        var config = SelectedAddonConfig();
        config.MetadataUpdateMode = "RefreshSelected";
        config.MetadataRefreshFields = reason == "unselected" ? ["Overview"] : ["Images", "Overview", "People", "Ratings"];
        if (reason == "disabled") config.EnableTmdbMetadata = false;
        var item = CompleteEpisode() with { PosterUrl = "https://images.example/parent.jpg", Description = null, ThumbnailUrl = null };
        if (reason == "movie") item = item with { Type = "movie" };
        if (reason == "complete") item = item with { SeasonPosterUrl = "https://images.example/season-one.jpg" };
        var library = DispatchProxy.Create<ILibraryManager, NativeLibraryProxy>();
        if (reason is "manual" or "locked") AddNativeArtwork(library, item, reason);
        var origin = new Origin((_, _) => new(HttpStatusCode.TooManyRequests));
        using var client = new MetadataProviderClient(new(() => config), origin, null!);
        var service = new MetadataEnrichmentService(new(() => config), client, library, null!, new PreviousState(item));

        var result = Assert.Single(await service.EnrichAsync([item], CancellationToken.None, forceRefresh: true));

        AssertSameMetadataValues(item, result);
        Assert.Equal(0, origin.Calls);
    }

    [Fact]
    public async Task SelectedAddonFetchesOnlyTheSeasonWhoseParentPosterIsMissing()
    {
        var config = SelectedAddonConfig();
        config.MetadataUpdateMode = "RefreshSelected";
        config.MetadataRefreshFields = ["Images", "Name", "Overview", "People", "Ratings"];
        var first = CompleteEpisode() with { Name = "Addon title", Description = null, ThumbnailUrl = null, EpisodePeople = [] };
        var second = first with { Key = "second-season", Season = 2 };
        var paths = new List<string>();
        var origin = TmdbOrigin(paths,
            """{"id":123,"name":"Wrong series","seasons":[{"id":101,"season_number":1},{"id":102,"season_number":2,"poster_path":"/season-two.jpg"}]}""",
            """{"id":101,"season_number":1,"poster_path":"/season-one.jpg","episodes":[{"id":1002,"show_id":123,"season_number":1,"episode_number":2,"name":"Wrong episode","overview":"Wrong plot","still_path":"/wrong-still.jpg","vote_count":1,"vote_average":1,"guest_stars":[{"name":"Wrong guest"}]}]}""");
        using var client = new MetadataProviderClient(new(() => config), origin, null!);
        var library = DispatchProxy.Create<ILibraryManager, NativeLibraryProxy>();
        var service = new MetadataEnrichmentService(new(() => config), client, library, null!, new PreviousState(first, second));

        var result = await service.EnrichAsync([first, second], CancellationToken.None, forceRefresh: true);

        AssertSameMetadataValues(first with { SeasonPosterUrl = "https://image.tmdb.org/t/p/original/season-one.jpg" }, result[0]);
        AssertSameMetadataValues(second with { SeasonPosterUrl = "https://image.tmdb.org/t/p/original/season-two.jpg" }, result[1]);
        Assert.Equal(new[] { "/3/tv/123", "/3/configuration", "/3/tv/123/season/1" }, paths);
    }

    [Fact]
    public async Task SelectedAddonUsesSharedCachedPostersDuringPersistedHardPauseButForceCannotBypassIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "siphon-season-supplement-" + Guid.NewGuid().ToString("N"));
        try
        {
            var applicationPaths = DispatchProxy.Create<IApplicationPaths, MetadataCacheTests.PathsProxy>();
            ((MetadataCacheTests.PathsProxy)(object)applicationPaths).Directory = directory;
            var paths = new SiphonPaths(applicationPaths);
            var config = SelectedAddonConfig();
            var item = CompleteEpisode();
            var expected = item with { SeasonPosterUrl = "https://image.tmdb.org/t/p/original/season-one.jpg" };
            var paused = false;
            var origin = new Origin((uri, _) =>
            {
                if (paused)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
                    return response;
                }
                var json = uri.AbsolutePath switch
                {
                    "/3/tv/123" => """{"id":123,"name":"Series","seasons":[{"id":101,"season_number":1,"poster_path":"/season-one.jpg"}]}""",
                    "/3/configuration" => """{"images":{"secure_base_url":"https://image.tmdb.org/t/p/"}}""",
                    _ => throw new InvalidOperationException("Unexpected season supplement request.")
                };
                return new(HttpStatusCode.OK) { Content = new StringContent(json) };
            });
            var library = DispatchProxy.Create<ILibraryManager, NativeLibraryProxy>();
            using (var cache = new MetadataResponseCache(paths))
            using (var client = new MetadataProviderClient(new(() => config), origin, null!, cache,
                quotas: new MetadataQuotaStore(paths, NullLogger<MetadataQuotaStore>.Instance)))
            {
                var service = new MetadataEnrichmentService(new(() => config), client, library, null!, new PreviousState(item));
                AssertSameMetadataValues(expected, Assert.Single(await service.EnrichAsync([item], CancellationToken.None)));
                paused = true;
                Assert.Equal("RateLimited", (await client.TestAsync("Tmdb", CancellationToken.None)).Code);
                Assert.Equal(3, origin.Calls);
            }
            using var restartedCache = new MetadataResponseCache(paths);
            using var restartedClient = new MetadataProviderClient(new(() => config), origin, null!, restartedCache,
                quotas: new MetadataQuotaStore(paths, NullLogger<MetadataQuotaStore>.Instance));
            var restarted = new MetadataEnrichmentService(new(() => config), restartedClient, library, null!, new PreviousState(item));
            var anotherEpisode = item with { Key = "another-episode", Episode = 3 };

            var cached = await restarted.EnrichAsync([item, anotherEpisode], CancellationToken.None);
            AssertSameMetadataValues(expected, cached[0]);
            AssertSameMetadataValues(anotherEpisode with { SeasonPosterUrl = expected.SeasonPosterUrl }, cached[1]);
            AssertSameMetadataValues(item, Assert.Single(await restarted.EnrichAsync([item], CancellationToken.None, forceRefresh: true)));
            Assert.Equal("RateLimited", restartedClient.GetStatus().Single(status => status.Provider == "Tmdb").LastResult?.Code);
            Assert.Equal(3, origin.Calls);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static PluginConfiguration SelectedAddonConfig() => new()
    {
        MetadataAddonId = "4771d266-2b88-44e3-ab49-267ff9a59188",
        EnableTmdbMetadata = true,
        TmdbReadAccessToken = "synthetic-token",
        EnableTvdbMetadata = true,
        TvdbApiKey = "synthetic-key",
        EnableFanartMetadata = true,
        FanartApiKey = "synthetic-key",
        EnableMdbListMetadata = true,
        MdbListApiKey = "synthetic-key",
        MetadataLanguage = "en"
    };

    private static PluginConfiguration TmdbConfig() => new() { EnableTmdbMetadata = true, TmdbReadAccessToken = "synthetic-token", MetadataLanguage = "en" };

    private static ManagedItem CompleteEpisode() => MetadataPolicyTests.EpisodeItem() with
    {
        Description = "Existing episode plot",
        Released = "2021-01-01",
        ThumbnailUrl = "https://images.example/existing.jpg",
        EpisodeRunTimeTicks = TimeSpan.TicksPerMinute * 40,
        EpisodeCommunityRating = 7,
        EpisodePeople = [new("Existing actor", "Actor")],
        ProviderIds = new() { ["Tmdb"] = "123" }
    };

    private static Origin TmdbOrigin(List<string> paths, string parent, string? season = null) => new((uri, _) =>
    {
        paths.Add(uri.AbsolutePath);
        var json = uri.AbsolutePath switch
        {
            "/3/tv/123" => parent,
            "/3/configuration" => """{"images":{"secure_base_url":"https://image.tmdb.org/t/p/"}}""",
            "/3/tv/123/season/1" when season is not null => season,
            _ => throw new InvalidOperationException("Unexpected provider resource: " + uri.AbsolutePath)
        };
        return new(HttpStatusCode.OK) { Content = new StringContent(json) };
    });

    private static void AddNativeArtwork(ILibraryManager library, ManagedItem item, string protection, string? cachedPath = null)
    {
        void Add(BaseItem target, string key, string variant)
        {
            target.Id = library.GetNewItemId(key, target.GetType());
            target.DateLastSaved = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            target.IsLocked = protection == "locked";
            target.SetProviderId("SiphonImagePrimary", "revision");
            target.SetImage(new ItemImageInfo
            {
                Type = ImageType.Primary,
                Path = cachedPath ?? (protection == "manual" ? "/manual/poster.jpg" : "https://jellyfin.example/Siphon/image/synthetic?type=" + variant),
                DateModified = target.DateLastSaved
            }, 0);
            ((NativeLibraryProxy)(object)library).Items[target.Id] = target;
        }
        Add(new Season(), "siphon:season:" + item.ContentKey + ":season:" + item.Season, "season");
        Add(new Episode(), "siphon:media:" + item.Key, "thumb");
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
        public Dictionary<Guid, BaseItem> Items { get; } = [];
        private readonly ConcurrentDictionary<string, Guid> _ids = new(StringComparer.Ordinal);

        private Guid ItemId(string key) => _ids.GetOrAdd(key, static _ => Guid.NewGuid());
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "GetNewItemId" => ItemId((string)args![0]!),
            "GetItemById" => Items.GetValueOrDefault((Guid)args![0]!) ?? Item,
            "GetVirtualFolders" => new List<VirtualFolderInfo>(),
            "GetPeople" => new List<PersonInfo>(),
            _ => throw new InvalidOperationException("Unexpected native lookup.")
        };
    }

    private sealed class PreviousState(params ManagedItem[] items) : ISiphonStateStore
    {
        public ManagedItem? FindByKey(string key) => items.FirstOrDefault(item => item.Key == key);
        public IReadOnlyList<ManagedItem> GetItems() => throw new InvalidOperationException("Full-state cloning is not needed for locks.");
        public ManagedItem? FindByPath(string path) => throw new NotSupportedException();
        public ManagedItem? FindByContentKey(string key) => throw new NotSupportedException();
        public Task SaveAsync(IReadOnlyList<ManagedItem> items, CancellationToken ct) => throw new NotSupportedException();
    }
}
