using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Protocol;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Configuration;

public sealed class SiphonDiagnosticsControllerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "siphon-diagnostics-" + Guid.NewGuid().ToString("N"));
    private const string InstallationId = "63962eafd14f4d8e93f57120e5f3e645";
    private const string SensitiveValue = "fixture-sensitive-value";

    [Fact]
    public void ExportAndRestartNeverExposeArbitraryManifestOrErrorValues()
    {
        var paths = Paths();
        using (var diagnostics = Diagnostics(paths))
        {
            diagnostics.RecordSync(InstallationId, false, "http://private.invalid:4321/" + SensitiveValue);
            diagnostics.RecordManifestCheck(InstallationId, true, "Ok", 12, new StremioManifest
            {
                Id = SensitiveValue,
                Name = SensitiveValue,
                Description = SensitiveValue,
                Types = ["movie", "tv", SensitiveValue],
                IdPrefixes = [SensitiveValue],
                Resources = [new("stream"), new(SensitiveValue), new("meta", ["series", SensitiveValue], [SensitiveValue], true)]
            });
            diagnostics.RecordSync(SensitiveValue, false, SensitiveValue);
        }

        var persisted = File.ReadAllText(Path.Combine(paths.DataDirectory, "diagnostics.json"));
        Assert.DoesNotContain(SensitiveValue, persisted);
        Assert.DoesNotContain("private.invalid", persisted);
        Assert.DoesNotContain("http://", persisted);
        using var restarted = Diagnostics(paths);
        var record = Assert.Single(restarted.GetSnapshot().Addons);
        Assert.Equal("SyncFailed", record.LastErrorCode);
        Assert.NotNull(record.LastFailureUtc);
        Assert.Null(record.LastSuccessUtc);
        Assert.True(record.LastManifestCheck!.Success);
        Assert.Equal(["movie", "tv"], record.LastManifestCheck.Capabilities!.Types);
        Assert.All(record.LastManifestCheck.Capabilities.Resources, resource => Assert.True(resource.IdPrefixesRestricted));

        var config = new PluginConfiguration
        {
            PublicBaseUrl = "http://private.invalid:4321/" + SensitiveValue,
            TmdbReadAccessToken = SensitiveValue,
            Addons = [new() { Id = InstallationId, DisplayName = SensitiveValue, ManifestUrl = "https://example.org/" + SensitiveValue + "/manifest.json", Enabled = false }]
        };
        var accessor = new ConfigurationAccessor(() => config);
        var origin = new ManifestOrigin();
        using var client = new StremioClient(origin, accessor);
        using var connection = new SelfConnectionProbe(accessor, Host());
        var controller = new SiphonDiagnosticsController(accessor, client, restarted, connection, Host());
        var export = JsonSerializer.Serialize(controller.GetDiagnostics().Value);
        Assert.DoesNotContain(SensitiveValue, export);
        Assert.DoesNotContain("private.invalid", export);
        Assert.DoesNotContain("http://", export);
        Assert.Equal(0, origin.RequestCount);
    }

    [Fact]
    public void CorruptDiagnosticsDoNotTouchOwnershipAndCanRecover()
    {
        var paths = Paths();
        Directory.CreateDirectory(paths.DataDirectory);
        var ownership = Path.Combine(paths.DataDirectory, "state.json");
        File.WriteAllText(ownership, "ownership-fixture");
        File.WriteAllText(Path.Combine(paths.DataDirectory, "diagnostics.json"), "{broken");
        using (var diagnostics = Diagnostics(paths))
        {
            Assert.Equal("DiagnosticsCorrupt", diagnostics.GetSnapshot().StorageCode);
            diagnostics.RecordSync(InstallationId, true);
        }

        Assert.Equal("ownership-fixture", File.ReadAllText(ownership));
        using var restarted = Diagnostics(paths);
        Assert.Null(restarted.GetSnapshot().StorageCode);
        Assert.NotNull(Assert.Single(restarted.GetSnapshot().Addons).LastSuccessUtc);
    }

    [Fact]
    public async Task SavedDisabledAddonCanBeCheckedWithoutClaimingSyncSuccess()
    {
        var config = new PluginConfiguration
        {
            Addons = [new() { Id = InstallationId, Enabled = false, ManifestUrl = "https://example.org/manifest.json" }]
        };
        var accessor = new ConfigurationAccessor(() => config);
        using var diagnostics = Diagnostics(Paths());
        using var client = new StremioClient(new ManifestOrigin(), accessor);
        using var connection = new SelfConnectionProbe(accessor, Host());
        var controller = new SiphonDiagnosticsController(accessor, client, diagnostics, connection, Host());
        var result = await controller.TestAddon(Guid.Parse(InstallationId), CancellationToken.None);
        Assert.True(result.Value!.Success);
        var record = Assert.Single(controller.GetDiagnostics().Value!.Addons);
        Assert.False(record.Enabled);
        Assert.Null(record.LastSuccessUtc);
        Assert.Null(record.LastFailureUtc);
        Assert.True(record.LastManifestCheck!.Success);
        var missing = await controller.TestAddon(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(missing.Result);
    }

    [Fact]
    public async Task ManifestFailureCannotExportExceptionSecretsOrEraseSyncHistory()
    {
        var config = new PluginConfiguration { Addons = [new() { Id = InstallationId, ManifestUrl = "https://example.org/manifest.json" }] };
        var accessor = new ConfigurationAccessor(() => config);
        using var diagnostics = Diagnostics(Paths());
        diagnostics.RecordSync(InstallationId, true);
        using var client = new StremioClient(new ManifestOrigin(fail: true), accessor);
        using var connection = new SelfConnectionProbe(accessor, Host());
        var controller = new SiphonDiagnosticsController(accessor, client, diagnostics, connection, Host());
        var result = await controller.TestAddon(Guid.Parse(InstallationId), CancellationToken.None);
        Assert.False(result.Value!.Success);
        Assert.Equal("ManifestUnavailable", result.Value.Code);
        Assert.DoesNotContain(SensitiveValue, JsonSerializer.Serialize(result.Value));
        var record = Assert.Single(controller.GetDiagnostics().Value!.Addons);
        Assert.NotNull(record.LastSuccessUtc);
        Assert.Null(record.LastFailureUtc);
        Assert.False(record.LastManifestCheck!.Success);
    }

    [Theory]
    [InlineData("fixture-server", true, "Ok")]
    [InlineData("different-server", false, "WrongServer")]
    public async Task SavedLoopbackSelfProbeVerifiesNativeIdentity(string returnedId, bool success, string code)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = ServeOnce(listener, "200 OK", "", JsonSerializer.Serialize(new { Id = returnedId, ServerName = SensitiveValue }), deadline.Token);
        var config = new PluginConfiguration { PublicBaseUrl = $"http://127.0.0.1:{port}/jellyfin", AllowedPrivateHosts = [] };
        var accessor = new ConfigurationAccessor(() => config);
        Assert.False(new SsrfPolicy(accessor).IsAllowed("127.0.0.1", IPAddress.Loopback));
        using var probe = new SelfConnectionProbe(accessor, Host());
        var result = await probe.ProbeAsync(deadline.Token);
        Assert.Equal(success, result.Success);
        Assert.Equal(code, result.Code);
        Assert.DoesNotContain(SensitiveValue, JsonSerializer.Serialize(result));
        var request = await served;
        Assert.StartsWith("GET /jellyfin/System/Info/Public HTTP/1.1", request);
        Assert.DoesNotContain("Authorization:", request, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cookie:", request, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelfProbeNeverFollowsRedirects()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        using var target = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        target.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var served = ServeOnce(origin, "302 Found", $"Location: http://127.0.0.1:{targetPort}/{SensitiveValue}\r\n", "", deadline.Token);
        using var probe = new SelfConnectionProbe(new ConfigurationAccessor(() => new PluginConfiguration { PublicBaseUrl = $"http://127.0.0.1:{originPort}" }), Host());
        var result = await probe.ProbeAsync(deadline.Token);
        Assert.Equal("RedirectBlocked", result.Code);
        await served;
        Assert.False(target.Pending());
    }

    [Fact]
    public async Task OversizedSelfProbeResponseIsRejectedWithoutBodyExposure()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = ServeOnce(listener, "200 OK", "", "", deadline.Token, contentLength: 65537);
        using var probe = new SelfConnectionProbe(new ConfigurationAccessor(() => new PluginConfiguration { PublicBaseUrl = $"http://127.0.0.1:{port}" }), Host());
        var result = await probe.ProbeAsync(deadline.Token);
        Assert.False(result.Success);
        Assert.Equal("ResponseTooLarge", result.Code);
        await served;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManifestDeadlineAndCallerCancellationRemainDistinct(bool cancelCaller)
    {
        var config = new PluginConfiguration
        {
            AddonTimeoutSeconds = 1,
            Addons = [new() { Id = InstallationId, ManifestUrl = "https://example.org/manifest.json" }]
        };
        var accessor = new ConfigurationAccessor(() => config);
        using var diagnostics = Diagnostics(Paths());
        var origin = new WaitingOrigin();
        using var client = new StremioClient(origin, accessor);
        using var connection = new SelfConnectionProbe(accessor, Host());
        using var caller = new CancellationTokenSource();
        var controller = new SiphonDiagnosticsController(accessor, client, diagnostics, connection, Host());
        var request = controller.TestAddon(Guid.Parse(InstallationId), caller.Token);
        await origin.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelCaller)
        {
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        }
        else
        {
            var result = await request.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.Value!.Success);
            Assert.Equal("Timeout", result.Value.Code);
        }

        var observation = Assert.Single(diagnostics.GetSnapshot().Addons);
        Assert.Equal(cancelCaller ? "Cancelled" : "Timeout", observation.LastManifestCheck!.Code);
        Assert.Null(observation.LastFailureUtc);
    }

    [Fact]
    public async Task TransportTimeoutIsReportedBeforeTheProbeDeadline()
    {
        var config = new PluginConfiguration
        {
            AddonTimeoutSeconds = 30,
            Addons = [new() { Id = InstallationId, ManifestUrl = "https://example.org/manifest.json" }]
        };
        var accessor = new ConfigurationAccessor(() => config);
        using var diagnostics = Diagnostics(Paths());
        using var client = new StremioClient(new ManifestOrigin(timeout: true), accessor);
        using var connection = new SelfConnectionProbe(accessor, Host());
        var controller = new SiphonDiagnosticsController(accessor, client, diagnostics, connection, Host());

        var result = await controller.TestAddon(Guid.Parse(InstallationId), CancellationToken.None);

        Assert.False(result.Value!.Success);
        Assert.Equal("Timeout", result.Value.Code);
        Assert.Equal("Timeout", Assert.Single(diagnostics.GetSnapshot().Addons).LastManifestCheck!.Code);
    }

    private sealed class WaitingOrigin : ISafeHttpClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellationToken);
        }
    }

    private static async Task<string> ServeOnce(TcpListener listener, string status, string headers, string body, CancellationToken cancellationToken, int? contentLength = null)
    {
        using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = connection.GetStream();
        var received = new StringBuilder();
        var buffer = new byte[2048];
        while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) throw new IOException("Request ended before headers.");
            received.Append(Encoding.ASCII.GetString(buffer, 0, count));
        }

        var bytes = Encoding.UTF8.GetBytes(body);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{headers}Content-Type: application/json\r\nContent-Length: {contentLength ?? bytes.Length}\r\nConnection: close\r\n\r\n"), cancellationToken);
        if (bytes.Length != 0) await stream.WriteAsync(bytes, cancellationToken);
        return received.ToString();
    }

    [Fact]
    public void ParallelProviderObservationsAndFinalSummarySurviveRestartWithoutPrivateValues()
    {
        var paths = Paths();
        using (var diagnostics = Diagnostics(paths))
        {
            diagnostics.BeginRun("FullRefresh");
            diagnostics.ReportStage("Metadata", "Titles", 4, 12);
            diagnostics.SetPrioritySeries(2);
            Parallel.For(0, 200, index =>
            {
                diagnostics.RecordProviderRequest("Tmdb", cached: index % 2 == 0);
                if (index % 4 == 0) diagnostics.RecordProviderError("Tmdb", "Timeout");
            });
            diagnostics.RecordProviderError("Tmdb", SensitiveValue);
            diagnostics.RecordProviderRequest(SensitiveValue, false);
            var current = diagnostics.GetRunStatus().CurrentRun!;
            Assert.Equal((4, 12), (current.Completed, current.Total));
            Assert.Equal(50, Assert.Single(current.Providers).Errors["Timeout"]);
            diagnostics.SetSummary(3, 4, 5, 1, 2, 0);
            diagnostics.FinishRun("Completed");
            // A late observation or an explicit credential test must not mutate the finished run.
            diagnostics.RecordProviderRequest("Tmdb", false);
            Assert.Null(diagnostics.GetRunStatus().CurrentRun);
        }

        Assert.DoesNotContain(SensitiveValue, File.ReadAllText(Path.Combine(paths.DataDirectory, "diagnostics.json")));
        using var restarted = Diagnostics(paths);
        var status = restarted.GetRunStatus();
        Assert.Null(status.CurrentRun);
        var finished = status.LastRun!;
        Assert.Equal("Completed", finished.State);
        Assert.Equal("FullRefresh", finished.Kind);
        Assert.NotNull(finished.FinishedAtUtc);
        Assert.Equal((3, 4, 5, 1, 2), (finished.Added, finished.Updated, finished.Unchanged, finished.Removed, finished.Preserved));
        var provider = Assert.Single(finished.Providers);
        Assert.Equal((100, 100), (provider.Requests, provider.CacheHits));
        Assert.Equal(1, provider.Errors["ProviderError"]);
        Assert.Equal(2, finished.PrioritySeries);
    }

    [Fact]
    public void SubscriptionFailuresSurviveRestartWithoutLeakingIntoLaterRuns()
    {
        var paths = Paths();
        var first = SyncTarget.CatalogIdentity(InstallationId, "first");
        var second = SyncTarget.CatalogIdentity(InstallationId, "second");
        using (var diagnostics = Diagnostics(paths))
        {
            diagnostics.BeginRun("Catalogs");
            diagnostics.RecordSubscriptionFailure(first, "RequestFailed");
            diagnostics.RecordSubscriptionFailure(second, "IncompleteCatalog");
            diagnostics.RecordSubscriptionFailure(SensitiveValue, "RequestFailed");
            diagnostics.RecordSubscriptionFailure("https://private.invalid/" + SensitiveValue, SensitiveValue);
            diagnostics.SetSummary(0, 0, 0, 0, 2, 2);
            diagnostics.FinishRun("Failed");
        }

        using var restarted = Diagnostics(paths);
        var failed = restarted.GetRunStatus().LastRun!;
        Assert.Equal("Failed", failed.State);
        Assert.Equal(2, failed.FailedSubscriptions);
        Assert.Equal(2, failed.SubscriptionFailures!.Count);
        Assert.Equal("RequestFailed", failed.SubscriptionFailures[first]);
        Assert.Equal("IncompleteCatalog", failed.SubscriptionFailures[second]);
        restarted.BeginRun("Catalogs");
        Assert.Empty(restarted.GetRunStatus().CurrentRun!.SubscriptionFailures!);
        restarted.FinishRun("Completed");
        Assert.Empty(restarted.GetRunStatus().LastRun!.SubscriptionFailures!);
        Assert.Equal(failed.SubscriptionFailures, restarted.GetHistory().Single(run => run.Id == failed.Id).SubscriptionFailures);
        Assert.DoesNotContain(SensitiveValue, File.ReadAllText(Path.Combine(paths.DataDirectory, "diagnostics.json")));
    }

    [Fact]
    public void PersistedUnsafeSubscriptionKeysCannotCollideOrEraseSafeRunHistory()
    {
        var paths = Paths();
        Directory.CreateDirectory(paths.DataDirectory);
        var first = SyncTarget.CatalogIdentity(InstallationId, "first");
        var second = SyncTarget.CatalogIdentity(InstallationId, "second");
        var run = new SyncRunSnapshot("Catalogs", "Failed", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow,
            "Finalizing", "Items", 2, 2, 0, 0, 0, 0, 0, 2, 2, [], SubscriptionFailures: new Dictionary<string, string>
            {
                [first] = "ManifestUnavailable",
                [second] = "Bearer " + SensitiveValue,
                [SensitiveValue] = "RequestFailed",
                ["https://private.invalid/" + SensitiveValue] = "RequestFailed",
                ["https://other.invalid/?token=" + SensitiveValue] = "IncompleteCatalog"
            });
        var path = Path.Combine(paths.DataDirectory, "diagnostics.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { Version = 3, Addons = Array.Empty<InstallationDiagnostic>(), LastRun = run }));

        using var diagnostics = Diagnostics(paths);
        Assert.Null(diagnostics.GetSnapshot().StorageCode);
        var restored = Assert.Single(diagnostics.GetHistory());
        Assert.Equal("Failed", restored.State);
        Assert.Equal(2, restored.SubscriptionFailures!.Count);
        Assert.Equal("ManifestUnavailable", restored.SubscriptionFailures[first]);
        Assert.Equal("SyncFailed", restored.SubscriptionFailures[second]);
        Assert.DoesNotContain(SensitiveValue, JsonSerializer.Serialize(diagnostics.GetSnapshot()));
        Assert.DoesNotContain(SensitiveValue, File.ReadAllText(path));
        Assert.DoesNotContain("https://", File.ReadAllText(path));
    }

    [Fact]
    public void RestartReportsInterruptedRunInsteadOfStaleRunningOrSuccessfulTask()
    {
        var paths = Paths();
        using (var diagnostics = Diagnostics(paths))
        {
            diagnostics.BeginRun("Catalogs");
            diagnostics.FinishRun("Completed");
            diagnostics.BeginRun("FollowedSeries");
            diagnostics.ReportStage("Credits", "Items", 12, 40);
        }

        using var restarted = Diagnostics(paths);
        var status = restarted.GetRunStatus();
        Assert.Null(status.CurrentRun);
        Assert.Equal("Interrupted", status.LastRun!.State);
        Assert.Equal("FollowedSeries", status.LastRun.Kind);
        Assert.Equal((12, 40), (status.LastRun.Completed, status.LastRun.Total));
        Assert.Null(status.LastRun.FinishedAtUtc);
        Assert.Equal(new[] { "Interrupted", "Completed" }, restarted.GetHistory().Select(run => run.State));
    }

    [Fact]
    public void TwentyRunsRotateDurablyAndDecisionsArePagedWithoutCredentialBearingValues()
    {
        var paths = Paths();
        string removedId = "";
        string latestId = "";
        using (var diagnostics = Diagnostics(paths))
        {
            for (var index = 0; index < 23; index++)
            {
                diagnostics.BeginRun("Targeted", "Series", "https://private.invalid/" + SensitiveValue);
                if (index == 0) removedId = diagnostics.GetRunStatus().CurrentRun!.Id;
                diagnostics.RecordDecision("series:tt1234567", "Example series", "Pending", "CacheHit");
                diagnostics.RecordDecision("series:tt1234567", "Example series", "Unchanged", "NoChanges");
                diagnostics.RecordDecision("https://private.invalid/" + SensitiveValue, "https://private.invalid/" + SensitiveValue, "Preserved", "MetadataLocked");
                diagnostics.RecordDecision("movie:tt1234568", "New movie", "Added", "Published");
                diagnostics.FinishRun(index == 22 ? "Cancelled" : "Completed");
                latestId = diagnostics.GetRunStatus().LastRun!.Id;
            }
        }
        using var restarted = Diagnostics(paths);
        var history = restarted.GetHistory();
        Assert.Equal(20, history.Count);
        Assert.Equal(latestId, history[0].Id);
        Assert.Equal("Cancelled", history[0].State);
        Assert.Null(restarted.GetHistoryDetails(removedId, 0, 100));
        var first = restarted.GetHistoryDetails(latestId, 0, 1)!;
        Assert.Equal(3, first.TotalCount);
        Assert.Equal("Unchanged", Assert.Single(first.Items).Action);
        Assert.Contains("CacheHit", first.Items[0].Reasons);
        var remainder = restarted.GetHistoryDetails(latestId, 1, 2)!;
        Assert.Equal(new[] { "Preserved", "Added" }, remainder.Items.Select(item => item.Action));
        Assert.Empty(restarted.GetHistoryDetails(latestId, 3, 2)!.Items);
        Assert.DoesNotContain(SensitiveValue, JsonSerializer.Serialize(history));
        Assert.DoesNotContain(SensitiveValue, JsonSerializer.Serialize(remainder));
        foreach (var path in Directory.EnumerateFiles(paths.DataDirectory, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(SensitiveValue, File.ReadAllText(path));
    }

    [Fact]
    public async Task ParallelTitleScopesAttributeOnlyActualProviderObservations()
    {
        using var diagnostics = Diagnostics(Paths());
        diagnostics.BeginRun("Catalogs");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Paused()
        {
            using var scope = diagnostics.BeginTitle("movie:tt1234567", "Paused movie");
            ready.SetResult();
            await release.Task;
            diagnostics.RecordProviderError("Tmdb", "ProviderPaused");
        }
        var paused = Paused();
        await ready.Task;
        using (diagnostics.BeginTitle("movie:tt1234568", "Cached movie"))
            diagnostics.RecordProviderRequest("Tvdb", cached: true);
        diagnostics.RecordDecision("movie:tt1234568", "Cached movie", "Unchanged", "NoChanges");
        release.SetResult();
        await paused;
        diagnostics.FinishRun("Completed");
        var page = diagnostics.GetHistoryDetails(diagnostics.GetRunStatus().LastRun!.Id, 0, 10)!;
        var cached = page.Items.Single(item => item.ContentKey == "movie:tt1234568");
        Assert.DoesNotContain("ProviderPaused", cached.Reasons);
        Assert.Equal("Unchanged", cached.Action);
        Assert.Equal("ProviderPaused", page.Items.Single(item => item.ContentKey == "movie:tt1234567").Action);
    }

    [Fact]
    public void InterruptedDetailsSurviveRepeatedRestartAndCorruptionDoesNotEraseSummaries()
    {
        var paths = Paths();
        string id;
        using (var diagnostics = Diagnostics(paths))
        {
            diagnostics.BeginRun("Catalogs");
            diagnostics.RecordDecision("movie:tt1234567", "Unfinished movie", "Pending", "Scheduled");
            id = diagnostics.GetRunStatus().CurrentRun!.Id;
        }
        using (var restarted = Diagnostics(paths))
        {
            Assert.Equal("Interrupted", Assert.Single(restarted.GetHistory()).State);
            var decision = Assert.Single(restarted.GetHistoryDetails(id, 0, 10)!.Items);
            Assert.Equal("Error", decision.Action);
            Assert.Contains("Interrupted", decision.Reasons);
        }
        File.WriteAllText(Path.Combine(paths.DataDirectory, "sync-history", id + ".jsonl"), "{broken");
        using var again = Diagnostics(paths);
        Assert.Equal(id, Assert.Single(again.GetHistory()).Id);
        Assert.Equal("HistoryDetailsUnavailable", again.GetHistoryDetails(id, 0, 10)!.StorageCode);
        Assert.Equal("Interrupted", again.GetRunStatus().LastRun!.State);
    }

    [Fact]
    public void VersionTwoMigrationKeepsLastRunAlongsideInterruptedCurrentRun()
    {
        var paths = Paths();
        Directory.CreateDirectory(paths.DataDirectory);
        var completed = new SyncRunSnapshot("Catalogs", "Completed", DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1),
            "Finalizing", "Items", 5, 5, 0, 2, 3, 0, 0, 0, 0, []);
        var current = completed with { Kind = "FullRefresh", State = "Running", StartedAtUtc = DateTimeOffset.UtcNow, FinishedAtUtc = null };
        File.WriteAllText(Path.Combine(paths.DataDirectory, "diagnostics.json"), JsonSerializer.Serialize(new
        {
            Version = 2,
            Addons = Array.Empty<InstallationDiagnostic>(),
            LastRun = completed,
            CurrentRun = current
        }));
        string[] ids;
        using (var migrated = Diagnostics(paths))
        {
            var history = migrated.GetHistory();
            Assert.Equal(new[] { "Interrupted", "Completed" }, history.Select(run => run.State));
            Assert.Equal((2, 3), (history[1].Added, history[1].Updated));
            ids = history.Select(run => run.Id).ToArray();
        }
        using var restarted = Diagnostics(paths);
        Assert.Equal(ids, restarted.GetHistory().Select(run => run.Id));
    }

    private static SyncDiagnostics Diagnostics(SiphonPaths paths) => new(paths, NullLogger<SyncDiagnostics>.Instance);
    private SiphonPaths Paths()
    {
        var paths = DispatchProxy.Create<IApplicationPaths, PathsProxy>();
        ((PathsProxy)(object)paths).DataPath = _directory;
        return new SiphonPaths(paths);
    }

    private static IServerApplicationHost Host() => DispatchProxy.Create<IServerApplicationHost, HostProxy>();
    public class PathsProxy : DispatchProxy
    {
        public string DataPath { get; set; } = "";
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name == "get_DataPath" ? DataPath : throw new NotSupportedException();
    }

    public class HostProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_SystemId" => "fixture-server",
            "get_ApplicationVersion" => new Version(12, 1, 0),
            _ => throw new NotSupportedException()
        };
    }

    private sealed class ManifestOrigin(bool fail = false, bool timeout = false) : ISafeHttpClient
    {
        public int RequestCount { get; private set; }
        public Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (timeout) throw new OperationCanceledException();
            if (fail) throw new HttpRequestException(SensitiveValue);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"fixture\",\"name\":\"Fixture\",\"types\":[\"tv\"],\"resources\":[\"stream\"],\"catalogs\":[]}")
            });
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
