using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Metadata;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Identity;

public sealed class MetadataCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "siphon-metadata-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly PluginConfiguration _config = new() { TmdbReadAccessToken = "synthetic-private-token", EnableTmdbMetadata = true, MetadataCacheHours = 24 };
    private SiphonPaths Paths()
    {
        var paths = DispatchProxy.Create<IApplicationPaths, PathsProxy>();
        ((PathsProxy)(object)paths).Directory = _directory;
        return new(paths);
    }

    [Fact]
    public async Task EnrichmentReusesRestartedCacheAndRefetchesNewEpisodesWhileCountingSeriesOnce()
    {
        _config.MetadataLanguage = "en";
        var seasons = 0;
        var origin = new Origin((uri, _) => Json(uri.AbsolutePath switch
        {
            "/3/tv/123" => """{"id":123,"name":"Series","overview":"Series plot"}""",
            "/3/configuration" => """{"images":{"secure_base_url":"https://image.tmdb.org/t/p/"}}""",
            "/3/tv/123/season/1" when ++seasons == 1 => """{"id":100,"season_number":1,"episodes":[{"id":101,"season_number":1,"episode_number":1,"name":"One","overview":"First plot"}]}""",
            "/3/tv/123/season/1" => """{"id":100,"season_number":1,"episodes":[{"id":101,"season_number":1,"episode_number":1,"name":"One","overview":"First plot"},{"id":102,"season_number":1,"episode_number":2,"name":"Two","overview":"New episode plot"}]}""",
            _ => throw new InvalidOperationException("Unexpected resource")
        }));
        var first = MetadataPolicyTests.EpisodeItem() with { Season = 1, Episode = 1, ProviderIds = new() { ["Tmdb"] = "123" } };
        var second = first with { Key = "episode-two", Episode = 2 };
        var library = DispatchProxy.Create<ILibraryManager, MetadataProviderTests.NativeLibraryProxy>();
        using var state = new SiphonStateStore(Paths());
        using var diagnostics = new SyncDiagnostics(Paths(), NullLogger<SyncDiagnostics>.Instance);
        diagnostics.BeginRun("Catalogs");
        using (var cache = new MetadataResponseCache(Paths()))
        using (var client = Client(origin, cache))
        {
            var service = new MetadataEnrichmentService(new(() => _config), client, library, null!, state, diagnostics);
            Assert.Equal("First plot", (await service.EnrichAsync([first], CancellationToken.None))[0].Description);
        }
        Assert.Equal(3, origin.Calls);
        using var restartedCache = new MetadataResponseCache(Paths());
        using var restartedClient = Client(origin, restartedCache);
        var restarted = new MetadataEnrichmentService(new(() => _config), restartedClient, library, null!, state, diagnostics);
        await restarted.EnrichAsync([first], CancellationToken.None);
        Assert.Equal(3, origin.Calls);
        var enriched = await restarted.EnrichAsync([first, second], CancellationToken.None);
        Assert.Equal("New episode plot", enriched[1].Description);
        Assert.Equal(4, origin.Calls);
        await restarted.EnrichAsync(enriched, CancellationToken.None, forceRefresh: true);
        Assert.Equal(7, origin.Calls);
        var run = diagnostics.GetRunStatus().CurrentRun!;
        Assert.Equal("Titles", run.Unit);
        Assert.Equal(1, run.Completed);
        Assert.Equal(1, run.Total);
    }

    [Fact]
    public async Task ValidatedResponsesSurviveRestartButExpireAtConfiguredBoundary()
    {
        var origin = new Origin((_, _) => Json("""{"id":123,"name":"Saved series"}"""));
        using (var cache = new MetadataResponseCache(Paths()))
        using (var client = Client(origin, cache))
        {
            Assert.Equal("Saved series", (await Fetch(client)).Name);
        }
        using var restartedCache = new MetadataResponseCache(Paths());
        using var restarted = Client(origin, restartedCache);
        _clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromTicks(1));
        Assert.Equal("Saved series", (await Fetch(restarted)).Name);
        Assert.Equal(1, origin.Calls);
        _clock.Advance(TimeSpan.FromTicks(1));
        await Fetch(restarted);
        Assert.Equal(2, origin.Calls);
        var files = Directory.GetFiles(Path.Combine(Paths().DataDirectory, "metadata-cache-v1"), "*.json");
        foreach (var file in files)
        {
            Assert.DoesNotContain("synthetic-private-token", file);
            var content = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain("synthetic-private-token", content);
            Assert.DoesNotContain("api.themoviedb.org", content);
        }
    }

    [Fact]
    public async Task InspectionCacheAgeTracksOriginalFetchAcrossRestartRatherThanLatestMerge()
    {
        _config.MetadataLanguage = "en";
        var observed = _clock.GetUtcNow();
        var origin = new Origin((uri, _) => Json(uri.AbsolutePath switch
        {
            "/3/movie/123" => """{"id":123,"title":"Cached movie","overview":"Cached plot"}""",
            "/3/configuration" => """{"images":{"secure_base_url":"https://image.tmdb.org/t/p/"}}""",
            _ => throw new InvalidOperationException("Unexpected resource")
        }));
        var movie = MetadataPolicyTests.EpisodeItem() with
        {
            Type = "movie",
            Name = "",
            SeriesName = null,
            Season = null,
            Episode = null,
            ProviderIds = new() { ["Tmdb"] = "123" }
        };
        var library = DispatchProxy.Create<ILibraryManager, MetadataProviderTests.NativeLibraryProxy>();
        using var state = new SiphonStateStore(Paths());
        using (var cache = new MetadataResponseCache(Paths()))
        using (var client = Client(origin, cache))
            await new MetadataEnrichmentService(new(() => _config), client, library, null!, state).EnrichAsync([movie], CancellationToken.None);
        _clock.Advance(TimeSpan.FromHours(3));
        using var restartedCache = new MetadataResponseCache(Paths());
        using var restartedClient = Client(origin, restartedCache);
        var result = Assert.Single(await new MetadataEnrichmentService(new(() => _config), restartedClient, library, null!, state).EnrichAsync([movie], CancellationToken.None));
        var field = Diagnostics.ItemInspectionController.Describe("Managed.Name", true, result.MetadataProvenance["Name"],
            null, false, null, _clock.GetUtcNow());
        Assert.Equal("Cached movie", result.Name);
        Assert.Equal("Tmdb", field.Origin);
        Assert.Equal(observed, field.ObservedAtUtc);
        Assert.Equal(observed, field.CacheCreatedAtUtc);
        Assert.Equal(observed.AddHours(24), field.CacheExpiresAtUtc);
        Assert.Equal(TimeSpan.FromHours(3).TotalSeconds, field.CacheAgeSeconds);
        Assert.Equal(2, origin.Calls);
    }

    [Theory]
    [InlineData("force")]
    [InlineData("language")]
    [InlineData("credentials")]
    [InlineData("identifier")]
    public async Task ChangedRequestContextDoesNotReuseOldResponse(string change)
    {
        var fresh = false;
        var origin = new Origin((uri, headers) => Json(uri.AbsolutePath.EndsWith("/124", StringComparison.Ordinal)
            ? """{"id":124,"name":"Different identity"}"""
            : headers?["Authorization"] == "Bearer replacement-private-token" ? """{"id":123,"name":"Different account"}"""
            : uri.Query.Contains("language=fr", StringComparison.Ordinal) ? """{"id":123,"name":"Série"}"""
            : fresh ? """{"id":123,"name":"Fresh series"}""" : """{"id":123,"name":"Series"}"""));
        using var cache = new MetadataResponseCache(Paths());
        using var client = Client(origin, cache);
        await Fetch(client);
        await Fetch(client);
        Assert.Equal(1, origin.Calls);
        fresh = true;
        if (change == "credentials") _config.TmdbReadAccessToken = "replacement-private-token";
        var value = await Fetch(client, change == "force", change == "language" ? "fr" : "en", change == "identifier" ? 124 : 123);
        Assert.Equal(change switch { "credentials" => "Different account", "language" => "Série", "identifier" => "Different identity", _ => "Fresh series" }, value.Name);
        Assert.Equal(2, origin.Calls);
    }

    [Fact]
    public async Task MergePreferencesDoNotInvalidateAnUnchangedProviderResponse()
    {
        var origin = new Origin((_, _) => Json("""{"id":123,"name":"Saved series"}"""));
        using var cache = new MetadataResponseCache(Paths());
        using var client = Client(origin, cache);
        await Fetch(client);
        _config.MetadataRefreshFields = ["Name"];
        _config.MetadataUpdateMode = "RefreshSelected";
        _config.EnableFanartMetadata = true;
        Assert.Equal("Saved series", (await Fetch(client)).Name);
        Assert.Equal(1, origin.Calls);
    }

    [Fact]
    public async Task ImageConfigurationIsSharedAcrossTitlesButRefreshedForEachForcedRun()
    {
        _config.MetadataLanguage = "en";
        _config.MetadataUpdateMode = "RefreshSelected";
        _config.MetadataRefreshFields = ["Images"];
        var imageBase = "old";
        var configurations = 0;
        var origin = new Origin((uri, _) =>
        {
            if (uri.AbsolutePath == "/3/configuration")
            {
                configurations++;
                return Json($$$"""{"images":{"secure_base_url":"https://image.tmdb.org/{{{imageBase}}}/"}}""");
            }
            return Json(uri.AbsolutePath.EndsWith("/123", StringComparison.Ordinal)
                ? """{"id":123,"title":"First movie","poster_path":"/123.jpg"}"""
                : """{"id":124,"title":"Second movie","poster_path":"/124.jpg"}""");
        });
        var first = MetadataPolicyTests.EpisodeItem() with { Key = "first", ContentKey = "movie:first", Type = "movie", ProviderIds = new() { ["Tmdb"] = "123" } };
        var second = first with { Key = "second", ContentKey = "movie:second", ProviderIds = new() { ["Tmdb"] = "124" } };
        var library = DispatchProxy.Create<ILibraryManager, MetadataProviderTests.NativeLibraryProxy>();
        using var state = new SiphonStateStore(Paths());
        using var cache = new MetadataResponseCache(Paths());
        using var client = Client(origin, cache);
        var service = new MetadataEnrichmentService(new(() => _config), client, library, null!, state);
        var saved = await service.EnrichAsync([first, second], CancellationToken.None);
        Assert.All(saved, item => Assert.StartsWith("https://image.tmdb.org/old/", item.PosterUrl));
        imageBase = "fresh";
        var refreshed = await service.EnrichAsync(saved, CancellationToken.None, forceRefresh: true);
        Assert.All(refreshed, item => Assert.StartsWith("https://image.tmdb.org/fresh/", item.PosterUrl));
        Assert.Equal(2, configurations);
        Assert.Equal(6, origin.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentTitlesShareTheLastAllowedResponseWithoutBypassingForceOrCancellation(bool force)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var origin = new AsyncOrigin(async ct =>
        {
            if (++calls == 1 && force) return Json("""{"id":123,"name":"Old response"}""");
            entered.TrySetResult();
            return await release.Task.WaitAsync(ct);
        });
        using var cache = new MetadataResponseCache(Paths());
        using var client = Client(origin, cache);
        if (force) await Fetch(client);
        using (client.BeginScope(_config, force, null))
        {
            var first = Get(client);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = Get(client);
            using var cancellation = new CancellationTokenSource();
            var cancelled = client.GetAsync<TmdbRecord>("Tmdb", "https://api.themoviedb.org/3/tv/124", null, true, cancellation.Token, value => value.Id == 124);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            var response = Json("""{"id":123,"name":"Last allowed response"}""");
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", _clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            release.SetResult(response);
            Assert.All(await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5)), value => Assert.Equal("Last allowed response", value.Name));
            Assert.Equal("Last allowed response", (await Get(client)).Name);
        }
        Assert.Equal("RateLimited", (await Assert.ThrowsAsync<MetadataProviderClient.ProviderFailure>(() => Fetch(client, force: true))).Code);
        Assert.Equal(force ? 2 : 1, calls);
    }

    [Fact]
    public async Task CooldownsSurviveRestartAndCredentialRoundTripWhileCachedMetadataRemainsUsable()
    {
        var reset = _clock.GetUtcNow().AddHours(1);
        var origin = new Origin((uri, headers) =>
        {
            if (uri.AbsolutePath != "/3/configuration") return Json(_clock.GetUtcNow() < reset
                ? """{"id":123,"name":"Saved before quota exhaustion"}""" : """{"id":123,"name":"Refreshed after reset"}""");
            var response = Json("""{"images":{"secure_base_url":"https://image.tmdb.org/t/p/"}}""");
            response.Headers.Add("X-RateLimit-Remaining", headers?["Authorization"] == "Bearer synthetic-private-token" ? "0" : "10");
            response.Headers.Add("X-RateLimit-Limit", "100");
            response.Headers.Add("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            return response;
        });
        using (var cache = new MetadataResponseCache(Paths()))
        using (var client = Client(origin, cache))
        {
            await Fetch(client);
            Assert.True((await client.TestAsync("Tmdb", CancellationToken.None)).Success);
        }
        using var restartedCache = new MetadataResponseCache(Paths());
        using var restarted = Client(origin, restartedCache);
        Assert.Equal(reset, restarted.GetStatus().Single(value => value.Provider == "Tmdb").PausedUntilUtc);
        var observed = restarted.GetStatus().Single(value => value.Provider == "Tmdb");
        Assert.Equal(100L, observed.QuotaLimit);
        Assert.Equal(0L, observed.QuotaRemaining);
        Assert.Equal(reset, observed.QuotaResetUtc);
        Assert.Equal(_clock.GetUtcNow(), observed.QuotaObservedAtUtc);
        Assert.Equal("RateLimited", (await restarted.TestAsync("Tmdb", CancellationToken.None)).Code);
        Assert.Equal("Saved before quota exhaustion", (await Fetch(restarted)).Name);
        Assert.Equal("RateLimited", (await Assert.ThrowsAsync<MetadataProviderClient.ProviderFailure>(() => Fetch(restarted, force: true))).Code);
        Assert.Equal(2, origin.Calls);
        _config.TmdbReadAccessToken = "replacement-private-token";
        Assert.Null(restarted.GetStatus().Single(value => value.Provider == "Tmdb").QuotaObservedAtUtc);
        Assert.True((await restarted.TestAsync("Tmdb", CancellationToken.None)).Success);
        Assert.Equal(10L, restarted.GetStatus().Single(value => value.Provider == "Tmdb").QuotaRemaining);
        _config.TmdbReadAccessToken = "synthetic-private-token";
        Assert.Equal("RateLimited", (await restarted.TestAsync("Tmdb", CancellationToken.None)).Code);
        Assert.Equal(0L, restarted.GetStatus().Single(value => value.Provider == "Tmdb").QuotaRemaining);
        Assert.Equal(3, origin.Calls);
        var persisted = await File.ReadAllTextAsync(Path.Combine(Paths().DataDirectory, "metadata-quotas.json"));
        Assert.DoesNotContain("synthetic-private-token", persisted);
        Assert.DoesNotContain("replacement-private-token", persisted);
        Assert.DoesNotContain("https://", persisted);
        _clock.Advance(TimeSpan.FromHours(1));
        var stale = restarted.GetStatus().Single(value => value.Provider == "Tmdb");
        Assert.Null(stale.QuotaRemaining);
        Assert.Equal(reset, stale.QuotaResetUtc);
        Assert.Equal(observed.QuotaObservedAtUtc, stale.QuotaObservedAtUtc);
        Assert.Equal("Refreshed after reset", (await Fetch(restarted, force: true)).Name);
        Assert.Equal(4, origin.Calls);
    }

    [Fact]
    public async Task ChangingCredentialsDuringARequestCannotReleaseQueuedRequestsForTheExhaustedKey()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var origin = new AsyncOrigin(async ct =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            return await release.Task.WaitAsync(ct);
        });
        using var client = Client(origin);
        var first = Get(client);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = Get(client, 124);
        _config.TmdbReadAccessToken = "different-account-token";
        client.GetStatus();
        var response = Json("""{"id":123,"name":"Successful last response"}""");
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", _clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        release.SetResult(response);
        Assert.Equal("Successful last response", (await first).Name);
        Assert.Equal("RateLimited", (await Assert.ThrowsAsync<MetadataProviderClient.ProviderFailure>(() => queued)).Code);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FailedCacheReplacementCannotMakeForceReuseAnOlderResponse()
    {
        var fresh = false;
        var origin = new Origin((_, _) => Json(fresh
            ? """{"id":123,"name":"Fresh response"}""" : """{"id":123,"name":"Old response"}"""));
        using var cache = new MetadataResponseCache(Paths());
        using var client = Client(origin, cache);
        await Fetch(client);
        Directory.CreateDirectory(Path.Combine(Paths().DataDirectory, "metadata-cache-v1", "pending.tmp"));
        fresh = true;
        using (client.BeginScope(_config, true, null))
        {
            Assert.Equal("Fresh response", (await Get(client)).Name);
            Assert.Equal("Fresh response", (await Get(client)).Name);
        }
        Assert.Equal(3, origin.Calls);
    }

    [Theory]
    [InlineData("Tmdb")]
    [InlineData("Tvdb")]
    [InlineData("Fanart")]
    [InlineData("MdbList")]
    public async Task EveryProviderHonorsRetryAfterHttpDates(string provider)
    {
        var calls = 0;
        var origin = new Origin((_, _) =>
        {
            if (++calls > 1) return Json("""{"id":123,"name":"Recovered"}""");
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(_clock.GetUtcNow().AddHours(1));
            return response;
        });
        using var client = Client(origin);
        Task<TmdbRecord> Request() => client.GetAsync<TmdbRecord>(provider, "https://provider.example/record", null, true, CancellationToken.None, value => value.Id == 123);
        Assert.Equal("RateLimited", (await Assert.ThrowsAsync<MetadataProviderClient.ProviderFailure>(Request)).Code);
        _clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromTicks(1));
        Assert.Equal("RateLimited", (await Assert.ThrowsAsync<MetadataProviderClient.ProviderFailure>(Request)).Code);
        Assert.Equal(1, calls);
        _clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal("Recovered", (await Request()).Name);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task AShortRetryDoesNotConsumeTheWholeResetWindowWhenBudgetRemains()
    {
        var calls = 0;
        var origin = new Origin((_, _) =>
        {
            if (++calls > 1) return Json("""{"images":{"secure_base_url":"https://image.tmdb.org/t/p/"}}""");
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.Add("X-RateLimit-Remaining", "10");
            response.Headers.Add("X-RateLimit-Reset", _clock.GetUtcNow().AddDays(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(10));
            return response;
        });
        using var client = Client(origin);
        Assert.Equal("RateLimited", (await client.TestAsync("Tmdb", CancellationToken.None)).Code);
        var observed = client.GetStatus().Single(value => value.Provider == "Tmdb");
        Assert.Equal(10L, observed.QuotaRemaining);
        Assert.Null(observed.QuotaLimit);
        Assert.Equal(_clock.GetUtcNow().AddDays(1), observed.QuotaResetUtc);
        Assert.Equal(_clock.GetUtcNow(), observed.QuotaObservedAtUtc);
        Assert.Equal(_clock.GetUtcNow().AddSeconds(10), observed.PausedUntilUtc);
        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True((await client.TestAsync("Tmdb", CancellationToken.None)).Success);
        Assert.Equal(2, calls);
        Assert.Equal(observed.QuotaObservedAtUtc, client.GetStatus().Single(value => value.Provider == "Tmdb").QuotaObservedAtUtc);
    }

    [Fact]
    public async Task InvalidResponsesAndCorruptStorageRemainMissesWithoutLosingFreshData()
    {
        var valid = false;
        var origin = new Origin((_, _) => Json(valid ? """{"id":123,"name":"Good"}""" : """{"id":999,"name":"Wrong identity"}"""));
        using var cache = new MetadataResponseCache(Paths());
        using var client = Client(origin, cache);
        for (var index = 0; index < 2; index++)
            Assert.Equal("InvalidResponse", (await Assert.ThrowsAsync<MetadataProviderClient.ProviderFailure>(() => Fetch(client))).Code);
        Assert.Equal(2, origin.Calls);
        valid = true;
        Assert.Equal("Good", (await Fetch(client)).Name);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(Paths().DataDirectory, "metadata-cache-v1"), "*.json"));
        await File.WriteAllTextAsync(file, "{ damaged");
        Assert.Equal("Good", (await Fetch(client)).Name);
        Assert.Equal(4, origin.Calls);
        Assert.Equal("Good", (await Fetch(client)).Name);
        Assert.Equal(4, origin.Calls);
    }

    [Fact]
    public async Task UnwritableCacheFallsBackToRealProviderRequests()
    {
        Directory.CreateDirectory(Paths().DataDirectory);
        await File.WriteAllTextAsync(Path.Combine(Paths().DataDirectory, "metadata-cache-v1"), "not a directory");
        using var cache = new MetadataResponseCache(Paths());
        var origin = new Origin((_, _) => Json("""{"id":123,"name":"Provider result"}"""));
        using var client = Client(origin, cache);
        Assert.Equal("Provider result", (await Fetch(client)).Name);
        Assert.Equal("Provider result", (await Fetch(client)).Name);
        Assert.Equal(2, origin.Calls);
    }

    [Fact]
    public async Task ManualTestsBypassCacheAndDoNotEnterEnrichmentCounters()
    {
        var origin = new Origin((uri, _) => Json(uri.AbsolutePath == "/3/configuration"
            ? """{"images":{"secure_base_url":"https://image.tmdb.org/t/p/"}}""" : """{"id":123,"name":"Series"}"""));
        using var cache = new MetadataResponseCache(Paths());
        using var client = Client(origin, cache);
        using var diagnostics = new SyncDiagnostics(Paths(), NullLogger<SyncDiagnostics>.Instance);
        diagnostics.BeginRun("Catalogs");
        using (client.BeginScope(_config, false, diagnostics))
        {
            await Get(client);
            await Get(client);
            Assert.True((await client.TestAsync("Tmdb", CancellationToken.None)).Success);
            Assert.True((await client.TestAsync("Tmdb", CancellationToken.None)).Success);
        }
        // A request outside enrichment scope is not part of this synchronization either.
        await Get(client);
        var provider = Assert.Single(diagnostics.GetRunStatus().CurrentRun!.Providers);
        Assert.Equal(1, provider.Requests);
        Assert.Equal(1, provider.CacheHits);
        Assert.Empty(provider.Errors);
        Assert.Equal(4, origin.Calls);
    }

    [Fact]
    public async Task TransientFailuresPauseOnlyTheirProviderAndRecoverAfterCooldown()
    {
        var failing = true;
        _config.MdbListApiKey = "synthetic-other-key";
        var origin = new Origin((uri, _) => uri.Host == "api.mdblist.com" ? Json("""{"username":"probe"}""")
            : failing ? new(HttpStatusCode.ServiceUnavailable) : Json("""{"id":123,"name":"Recovered series"}"""));
        using var client = Client(origin);
        for (var index = 0; index < 3; index++)
            Assert.Equal("ProviderUnavailable", (await client.TryAsync("Tmdb", _config, () => Get(client), CancellationToken.None)).Result.Code);
        var paused = client.GetStatus().Single(status => status.Provider == "Tmdb");
        Assert.Equal(3, paused.ConsecutiveFailures);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(5), paused.PausedUntilUtc);
        Assert.Equal("ProviderUnavailable", (await client.TryAsync("Tmdb", _config, () => Get(client), CancellationToken.None)).Result.Code);
        Assert.Equal(3, origin.Calls);
        Assert.True((await client.TestAsync("MdbList", CancellationToken.None)).Success);
        failing = false;
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("Recovered series", (await Get(client)).Name);
        Assert.Null(client.GetStatus().Single(status => status.Provider == "Tmdb").PausedUntilUtc);
        Assert.Equal(0, client.GetStatus().Single(status => status.Provider == "Tmdb").ConsecutiveFailures);
    }

    [Fact]
    public async Task LastAllowedResponsePausesBeforeAnotherRequestAndResumesAtReset()
    {
        _config.MdbListApiKey = "synthetic-mdb-key";
        var reset = _clock.GetUtcNow().AddHours(1);
        var mdbCalls = 0;
        var origin = new Origin((uri, _) =>
        {
            if (uri.Host != "api.mdblist.com") return Json("""{"images":{"secure_base_url":"https://image.tmdb.org/t/p/"}}""");
            var response = Json("""{"username":"fixture"}""");
            response.Headers.Add("X-RateLimit-Remaining", ++mdbCalls == 1 ? "0" : "10");
            response.Headers.Add("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            return response;
        });
        using var client = Client(origin);
        Assert.True((await client.TestAsync("MdbList", CancellationToken.None)).Success);
        Assert.Equal(reset, client.GetStatus().Single(value => value.Provider == "MdbList").PausedUntilUtc);
        Assert.Equal("RateLimited", (await client.TestAsync("MdbList", CancellationToken.None)).Code);
        Assert.True((await client.TestAsync("Tmdb", CancellationToken.None)).Success);
        _clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromTicks(1));
        Assert.Equal("RateLimited", (await client.TestAsync("MdbList", CancellationToken.None)).Code);
        Assert.Equal(1, mdbCalls);
        _clock.Advance(TimeSpan.FromTicks(1));
        Assert.True((await client.TestAsync("MdbList", CancellationToken.None)).Success);
        Assert.Equal(2, mdbCalls);
        Assert.Null(client.GetStatus().Single(value => value.Provider == "MdbList").PausedUntilUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(60)]
    [InlineData(7200)]
    public async Task ExhaustedQuotaNeverResumesBeforeEitherAdvertisedDeadline(int? retrySeconds)
    {
        _config.MdbListApiKey = "synthetic-mdb-key";
        var reset = _clock.GetUtcNow().AddHours(1);
        var until = retrySeconds is > 3600 ? _clock.GetUtcNow().AddSeconds(retrySeconds.Value) : reset;
        var origin = new Origin((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            if (retrySeconds.HasValue) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retrySeconds.Value));
            return response;
        });
        using var client = Client(origin);
        Assert.Equal("RateLimited", (await client.TestAsync("MdbList", CancellationToken.None)).Code);
        Assert.Equal(until, client.GetStatus().Single(value => value.Provider == "MdbList").PausedUntilUtc);
        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal("RateLimited", (await client.TestAsync("MdbList", CancellationToken.None)).Code);
        Assert.Equal(1, origin.Calls);
    }

    [Fact]
    public async Task RetryAfterSurvivesHalfOpenFailureAndCredentialChangeClearsObsoletePause()
    {
        var calls = 0;
        var origin = new Origin((_, _) =>
        {
            if (++calls <= 3) return new(HttpStatusCode.ServiceUnavailable);
            if (calls == 4)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
                return limited;
            }
            return Json("""{"images":{"secure_base_url":"https://image.tmdb.org/t/p/"}}""");
        });
        using var client = Client(origin);
        for (var index = 0; index < 3; index++) await client.TestAsync("Tmdb", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("RateLimited", (await client.TestAsync("Tmdb", CancellationToken.None)).Code);
        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal("RateLimited", (await client.TestAsync("Tmdb", CancellationToken.None)).Code);
        Assert.Equal(4, origin.Calls);
        _config.TmdbReadAccessToken = "new-credential";
        Assert.True((await client.TestAsync("Tmdb", CancellationToken.None)).Success);
        Assert.Null(client.GetStatus().Single(status => status.Provider == "Tmdb").PauseReason);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, 3)]
    [InlineData(HttpStatusCode.Unauthorized, 1)]
    public async Task ExplicitProbeCanRecoverBeforeCircuitExpiry(HttpStatusCode failure, int failures)
    {
        var healthy = false;
        var origin = new Origin((_, _) => healthy
            ? Json("""{"images":{"secure_base_url":"https://image.tmdb.org/t/p/"}}""") : new(failure));
        using var client = Client(origin);
        for (var index = 0; index < failures; index++) await client.TestAsync("Tmdb", CancellationToken.None);
        Assert.NotNull(client.GetStatus().Single(status => status.Provider == "Tmdb").PausedUntilUtc);
        healthy = true;
        Assert.True((await client.TestAsync("Tmdb", CancellationToken.None)).Success);
        Assert.Equal(failures + 1, origin.Calls);
        Assert.Null(client.GetStatus().Single(status => status.Provider == "Tmdb").PausedUntilUtc);
    }

    [Fact]
    public async Task CallerCancellationDoesNotCountAsProviderFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var origin = new Origin((_, _) => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); });
        using var client = Client(origin);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.TestAsync("Tmdb", cancellation.Token));
        var status = client.GetStatus().Single(value => value.Provider == "Tmdb");
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.Null(status.LastResult);
        Assert.Null(status.PausedUntilUtc);
    }

    [Fact]
    public async Task BudgetObservationsSurviveRestartWithoutStatusRequestsOrInventedResetBudgets()
    {
        var observed = _clock.GetUtcNow();
        var reset = observed.AddHours(1);
        var origin = new Origin((_, _) =>
        {
            var response = Json("""{"id":123,"name":"Series"}""");
            response.Headers.Add("X-RateLimit-Limit", "100");
            response.Headers.Add("X-RateLimit-Remaining", "37");
            response.Headers.Add("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            return response;
        });
        using (var client = Client(origin)) await Get(client);
        _clock.Advance(TimeSpan.FromMinutes(15));
        using (var restarted = Client(origin))
        {
            for (var index = 0; index < 3; index++)
            {
                var status = restarted.GetStatus().Single(value => value.Provider == "Tmdb");
                Assert.Equal(37L, status.QuotaRemaining);
                Assert.Equal(observed, status.QuotaObservedAtUtc);
                Assert.Null(status.PausedUntilUtc);
            }
            Assert.All(restarted.GetStatus().Where(value => value.Provider != "Tmdb"), status =>
            {
                Assert.Null(status.QuotaLimit);
                Assert.Null(status.QuotaRemaining);
                Assert.Null(status.QuotaResetUtc);
                Assert.Null(status.QuotaObservedAtUtc);
            });
            _clock.Advance(TimeSpan.FromMinutes(45));
            Assert.Null(restarted.GetStatus().Single(value => value.Provider == "Tmdb").QuotaRemaining);
        }
        using var afterReset = Client(origin);
        var stale = afterReset.GetStatus().Single(value => value.Provider == "Tmdb");
        Assert.Null(stale.QuotaRemaining);
        Assert.Equal(100L, stale.QuotaLimit);
        Assert.Equal(reset, stale.QuotaResetUtc);
        Assert.Equal(observed, stale.QuotaObservedAtUtc);
        Assert.Equal(1, origin.Calls);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("not-a-number", "1.5", "tomorrow")]
    [InlineData("-1", "-2", "-3")]
    [InlineData("9223372036854775808", "9223372036854775808", "253402300800")]
    [InlineData("10,20", "1,2", "1,2")]
    public async Task UnusableHeadersDoNotRefreshAnOlderObservationOrWeakenRateLimitPauses(string? limit, string? remaining, string? reset)
    {
        var calls = 0;
        var observed = _clock.GetUtcNow();
        var origin = new Origin((_, _) =>
        {
            if (++calls == 1)
            {
                var first = Json("""{"id":123,"name":"Series"}""");
                first.Headers.Add("X-RateLimit-Remaining", "7");
                return first;
            }
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (limit is not null) response.Headers.TryAddWithoutValidation("X-RateLimit-Limit", limit);
            if (remaining is not null) response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", remaining);
            if (reset is not null) response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset);
            return response;
        });
        using (var client = Client(origin))
        {
            await Get(client);
            _clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal("RateLimited", (await client.TestAsync("Tmdb", CancellationToken.None)).Code);
        }
        using var restarted = Client(origin);
        var status = restarted.GetStatus().Single(value => value.Provider == "Tmdb");
        Assert.Equal(7L, status.QuotaRemaining);
        Assert.Null(status.QuotaLimit);
        Assert.Null(status.QuotaResetUtc);
        Assert.Equal(observed, status.QuotaObservedAtUtc);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(5), status.PausedUntilUtc);
        Assert.Equal("RateLimited", (await restarted.TestAsync("Tmdb", CancellationToken.None)).Code);
        Assert.Equal("RateLimited", (await Assert.ThrowsAsync<MetadataProviderClient.ProviderFailure>(() => Fetch(restarted, force: true))).Code);
        Assert.Equal(2, origin.Calls);
    }

    [Fact]
    public async Task PartialObservationsCannotCombineOldBudgetsWithNewWindowsOrExposeImpossibleRemaining()
    {
        var calls = 0;
        var origin = new Origin((_, _) =>
        {
            var response = Json("""{"id":123,"name":"Series"}""");
            calls++;
            if (calls != 2)
            {
                response.Headers.Add("X-RateLimit-Limit", calls == 1 ? "100" : "10");
                response.Headers.Add("X-RateLimit-Remaining", "37");
            }
            else response.Headers.Add("X-RateLimit-Reset", _clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            return response;
        });
        using var client = Client(origin);
        await Get(client);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Get(client);
        var resetOnly = client.GetStatus().Single(value => value.Provider == "Tmdb");
        Assert.Null(resetOnly.QuotaLimit);
        Assert.Null(resetOnly.QuotaRemaining);
        Assert.Equal(_clock.GetUtcNow().AddHours(1), resetOnly.QuotaResetUtc);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Get(client);
        var inconsistent = client.GetStatus().Single(value => value.Provider == "Tmdb");
        Assert.Equal(10L, inconsistent.QuotaLimit);
        Assert.Null(inconsistent.QuotaRemaining);
        Assert.Null(inconsistent.QuotaResetUtc);
        Assert.Equal(_clock.GetUtcNow(), inconsistent.QuotaObservedAtUtc);
        Assert.Null(inconsistent.PausedUntilUtc);
    }

    [Fact]
    public async Task LegacyDeadlinesUpgradeWithoutInventingObservationsOrLosingCredentialIsolation()
    {
        var until = _clock.GetUtcNow().AddHours(1);
        var credential = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(new[] { _config.TmdbReadAccessToken, "" }))));
        var path = Path.Combine(Paths().DataDirectory, "metadata-quotas.json");
        Directory.CreateDirectory(Paths().DataDirectory);
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, DateTimeOffset>
        {
            ["Tmdb:" + credential] = until
        }));
        var origin = new Origin((_, _) =>
        {
            var response = Json("""{"images":{"secure_base_url":"https://image.tmdb.org/t/p/"}}""");
            response.Headers.Add("X-RateLimit-Remaining", "5");
            return response;
        });
        using (var client = Client(origin))
        {
            var legacy = client.GetStatus().Single(value => value.Provider == "Tmdb");
            Assert.Equal(until, legacy.PausedUntilUtc);
            Assert.Null(legacy.QuotaObservedAtUtc);
            Assert.Null(legacy.QuotaRemaining);
            Assert.Equal("RateLimited", (await client.TestAsync("Tmdb", CancellationToken.None)).Code);
            _config.TmdbReadAccessToken = "replacement-private-token";
            Assert.True((await client.TestAsync("Tmdb", CancellationToken.None)).Success);
        }
        _config.TmdbReadAccessToken = "synthetic-private-token";
        using var restarted = Client(origin);
        Assert.Equal(until, restarted.GetStatus().Single(value => value.Provider == "Tmdb").PausedUntilUtc);
        Assert.Equal("RateLimited", (await restarted.TestAsync("Tmdb", CancellationToken.None)).Code);
        _config.TmdbReadAccessToken = "replacement-private-token";
        Assert.Equal(5L, restarted.GetStatus().Single(value => value.Provider == "Tmdb").QuotaRemaining);
        Assert.Equal(1, origin.Calls);
        var persisted = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("synthetic-private-token", persisted);
        Assert.DoesNotContain("replacement-private-token", persisted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UntrustworthyPersistedObservationsCannotDiscardDurableDeadlines(bool futureTimestamp)
    {
        var until = _clock.GetUtcNow().AddHours(1);
        var origin = new Origin((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", until.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            return response;
        });
        using (var client = Client(origin))
            Assert.Equal("RateLimited", (await client.TestAsync("Tmdb", CancellationToken.None)).Code);
        var path = Path.Combine(Paths().DataDirectory, "metadata-quotas.json");
        var saved = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var entry = Assert.Single(saved).Value!;
        entry["Remaining"] = "malformed";
        entry["ObservedAtUtc"] = futureTimestamp ? until.ToString("O", CultureInfo.InvariantCulture) : "malformed";
        await File.WriteAllTextAsync(path, saved.ToJsonString());
        using var restarted = Client(origin);
        var status = restarted.GetStatus().Single(value => value.Provider == "Tmdb");
        Assert.Equal(until, status.PausedUntilUtc);
        Assert.Null(status.QuotaRemaining);
        Assert.Null(status.QuotaObservedAtUtc);
        Assert.Equal("RateLimited", (await restarted.TestAsync("Tmdb", CancellationToken.None)).Code);
        Assert.Equal(1, origin.Calls);
    }

    private MetadataProviderClient Client(ISafeHttpClient origin, MetadataResponseCache? cache = null)
        => new(new(() => _config), origin, null!, cache, _clock, new MetadataQuotaStore(Paths(), NullLogger<MetadataQuotaStore>.Instance, _clock));
    private async Task<TmdbRecord> Fetch(MetadataProviderClient client, bool force = false, string language = "en", int id = 123)
    {
        using var scope = client.BeginScope(_config, force, null);
        return await Get(client, id, language);
    }
    private Task<TmdbRecord> Get(MetadataProviderClient client, int id = 123, string language = "en") => client.GetAsync<TmdbRecord>("Tmdb", "https://api.themoviedb.org/3/tv/" + id + "?language=" + language,
        MetadataProviderClient.Bearer(_config.TmdbReadAccessToken), true, CancellationToken.None, value => value.Id == id && !string.IsNullOrWhiteSpace(value.Name));
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
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
    private sealed class AsyncOrigin(Func<CancellationToken, Task<HttpResponseMessage>> respond) : ISafeHttpClient
    {
        public Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
            => respond(cancellationToken);
    }
    public class PathsProxy : DispatchProxy
    {
        public string Directory { get; set; } = string.Empty;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name == "get_DataPath" ? Directory : throw new NotSupportedException();
    }
}
