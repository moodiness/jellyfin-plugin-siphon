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
        var origin = new Origin((uri, _) => Json(uri.AbsolutePath switch
        {
            "/3/tv/123" => """{"id":123,"name":"Series","overview":"Series plot"}""",
            "/3/configuration" => """{"images":{"secure_base_url":"https://image.tmdb.org/t/p/"}}""",
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
        Assert.Equal(6, origin.Calls);
        await restarted.EnrichAsync(enriched, CancellationToken.None, forceRefresh: true);
        Assert.Equal(9, origin.Calls);
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

    [Theory]
    [InlineData("force")]
    [InlineData("language")]
    [InlineData("episodes")]
    [InlineData("credentials")]
    [InlineData("fields")]
    [InlineData("mode")]
    [InlineData("providers")]
    [InlineData("identifier")]
    public async Task ChangedRequestContextDoesNotReuseOldResponse(string change)
    {
        var origin = new Origin((uri, _) => Json(uri.AbsolutePath.EndsWith("/124", StringComparison.Ordinal)
            ? """{"id":124,"name":"Different identity"}""" : """{"id":123,"name":"Series"}"""));
        using var cache = new MetadataResponseCache(Paths());
        using var client = Client(origin, cache);
        await Fetch(client);
        await Fetch(client);
        Assert.Equal(1, origin.Calls);
        if (change == "credentials") _config.TmdbReadAccessToken = "replacement-private-token";
        if (change == "fields") _config.MetadataRefreshFields = ["Name"];
        if (change == "mode") _config.MetadataUpdateMode = "RefreshSelected";
        if (change == "providers") _config.EnableFanartMetadata = true;
        await Fetch(client, change == "force", change == "language" ? "fr" : "en", change == "episodes" ? "1:1;1:2" : "1:1", change == "identifier" ? 124 : 123);
        Assert.Equal(2, origin.Calls);
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
        using (client.BeginScope(_config, "en", "series", "1:1", false, diagnostics))
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

    private MetadataProviderClient Client(Origin origin, MetadataResponseCache? cache = null) => new(new(() => _config), origin, null!, cache, _clock);
    private async Task<TmdbRecord> Fetch(MetadataProviderClient client, bool force = false, string language = "en", string episodes = "1:1", int id = 123)
    {
        using var scope = client.BeginScope(_config, language, "series", episodes, force, null);
        return await Get(client, id);
    }
    private Task<TmdbRecord> Get(MetadataProviderClient client, int id = 123) => client.GetAsync<TmdbRecord>("Tmdb", "https://api.themoviedb.org/3/tv/" + id,
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
    public class PathsProxy : DispatchProxy
    {
        public string Directory { get; set; } = string.Empty;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name == "get_DataPath" ? Directory : throw new NotSupportedException();
    }
}
