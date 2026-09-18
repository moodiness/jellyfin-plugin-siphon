using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Siphon.Collections;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Metadata;
using Jellyfin.Plugin.Siphon.Playback;
using Jellyfin.Plugin.Siphon.Protocol;
using Jellyfin.Plugin.Siphon.Tests.Playback;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Identity;

[Collection("Native publication")]
public sealed class CatalogSyncTests
{
    [Fact]
    public async Task ShortCatalogPagesContinueAtTheirActualOffset()
    {
        var snapshot = await SynchronizeToStateAsync("movie", [], uri => uri.AbsolutePath switch
        {
            "/manifest.json" => """{"id":"catalog","catalogs":[{"type":"movie","id":"all","extra":[{"name":"skip"}]}]}""",
            "/catalog/movie/all/skip=0.json" => """{"metas":[{"id":"tt1234567","type":"movie","name":"First"},{"id":"tt1234568","type":"movie","name":"Second"}]}""",
            "/catalog/movie/all/skip=2.json" => """{"metas":[{"id":"tt1234569","type":"movie","name":"Third"}]}""",
            "/catalog/movie/all/skip=3.json" => """{"metas":[]}""",
            _ => throw new InvalidOperationException("Unexpected catalog offset.")
        });
        Assert.Equal(new[] { "First", "Second", "Third" }, snapshot.Select(item => item.Name));
    }

    [Fact]
    public async Task RepeatedPaginationRetainsItemsOutsideThePartialSnapshot()
    {
        var previous = Item("movie:tt1234567", "movie", "tt1234567");
        var snapshot = await SynchronizeToStateAsync("movie", [previous], uri => uri.AbsolutePath switch
        {
            "/manifest.json" => """{"id":"catalog","catalogs":[{"type":"movie","id":"all","extra":[{"name":"skip"}]}]}""",
            "/catalog/movie/all/skip=0.json" or "/catalog/movie/all/skip=1.json" => """{"metas":[{"id":"tt1234568","type":"movie","name":"Repeated"}]}""",
            _ => throw new InvalidOperationException("Unexpected catalog offset.")
        });
        Assert.Equal(new[] { previous.Key, "movie:tt1234568" }, snapshot.Select(item => item.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalProviderMetadataCannotReplaceExistingNativeIdentity(bool enriched)
    {
        var oldKey = enriched ? ContentIdentity.Key("movie", "opaque", "installation", new Dictionary<string, string>()) : "movie:tt1234567";
        var previous = Item(oldKey, "movie", "opaque") with
        {
            ProviderIds = enriched ? [] : new() { ["Imdb"] = "tt1234567" }
        };
        var snapshot = await SynchronizeToStateAsync("movie", [previous], uri => uri.AbsolutePath switch
        {
            "/manifest.json" => """{"id":"catalog","catalogs":[{"type":"movie","id":"all"}]}""",
            "/catalog/movie/all.json" => enriched
                ? """{"metas":[{"id":"opaque","type":"movie","name":"Updated","imdb_id":"tt1234567"}]}"""
                : """{"metas":[{"id":"opaque","type":"movie","name":"Updated"}]}""",
            _ => throw new InvalidOperationException("Unexpected resource.")
        });
        var retained = Assert.Single(snapshot);
        Assert.Equal(oldKey, retained.Key);
        Assert.Equal(oldKey, retained.ContentKey);
        Assert.Equal(previous.Path, retained.Path);
        Assert.Equal("Updated", retained.Name);
        Assert.Equal("tt1234567", retained.ProviderIds["Imdb"]);
    }

    [Fact]
    public async Task MissingAnimeEpisodeMetadataRetainsTheSeriesInsteadOfCreatingAMovie()
    {
        var previous = Item("series:mal:42:1:1", "series", "mal:42") with
        {
            ContentKey = "series:mal:42",
            Season = 1,
            Episode = 1,
            VideoId = "exact:episode:1"
        };
        var snapshot = await SynchronizeToStateAsync("anime", [previous], uri => uri.AbsolutePath switch
        {
            "/manifest.json" => """{"id":"catalog","types":["anime"],"resources":["meta"],"catalogs":[{"type":"anime","id":"all"}]}""",
            "/catalog/anime/all.json" => """{"metas":[{"id":"mal:42","type":"anime","name":"Show"}]}""",
            "/meta/anime/mal%3A42.json" => """{"meta":null}""",
            _ => throw new InvalidOperationException("Unexpected resource.")
        });
        var retained = Assert.Single(snapshot);
        Assert.Equal(previous.Key, retained.Key);
        Assert.Equal("series", retained.Type);
        Assert.Equal(previous.VideoId, retained.VideoId);
        Assert.Equal(previous.Owners, retained.Owners);
    }

    [Fact]
    public async Task CompleteAbsenceIsTrackedWithoutEnablingRemoval()
    {
        var previous = Item("movie:tt1234567", "movie", "tt1234567");
        var snapshot = await SynchronizeToStateAsync("movie", [previous], EmptyCatalog, config => config.RemoveMissingItems = false);
        var missing = Assert.Single(snapshot);
        Assert.NotNull(missing.MissingSinceUtc);
        Assert.Equal(previous.Owners, missing.MissingOwners);
        Assert.Equal(previous.Path, missing.Path);
    }

    [Fact]
    public async Task ReappearanceClearsAbsenceWithoutReplacingIdentity()
    {
        var previous = Item("movie:tt1234567", "movie", "tt1234567") with
        {
            MissingSinceUtc = DateTimeOffset.UtcNow.AddDays(-30),
            MissingOwners = ["installation:subscription"]
        };
        var snapshot = await SynchronizeToStateAsync("movie", [previous], uri => uri.AbsolutePath == "/manifest.json"
            ? """{"id":"catalog","catalogs":[{"type":"movie","id":"all"}]}"""
            : """{"metas":[{"id":"tt1234567","type":"movie","name":"Returned"}]}""");
        var returned = Assert.Single(snapshot);
        Assert.Null(returned.MissingSinceUtc);
        Assert.Empty(returned.MissingOwners);
        Assert.Equal(previous.Key, returned.Key);
        Assert.Equal(previous.Path, returned.Path);
    }

    [Fact]
    public async Task FailedSnapshotCannotDeleteAnAlreadyExpiredMissingItem()
    {
        var previous = Item("movie:tt1234567", "movie", "tt1234567") with
        {
            MissingSinceUtc = DateTimeOffset.UtcNow.AddDays(-30),
            MissingOwners = ["installation:subscription"]
        };
        var snapshot = await SynchronizeToStateAsync("movie", [previous], uri => uri.AbsolutePath == "/manifest.json"
            ? """{"id":"catalog","catalogs":[{"type":"movie","id":"all"}]}"""
            : throw new HttpRequestException());
        var retained = Assert.Single(snapshot);
        Assert.Null(retained.MissingSinceUtc);
        Assert.Empty(retained.MissingOwners);
    }

    [Fact]
    public async Task IncompleteSnapshotRevokesPreviousAbsenceConfirmation()
    {
        var previous = Item("movie:tt1234567", "movie", "tt1234567") with
        {
            MissingSinceUtc = DateTimeOffset.UtcNow.AddDays(-30),
            MissingOwners = ["installation:subscription"]
        };
        var snapshot = await SynchronizeToStateAsync("movie", [previous], uri => uri.AbsolutePath == "/manifest.json"
            ? """{"id":"catalog","catalogs":[{"type":"movie","id":"all"}]}"""
            : """{"metas":[{"type":"movie","name":"Missing resource ID"}]}""");
        Assert.Null(Assert.Single(snapshot).MissingSinceUtc);
    }

    [Fact]
    public async Task ZeroDayGraceIsExplicitAndSevenDaysRetainsNewAbsence()
    {
        var previous = Item("movie:tt1234567", "movie", "tt1234567");
        Assert.Empty(await SynchronizeToStateAsync("movie", [previous], EmptyCatalog));
        var retained = await SynchronizeToStateAsync("movie", [previous], EmptyCatalog, config => config.MissingItemRetentionDays = 7);
        Assert.NotNull(Assert.Single(retained).MissingSinceUtc);
    }

    [Fact]
    public async Task MissingOneOwnerCannotRemoveSharedOrDeselectedMedia()
    {
        var previous = Item("movie:tt1234567", "movie", "tt1234567") with
        {
            Owners = ["installation:subscription", "another:subscription"]
        };
        var snapshot = await SynchronizeToStateAsync("movie", [previous], EmptyCatalog);
        var retained = Assert.Single(snapshot);
        Assert.Null(retained.MissingSinceUtc);
        Assert.Equal(previous.Owners, retained.Owners);
        Assert.Equal(new[] { "installation:subscription" }, retained.MissingOwners);
    }

    [Fact]
    public async Task ReturningSharedOwnerOverridesAnotherOwnersCompleteAbsence()
    {
        var previous = Item("movie:tt1234567", "movie", "tt1234567") with
        {
            Owners = ["installation:subscription", "installation:second"],
            MissingOwners = ["installation:subscription", "installation:second"],
            MissingSinceUtc = DateTimeOffset.UtcNow.AddDays(-30)
        };
        var snapshot = await SynchronizeToStateAsync("movie", [previous], uri => uri.AbsolutePath switch
        {
            "/manifest.json" => """{"id":"catalog","catalogs":[{"type":"movie","id":"all"},{"type":"movie","id":"other"}]}""",
            "/catalog/movie/all.json" => """{"metas":[]}""",
            "/catalog/movie/other.json" => """{"metas":[{"id":"tt1234567","type":"movie","name":"Returned"}]}""",
            _ => throw new InvalidOperationException("Unexpected resource.")
        }, config => config.Addons[0].Catalogs.Add(new CatalogSubscription { Key = "second", Type = "movie", Id = "other" }));
        var retained = Assert.Single(snapshot);
        Assert.Null(retained.MissingSinceUtc);
        Assert.Equal(new[] { "installation:subscription" }, retained.MissingOwners);
        Assert.Equal(previous.Path, retained.Path);
        Assert.Equal(previous.Key, retained.Key);
    }

    [Fact]
    public async Task ReachingPaginationLimitDoesNotConfirmAbsence()
    {
        var previous = Item("movie:tt1234567", "movie", "tt1234567");
        var snapshot = await SynchronizeToStateAsync("movie", [previous], uri => uri.AbsolutePath == "/manifest.json"
            ? """{"id":"catalog","catalogs":[{"type":"movie","id":"all","extra":[{"name":"skip"}]}]}"""
            : """{"metas":[{"id":"tt1234568","type":"movie","name":"Capped"}]}""", config => config.MaxItemsPerCatalog = 1);
        Assert.Null(snapshot.Single(item => item.Key == previous.Key).MissingSinceUtc);
    }

    [Fact]
    public async Task FollowedRefreshAddsExactEpisodeIdsWithoutCatalogRequestsOrRemovingOldEpisodes()
    {
        using var database = new FollowedSeriesSelectorTests.Database();
        var first = FollowedSeriesSelectorTests.Episode("opaque");
        var missingEpisode = first with { Key = first.ContentKey + ":1:2", Episode = 2, VideoId = "old:second" };
        var unrelated = Item("movie:unrelated", "movie", "unrelated") with
        {
            MissingSinceUtc = DateTimeOffset.UtcNow.AddDays(-30),
            MissingOwners = ["installation:subscription"]
        };
        await database.Seed(first.Key, resume: 100, alternate: true);
        var snapshot = await SynchronizeToStateAsync("series", [first, missingEpisode, unrelated], uri => uri.AbsolutePath switch
        {
            "/manifest.json" => """{"id":"catalog","types":["series"],"resources":["meta"],"catalogs":[{"type":"series","id":"all"}]}""",
            "/meta/series/opaque.json" => """{"meta":{"id":"opaque","type":"series","name":"Refreshed","imdb_id":"tt1234567","videos":[{"id":"exact:new:video","title":"New episode","season":1,"episode":3}]}}""",
            _ => throw new InvalidOperationException("Targeted refresh must never request a catalog.")
        }, kind: SyncKind.FollowedSeries, selector: new FollowedSeriesSelector(database));
        foreach (var oldEpisode in new[] { first, missingEpisode })
        {
            var retained = snapshot.Single(item => item.Key == oldEpisode.Key);
            Assert.Equal(oldEpisode.Path, retained.Path);
            Assert.Equal(oldEpisode.VideoId, retained.VideoId);
            Assert.Equal(oldEpisode.Name, retained.Name);
            Assert.Equal("Refreshed", retained.SeriesName);
        }
        Assert.Equal(unrelated, snapshot.Single(item => item.Key == unrelated.Key));
        var added = snapshot.Single(item => item.Episode == 3);
        Assert.Equal("series:opaque:1:3", added.Key);
        Assert.Equal(first.ContentKey, added.ContentKey);
        Assert.Equal(first.Owners, added.Owners);
        Assert.Equal("exact:new:video", added.VideoId);
        Assert.Equal(new StreamIdentity("series", "exact:new:video"), Assert.Single(added.StreamIdentities));
    }

    [Theory]
    [InlineData("""{"meta":{"id":"opaque","type":"series","name":"Incomplete","videos":[{"id":"valid","season":1,"episode":2},{"id":"broken"}]}}""")]
    [InlineData("""{"meta":{"id":"wrong","type":"series","name":"Wrong identity","videos":[{"id":"valid","season":1,"episode":2}]}}""")]
    [InlineData("""{"meta":null}""")]
    public async Task FailedTargetedResponsePreservesSeriesAndReportsFailure(string response)
    {
        using var database = new FollowedSeriesSelectorTests.Database();
        var previous = FollowedSeriesSelectorTests.Episode("opaque");
        await database.Seed(previous.ContentKey, favorite: true);
        var snapshot = await SynchronizeToStateAsync("series", [previous], uri => uri.AbsolutePath == "/manifest.json"
            ? """{"id":"catalog","types":["series"],"resources":["meta"],"catalogs":[{"type":"series","id":"all"}]}"""
            : response, kind: SyncKind.FollowedSeries, selector: new FollowedSeriesSelector(database), expectCommit: false,
            inspectDiagnostics: diagnostics =>
            {
                var run = diagnostics.GetSnapshot().LastRun!;
                Assert.Equal("Failed", run.State);
                Assert.Equal(1, run.Preserved);
                Assert.Equal(1, run.FailedSubscriptions);
                Assert.Equal(0, run.Removed);
            });
        Assert.Equal(previous, Assert.Single(snapshot));
    }

    [Fact]
    public async Task NoFollowedSeriesIsANoOpWithoutAddonRequests()
    {
        using var database = new FollowedSeriesSelectorTests.Database();
        var previous = FollowedSeriesSelectorTests.Episode("opaque");
        var snapshot = await SynchronizeToStateAsync("series", [previous],
            _ => throw new InvalidOperationException("No addon request is needed."), kind: SyncKind.FollowedSeries,
            selector: new FollowedSeriesSelector(database), expectCommit: null,
            inspectDiagnostics: diagnostics => Assert.Equal("Completed", diagnostics.GetSnapshot().LastRun!.State));
        Assert.Equal(previous, Assert.Single(snapshot));
    }

    [Fact]
    public async Task CatalogExpansionPrioritizesFollowedSeriesWithinThePage()
    {
        using var database = new FollowedSeriesSelectorTests.Database();
        var followed = FollowedSeriesSelectorTests.Episode("followed");
        await database.Seed(followed.Key, played: true);
        var requested = new List<string>();
        await SynchronizeToStateAsync("series", [followed], uri =>
        {
            if (uri.AbsolutePath == "/manifest.json")
                return """{"id":"catalog","types":["series"],"resources":["meta"],"catalogs":[{"type":"series","id":"all"}]}""";
            if (uri.AbsolutePath == "/catalog/series/all.json")
                return """{"metas":[{"id":"other","type":"series","name":"Other"},{"id":"followed","type":"series","name":"Followed"}]}""";
            var id = uri.AbsolutePath.Contains("followed", StringComparison.Ordinal) ? "followed" : "other";
            requested.Add(id);
            return $$$"""{"meta":{"id":"{{{id}}}","type":"series","name":"Series","videos":[{"id":"exact:{{{id}}}","season":1,"episode":1}]}}""";
        }, selector: new FollowedSeriesSelector(database));
        Assert.Equal(new[] { "followed", "other" }, requested);
    }

    private static string EmptyCatalog(Uri uri) => uri.AbsolutePath == "/manifest.json"
        ? """{"id":"catalog","catalogs":[{"type":"movie","id":"all"}]}""" : """{"metas":[]}""";

    private static ManagedItem Item(string key, string type, string contentId) => new()
    {
        Key = key,
        Type = type,
        ContentId = contentId,
        ContentKey = key,
        VideoId = contentId,
        Name = "Original",
        Path = Path.Combine(Path.GetTempPath(), "siphon-catalog-fixture", "original.strm"),
        Owners = ["installation:subscription"]
    };

    [Fact]
    public async Task DistinctCatalogFailuresPreserveExpiredItemsAndIdentifyEachSubscription()
    {
        const string secret = "fixture-private-token";
        var first = Item("movie:tt1234567", "movie", "tt1234567") with
        {
            MissingSinceUtc = DateTimeOffset.UtcNow.AddDays(-30),
            MissingOwners = ["installation:subscription"]
        };
        var second = Item("movie:tt1234568", "movie", "tt1234568") with
        {
            Owners = ["installation:second"],
            MissingOwners = ["installation:second"],
            MissingSinceUtc = DateTimeOffset.UtcNow.AddDays(-30)
        };
        var snapshot = await SynchronizeToStateAsync("movie", [first, second], uri => uri.AbsolutePath switch
        {
            "/manifest.json" => """{"id":"catalog","catalogs":[{"type":"movie","id":"all"},{"type":"movie","id":"partial"},{"type":"movie","id":"healthy"}]}""",
            "/catalog/movie/all.json" => throw new HttpRequestException("https://private.invalid/?token=" + secret),
            "/catalog/movie/partial.json" => """{"metas":[{"type":"movie","name":"Missing identity"}]}""",
            "/catalog/movie/healthy.json" => """{"metas":[]}""",
            _ => throw new InvalidOperationException("Unexpected resource.")
        }, config => config.Addons[0].Catalogs.AddRange([
            new CatalogSubscription { Key = "second", Type = "movie", Id = "partial" },
            new CatalogSubscription { Key = "third", Type = "movie", Id = "healthy" }
        ]), diagnostics =>
        {
            var failures = diagnostics.GetRunStatus().LastRun!.SubscriptionFailures!;
            Assert.Equal(2, failures.Count);
            Assert.Equal("RequestFailed", failures[SyncTarget.CatalogIdentity("installation", "subscription")]);
            Assert.Equal("IncompleteCatalog", failures[SyncTarget.CatalogIdentity("installation", "second")]);
            Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(diagnostics.GetSnapshot()));
        });
        Assert.Equal(new[] { first.Key, second.Key }, snapshot.Select(item => item.Key));
        Assert.All(snapshot, item =>
        {
            Assert.Null(item.MissingSinceUtc);
            Assert.Empty(item.MissingOwners);
        });
    }

    [Fact]
    public async Task SuccessfulSubscriptionCannotHideAnotherFailureForTheSameAddon()
    {
        await SynchronizeToStateAsync("movie", [], uri => uri.AbsolutePath switch
        {
            "/manifest.json" => """{"id":"catalog","catalogs":[{"type":"movie","id":"all"},{"type":"movie","id":"other"}]}""",
            "/catalog/movie/all.json" => throw new HttpRequestException(),
            "/catalog/movie/other.json" => """{"metas":[]}""",
            _ => throw new InvalidOperationException("Unexpected resource.")
        }, config =>
        {
            config.Addons[0].Id = "7d48fa51fcf04d90a5eedf83fcb747fa";
            config.Addons[0].Catalogs.Add(new CatalogSubscription { Key = "second", Type = "movie", Id = "other" });
        }, diagnostics =>
        {
            var outcome = Assert.Single(diagnostics.GetSnapshot().Addons);
            Assert.Null(outcome.LastSuccessUtc);
            Assert.NotNull(outcome.LastFailureUtc);
            Assert.Equal("RequestFailed", outcome.LastErrorCode);
        });
    }

    [Fact]
    public async Task CatalogFetchAllowsColdVersionsButSerializesSnapshotOperations()
    {
        var config = new PluginConfiguration
        {
            PublicBaseUrl = "https://jellyfin.example",
            ProtectFavorites = false,
            ProtectResumePositions = false,
            Addons = [new ConfiguredAddon
            {
                Id = "installation",
                ManifestUrl = "https://example.org/manifest.json",
                Catalogs = [new CatalogSubscription { Key = "subscription", Type = "movie", Id = "all" }]
            }]
        };
        var configuration = new ConfigurationAccessor(() => config);
        var origin = new PendingCatalogOrigin();
        using var client = new StremioClient(origin, configuration);
        var registry = new AddonRegistry(client, configuration, NullLogger<AddonRegistry>.Instance);
        var managed = Item("movie:original", "movie", "original") with { MissingSinceUtc = DateTimeOffset.UtcNow.AddDays(-8) };
        var state = new StateBoundary([managed]);
        var movie = new Movie { Id = Guid.NewGuid(), Name = managed.Name, PresentationUniqueKey = managed.Key };
        movie.SetProviderId("Siphon", managed.Key);
        var library = DispatchProxy.Create<ILibraryManager, VersionLibraryProxy>();
        ((VersionLibraryProxy)(object)library).Movie = movie;
        movie.SetParent(((VersionLibraryProxy)(object)library).Parent);
        var persistence = DispatchProxy.Create<IItemPersistenceService, VersionPersistenceProxy>();
        var applicationPaths = DispatchProxy.Create<IApplicationPaths, PathsProxy>();
        var directory = Path.Combine(Path.GetTempPath(), "siphon-concurrent-sync-" + Guid.NewGuid().ToString("N"));
        ((PathsProxy)(object)applicationPaths).DataPath = directory;
        var paths = new SiphonPaths(applicationPaths);
        var diagnostics = new SyncDiagnostics(paths, NullLogger<SyncDiagnostics>.Instance);
        var tokens = new CapabilityTokenService(new SiphonSecretStore(paths));
        var resolver = new StreamResolver(client, registry, configuration, NullLogger<StreamResolver>.Instance,
            new SourceBindingStore(Path.Combine(directory, "sources.json")));
        var metadata = new ItemMetadataMapper(configuration, tokens, library, null!, persistence);
        var user = new User("viewer", "authentication", "password-reset") { Id = Guid.NewGuid() };
        var versions = new NativeVersionService(configuration, tokens, state, library, persistence, null!, metadata, resolver,
            NativeSourceLifetimeTests.CreateAccess(library, user));
        var cleanup = new CatalogCleanupService(configuration, state, null!, null!, new CollectionRetentionService(library, state, null!));
        var enrichment = new MetadataEnrichmentService(configuration, null!, library, null!, state);
        var sync = new CatalogSyncService(configuration, registry, client, state, null!, NullLogger<CatalogSyncService>.Instance,
            cleanup, enrichment, diagnostics);
        using var cancellation = new CancellationTokenSource();
        using var waitingCancellation = new CancellationTokenSource();
        using var refreshCancellation = new CancellationTokenSource();
        var synchronizing = sync.SynchronizeAsync(new Progress<double>(), cancellation.Token);
        Task<IReadOnlyList<NativeStreamVersion>>? opening = null;
        Task<CleanupPreview>? preview = null;
        Task? waitingTargeted = null;
        Task? fullRefresh = null;
        var originalLibrary = BaseItem.LibraryManager;
        BaseItem.LibraryManager = library;
        try
        {
            await origin.CatalogStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            preview = cleanup.PreviewAsync(0, 50, CancellationToken.None);
            opening = versions.GetVersionsAsync(movie, user.Id, cancellation.Token);
            var available = await opening.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(available);
            Assert.False(synchronizing.IsCompleted);
            Assert.False(preview.IsCompleted);
            var activeRun = diagnostics.GetSnapshot().CurrentRun!;
            waitingTargeted = sync.SynchronizeAsync(new Progress<double>(), waitingCancellation.Token, SyncKind.Targeted, new SyncTarget(CatalogKey: SyncTarget.CatalogIdentity("installation", "subscription")));
            Assert.False(waitingTargeted.IsCompleted);
            waitingCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingTargeted);
            Assert.Equal(activeRun.StartedAtUtc, diagnostics.GetSnapshot().CurrentRun!.StartedAtUtc);
            Assert.Equal("Catalogs", diagnostics.GetSnapshot().CurrentRun!.Kind);
            Assert.Null(diagnostics.GetSnapshot().LastRun);
            Assert.False(preview.IsCompleted);

            fullRefresh = sync.SynchronizeAsync(new Progress<double>(), refreshCancellation.Token, SyncKind.FullRefresh);
            Assert.False(fullRefresh.IsCompleted);
            Assert.False(synchronizing.IsCompleted);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => synchronizing);
            var afterCancellation = await preview.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("SynchronizationUnconfirmed", Assert.Single(afterCancellation.Items).Reason);
            Assert.Equal(managed, Assert.Single(state.GetItems()));

            origin.ReleaseCatalog.TrySetResult();
            await Assert.ThrowsAsync<SnapshotCapturedException>(() => fullRefresh.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("FullRefresh", diagnostics.GetSnapshot().LastRun!.Kind);
            Assert.Null(diagnostics.GetSnapshot().CurrentRun);
            await Assert.ThrowsAsync<SnapshotCapturedException>(() =>
                sync.SynchronizeAsync(new Progress<double>(), CancellationToken.None));
            Assert.Equal(managed.Key, Assert.Single(state.GetItems()).Key);
        }
        finally
        {
            cancellation.Cancel();
            BaseItem.LibraryManager = originalLibrary;
            waitingCancellation.Cancel();
            refreshCancellation.Cancel();
            origin.ReleaseCatalog.TrySetResult();
            try { await synchronizing; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            if (opening is not null)
            {
                try { await opening; }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
            if (waitingTargeted is not null)
            {
                try { await waitingTargeted; }
                catch (OperationCanceledException) when (waitingCancellation.IsCancellationRequested) { }
            }
            if (fullRefresh is not null)
            {
                try { await fullRefresh; }
                catch (OperationCanceledException) when (refreshCancellation.IsCancellationRequested) { }
                catch (SnapshotCapturedException) { }
            }
            if (preview is not null) await preview;
            diagnostics.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationDuringCatalogRequestRetainsSnapshotAndFinishesRun()
    {
        using var cancellation = new CancellationTokenSource();
        var previous = Item("movie:original", "movie", "original");
        var snapshot = await SynchronizeToStateAsync("movie", [previous], uri =>
        {
            if (uri.AbsolutePath == "/manifest.json")
                return """{"id":"catalog","catalogs":[{"type":"movie","id":"all"}]}""";
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        }, cancellationToken: cancellation.Token, expectCancellation: true,
            inspectDiagnostics: diagnostics => Assert.Equal("Cancelled", diagnostics.GetSnapshot().LastRun!.State));
        Assert.Equal(previous, Assert.Single(snapshot));
    }

    [Fact]
    public async Task OneCatalogCannotRefreshOrCleanUpAnotherCatalogsAlreadyMissingTitle()
    {
        var selected = Item("movie:tt1234567", "movie", "tt1234567");
        var unrelated = Item("movie:tt1234568", "movie", "tt1234568") with
        {
            Owners = ["installation:second"],
            MissingOwners = ["installation:second"],
            MissingSinceUtc = DateTimeOffset.UtcNow.AddDays(-30),
            Description = "Preserve unrelated metadata"
        };
        var snapshot = await SynchronizeToStateAsync("movie", [selected, unrelated], uri => uri.AbsolutePath switch
        {
            "/manifest.json" => """{"id":"catalog","catalogs":[{"type":"movie","id":"all"},{"type":"movie","id":"other"}]}""",
            "/catalog/movie/all.json" => """{"metas":[{"id":"tt1234567","type":"movie","name":"Selected update"}]}""",
            _ => throw new InvalidOperationException("An unrelated catalog must not be requested.")
        }, config => config.Addons[0].Catalogs.Add(new() { Key = "second", Type = "movie", Id = "other" }),
            kind: SyncKind.Targeted, target: new(CatalogKey: SyncTarget.CatalogIdentity("installation", "subscription")));
        Assert.Equal("Selected update", snapshot.Single(item => item.Key == selected.Key).Name);
        Assert.Equal(unrelated, snapshot.Single(item => item.Key == unrelated.Key));
    }

    [Fact]
    public async Task OneSeriesRefreshPreservesOtherSeriesAndOmittedEpisodesWithoutCatalogDiscovery()
    {
        var selected = Item("series:tt1234567:1:1", "series", "tt1234567") with
        {
            ContentKey = "series:tt1234567",
            Season = 1,
            Episode = 1,
            VideoId = "original-exact-video"
        };
        var unrelated = selected with
        {
            Key = "series:tt1234568:1:1",
            ContentKey = "series:tt1234568",
            ContentId = "tt1234568",
            MissingOwners = selected.Owners,
            MissingSinceUtc = DateTimeOffset.UtcNow.AddDays(-30),
            SeriesName = "Unrelated series"
        };
        var snapshot = await SynchronizeToStateAsync("series", [selected, unrelated], uri => uri.AbsolutePath switch
        {
            "/manifest.json" => """{"id":"catalog","types":["series"],"resources":["meta"],"catalogs":[{"type":"series","id":"all"}]}""",
            "/meta/series/tt1234567.json" => """{"meta":{"id":"tt1234567","type":"series","name":"Selected series","videos":[{"id":"new-exact-video","season":1,"episode":2}]}}""",
            _ => throw new InvalidOperationException("A series refresh must not request catalogs or another series.")
        }, kind: SyncKind.Targeted, target: new(ContentKey: selected.ContentKey));
        Assert.Equal(unrelated, snapshot.Single(item => item.Key == unrelated.Key));
        Assert.Equal("original-exact-video", snapshot.Single(item => item.Key == selected.Key).VideoId);
        Assert.Equal("Selected series", snapshot.Single(item => item.Key == selected.Key).SeriesName);
        Assert.Equal("new-exact-video", snapshot.Single(item => item.ContentKey == selected.ContentKey && item.Episode == 2).VideoId);
    }

    [Fact]
    public async Task CatalogAbsenceCannotRemoveAnExplicitManualAddition()
    {
        var previous = Item("movie:tt1234567", "movie", "tt1234567") with
        {
            Owners = ["installation:subscription", "siphon:manual:movie"],
            SearchAddonId = "installation",
            SearchResourceType = "movie"
        };
        var snapshot = await SynchronizeToStateAsync("movie", [previous], EmptyCatalog);
        var retained = Assert.Single(snapshot);
        Assert.Equal(previous.Owners, retained.Owners);
        Assert.Null(retained.MissingSinceUtc);
        Assert.Equal(previous.SearchAddonId, retained.SearchAddonId);
    }

    [Theory]
    [InlineData(SyncKind.Catalogs)]
    [InlineData(SyncKind.Targeted)]
    public async Task CatalogDiscoveryKeepsNewEpisodesOfAManuallySavedSeriesAfterCatalogAbsence(SyncKind kind)
    {
        var previous = FollowedSeriesSelectorTests.Episode("opaque") with
        {
            Owners = ["siphon:manual:series"],
            SearchAddonId = "installation",
            SearchResourceType = "series"
        };
        var target = kind == SyncKind.Targeted
            ? new SyncTarget(CatalogKey: SyncTarget.CatalogIdentity("installation", "subscription")) : null;
        var discovered = await SynchronizeToStateAsync("series", [previous], uri => uri.AbsolutePath == "/manifest.json"
            ? """{"id":"catalog","catalogs":[{"type":"series","id":"all"}]}"""
            : """{"metas":[{"id":"opaque","imdb_id":"tt1234567","type":"series","name":"Saved series","videos":[{"id":"opaque:video:1","season":1,"episode":1},{"id":"opaque:video:2","season":1,"episode":2}]},{"id":"other","type":"series","name":"Catalog only","videos":[{"id":"other:video:1","season":1,"episode":1}]}]}""",
            kind: kind, target: target);
        var added = Assert.Single(discovered, item => item.ContentKey == previous.ContentKey && item.Episode == 2);
        Assert.Contains("siphon:manual:series", added.Owners);
        Assert.Equal("opaque:video:2", added.VideoId);
        Assert.Equal(previous.Path, discovered.Single(item => item.Key == previous.Key).Path);
        Assert.DoesNotContain("siphon:manual:series", discovered.Single(item => item.ContentId == "other").Owners);

        var retained = await SynchronizeToStateAsync("series", discovered, uri => uri.AbsolutePath == "/manifest.json"
            ? """{"id":"catalog","catalogs":[{"type":"series","id":"all"}]}""" : """{"metas":[]}""",
            kind: kind, target: target);
        Assert.Equal(new[] { previous.Key, added.Key }.Order(StringComparer.Ordinal),
            retained.Select(item => item.Key).Order(StringComparer.Ordinal));
        Assert.All(retained, item =>
        {
            Assert.Contains("siphon:manual:series", item.Owners);
            Assert.Null(item.MissingSinceUtc);
        });
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("series")]
    public async Task CatalogAdoptionDropsTemporaryPreviewOwnershipAndSeriesShells(string type)
    {
        var preview = Item(type + ":tt1234567", type, "tt1234567") with
        {
            Owners = ["siphon:preview:" + type],
            IsSearchPreview = true,
            SearchAddonId = "installation",
            SearchResourceType = type,
            PreviewExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        var snapshot = await SynchronizeToStateAsync(type, [preview], uri => uri.AbsolutePath == "/manifest.json"
            ? $$"""{"id":"catalog","catalogs":[{"type":"{{type}}","id":"all"}]}"""
            : $$"""{"metas":[{"id":"tt1234567","type":"{{type}}","name":"Adopted title","videos":[{"id":"episode-video","season":1,"episode":1}]}]}""");
        var adopted = Assert.Single(snapshot);
        Assert.Equal(preview.ContentKey, adopted.ContentKey);
        Assert.False(adopted.IsSearchPreview);
        Assert.Null(adopted.PreviewExpiresUtc);
        Assert.Equal(new[] { "installation:subscription" }, adopted.Owners);
        if (type == "series") Assert.Equal(1, adopted.Episode);
    }

    [Fact]
    public async Task SelectedMetadataAddonOwnsDetailsWithoutCatalogFallback()
    {
        var previous = Item("series:tt0149460:1:1", "series", "tt0149460") with
        {
            ContentKey = "series:tt0149460",
            Season = 1,
            Episode = 1,
            Genres = ["Old catalog genre"],
            People = [new("Old catalog actor", "Actor")]
        };
        var snapshot = await SynchronizeToStateAsync("series", [previous], uri => uri.AbsolutePath switch
        {
            "/manifest.json" => """{"id":"catalog","types":["series"],"resources":["catalog","meta"],"catalogs":[{"type":"series","id":"all"}]}""",
            "/aio/manifest.json" => """{"id":"metadata","types":["series"],"resources":["meta"]}""",
            "/catalog/series/all.json" => """{"metas":[{"id":"tt0149460","type":"series","name":"Catalog name","description":"Catalog plot","genres":["Catalog genre"],"videos":[{"id":"catalog:1:1","season":1,"episode":1,"thumbnail":"https://art.example/catalog.jpg"}]}]}""",
            "/aio/meta/series/tt0149460.json" => """
                {"meta":{"id":"tt0149460","type":"series","name":"AIOMetadata title","description":"Selected series plot",
                  "poster":"https://art.example/series.jpg","background":"https://art.example/background.jpg",
                  "logo":"https://art.example/logo.png","_tmdbId":"615","genres":["Animation"],
                  "app_extras":{"cast":[{"name":"Selected actor","character":"Narrator"}],"seasonPosters":["https://art.example/season-one.jpg","https://art.example/season-four.jpg"]},
                  "videos":[{"id":"exact:one","season":1,"episode":1,"title":"First","overview":"First plot","thumbnail":"https://art.example/one.jpg"},
                    {"id":"exact:four","season":4,"episode":1,"title":"Fourth","overview":"Fourth plot"}]}}
                """,
            _ => throw new InvalidOperationException("Catalog metadata must not replace the selected metadata addon.")
        }, config =>
        {
            config.MetadataAddonId = "metadata";
            config.Addons = [.. config.Addons, new ConfiguredAddon { Id = "metadata", ManifestUrl = "https://example.org/aio/manifest.json" }];
        });
        Assert.Equal(2, snapshot.Count);
        Assert.All(snapshot, item =>
        {
            Assert.Equal("AIOMetadata title", item.SeriesName);
            Assert.Equal("Selected series plot", item.SeriesDescription);
            Assert.Equal("https://art.example/background.jpg", item.BackdropUrl);
            Assert.Equal("https://art.example/logo.png", item.LogoUrl);
            Assert.Equal(new[] { "Animation" }, item.Genres);
            Assert.Equal("Selected actor", Assert.Single(item.People).Name);
            Assert.Null(item.SeasonPosterUrl); // Unnumbered artwork must not be attached to the wrong season.
            Assert.Equal("615", item.ProviderIds["Tmdb"]);
        });
        Assert.Equal("https://art.example/one.jpg", snapshot.Single(item => item.Season == 1).ThumbnailUrl);
        Assert.Null(snapshot.Single(item => item.Season == 4).ThumbnailUrl);
        Assert.Equal("exact:four", snapshot.Single(item => item.Season == 4).VideoId);
    }

    [Fact]
    public async Task SelectedMetadataFailureRetainsExistingTitleWithoutTryingOtherAddons()
    {
        var previous = Item("movie:tt1234567", "movie", "tt1234567") with { Description = "Retained metadata" };
        var snapshot = await SynchronizeToStateAsync("movie", [previous], uri => uri.AbsolutePath switch
        {
            "/manifest.json" => """{"id":"catalog","types":["movie"],"resources":["catalog","meta"],"catalogs":[{"type":"movie","id":"all"}]}""",
            "/aio/manifest.json" => """{"id":"metadata","types":["movie"],"resources":["meta"]}""",
            "/catalog/movie/all.json" => """{"metas":[{"id":"tt1234567","type":"movie","name":"Unwanted catalog replacement","description":"Do not use this fallback"}]}""",
            "/aio/meta/movie/tt1234567.json" => """{"meta":null}""",
            _ => throw new InvalidOperationException("A selected source failure must not activate Cinemata or another metadata addon.")
        }, config =>
        {
            config.MetadataAddonId = "metadata";
            config.Addons = [.. config.Addons, new ConfiguredAddon { Id = "metadata", ManifestUrl = "https://example.org/aio/manifest.json" }];
        });
        Assert.Equal(previous, Assert.Single(snapshot));
    }

    [Theory]
    [InlineData("615", "Selected title")]
    [InlineData("999", "Original")]
    public async Task CanonicalizedMetadataMustProveTheSameIdentity(string returnedTmdbId, string expectedName)
    {
        var previous = Item("series:tmdb:615:1:1", "series", "tmdb:615") with
        {
            Name = "Episode",
            SeriesName = "Original",
            ContentKey = "series:tmdb:615",
            Season = 1,
            Episode = 1,
            ProviderIds = new() { ["Tmdb"] = "615" },
            VideoId = "original:video"
        };
        var snapshot = await SynchronizeToStateAsync("series", [previous], uri => uri.AbsolutePath switch
        {
            "/manifest.json" => """{"id":"catalog","types":["series"],"resources":["catalog"],"catalogs":[{"type":"series","id":"all"}]}""",
            "/aio/manifest.json" => """{"id":"metadata","types":["series"],"resources":["meta"]}""",
            "/catalog/series/all.json" => """{"metas":[{"id":"tmdb:615","type":"series","name":"Catalog title"}]}""",
            "/aio/meta/series/tmdb%3A615.json" => $$$"""{"meta":{"id":"tt0149460","type":"series","name":"Selected title","_tmdbId":"{{{returnedTmdbId}}}","videos":[{"id":"exact:response:video","season":1,"episode":1,"title":"Episode"}]}}""",
            _ => throw new InvalidOperationException("Unexpected metadata request.")
        }, config =>
        {
            config.MetadataAddonId = "metadata";
            config.Addons = [.. config.Addons, new ConfiguredAddon { Id = "metadata", ManifestUrl = "https://example.org/aio/manifest.json" }];
        });
        var result = Assert.Single(snapshot);
        Assert.Equal(expectedName, result.SeriesName);
        Assert.Equal(previous.Key, result.Key);
        Assert.Equal(previous.ContentKey, result.ContentKey);
        Assert.Equal(previous.Path, result.Path);
        Assert.Equal(returnedTmdbId == "615" ? "exact:response:video" : previous.VideoId, result.VideoId);
    }

    private static async Task<IReadOnlyList<ManagedItem>> SynchronizeToStateAsync(string type, IReadOnlyList<ManagedItem> previous, Func<Uri, string> respond,
        Action<PluginConfiguration>? configure = null, Action<SyncDiagnostics>? inspectDiagnostics = null,
        SyncKind kind = SyncKind.Catalogs, FollowedSeriesSelector? selector = null, bool? expectCommit = true,
        CancellationToken cancellationToken = default, bool expectCancellation = false, SyncTarget? target = null)
    {
        var config = new PluginConfiguration
        {
            RemoveMissingItems = true,
            MissingItemRetentionDays = 0,
            ProtectFavorites = false,
            ProtectResumePositions = false,
            Addons = [new ConfiguredAddon
            {
                Id = "installation",
                ManifestUrl = "https://example.org/manifest.json",
                Catalogs = [new CatalogSubscription { Key = "subscription", Type = type, Id = "all" }]
            }]
        };
        configure?.Invoke(config);
        var configuration = new ConfigurationAccessor(() => config);
        using var client = new StremioClient(new MemoryOrigin(respond), configuration);
        var registry = new AddonRegistry(client, configuration, NullLogger<AddonRegistry>.Instance);
        var state = new StateBoundary(previous);
        // Exercise real parsing and reconciliation, stopping at persistence before native
        // Jellyfin materialization, which needs a running server and its database.
        var library = DispatchProxy.Create<ILibraryManager, EmptyLibraryProxy>();
        var cleanup = new CatalogCleanupService(configuration, state, null!, null!, new CollectionRetentionService(library, state, null!));
        var enrichment = new MetadataEnrichmentService(configuration, null!, library, null!, state);
        var proxy = DispatchProxy.Create<IApplicationPaths, PathsProxy>();
        ((PathsProxy)(object)proxy).DataPath = Path.Combine(Path.GetTempPath(), "siphon-sync-" + Guid.NewGuid().ToString("N"));
        var diagnostics = new SyncDiagnostics(new SiphonPaths(proxy), NullLogger<SyncDiagnostics>.Instance);
        try
        {
            var sync = new CatalogSyncService(configuration, registry, client, state, null!, NullLogger<CatalogSyncService>.Instance,
                cleanup, enrichment, diagnostics, selector);
            if (expectCancellation)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync.SynchronizeAsync(new Progress<double>(), cancellationToken, kind, target));
            else if (expectCommit == true)
                await Assert.ThrowsAsync<SnapshotCapturedException>(() => sync.SynchronizeAsync(new Progress<double>(), cancellationToken, kind, target));
            else if (expectCommit == false)
                await Assert.ThrowsAsync<InvalidOperationException>(() => sync.SynchronizeAsync(new Progress<double>(), cancellationToken, kind, target));
            else await sync.SynchronizeAsync(new Progress<double>(), cancellationToken, kind, target);
            inspectDiagnostics?.Invoke(diagnostics);
            return state.GetItems();
        }
        finally
        {
            diagnostics.Dispose();
            var directory = ((PathsProxy)(object)proxy).DataPath;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    public class PathsProxy : DispatchProxy
    {
        public string DataPath { get; set; } = string.Empty;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name == "get_DataPath"
            ? DataPath : throw new NotSupportedException();
    }

    public class EmptyLibraryProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "GetNewItemId" => Guid.Empty,
            "GetItemById" => null,
            "GetItemList" => Array.Empty<BaseItem>(),
            _ => throw new NotSupportedException()
        };
    }

    public class VersionLibraryProxy : DispatchProxy
    {
        public Movie Movie { get; set; } = null!;
        public Folder Parent { get; } = new() { Id = Guid.NewGuid(), Path = "/fixture/parent" };
        private readonly List<Video> _versions = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "GetNewItemId" => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(args![0] + ":" + args[1]))),
            "GetItemById" => args![0] is Guid id ? id == Parent.Id ? Parent : id == Movie.Id ? Movie : _versions.FirstOrDefault(version => version.Id == id) : null,
            "GetItemList" => ((InternalItemsQuery)args![0]!).IncludeItemTypes.Any(kind => kind.ToString() is "BoxSet" or "Playlist")
                ? Array.Empty<BaseItem>() : new BaseItem[] { Movie }.Concat(_versions).ToArray(),
            "GetLinkedAlternateVersions" => _versions.ToArray(),
            "CreateItems" => Add((IEnumerable<BaseItem>)args![0]!),
            "ConfigureUserAccess" => null,
            "UpsertLinkedChild" => null,
            "GetPeople" => new List<PersonInfo>(),
            "RegisterItem" => null,
            _ => throw new NotSupportedException(method?.Name)
        };
        private object? Add(IEnumerable<BaseItem> items) { _versions.AddRange(items.OfType<Video>()); return null; }
    }

    public class VersionPersistenceProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.Name == "SaveItems" ? null : throw new NotSupportedException(method?.Name);
    }

    private sealed class PendingCatalogOrigin : ISafeHttpClient
    {
        public TaskCompletionSource CatalogStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCatalog { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method,
            IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            string body;
            if (uri.AbsolutePath == "/manifest.json")
                body = """{"id":"fixture","name":"Fixture","resources":["catalog","stream"],"types":["movie"],"catalogs":[{"type":"movie","id":"all"}]}""";
            else if (uri.AbsolutePath == "/stream/movie/original.json")
                body = """{"streams":[{"url":"https://media.example/feature.mp4","name":"Source"}]}""";
            else if (uri.AbsolutePath == "/catalog/movie/all.json")
            {
                CatalogStarted.TrySetResult();
                await ReleaseCatalog.Task.WaitAsync(cancellationToken);
                body = """{"metas":[{"id":"original","type":"movie","name":"Original"}]}""";
            }
            else throw new InvalidOperationException("Unexpected fixture request.");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    private sealed class SnapshotCapturedException : Exception { }

    private sealed class StateBoundary(IReadOnlyList<ManagedItem> initial) : ISiphonStateStore
    {
        private IReadOnlyList<ManagedItem> _items = initial;
        public IReadOnlyList<ManagedItem> GetItems() => _items;
        public ManagedItem? FindByKey(string key) => _items.FirstOrDefault(item => item.Key == key);
        public ManagedItem? FindByPath(string path) => _items.FirstOrDefault(item => item.Path == path);
        public ManagedItem? FindByContentKey(string contentKey) => _items.FirstOrDefault(item => item.ContentKey == contentKey);
        public Task SaveAsync(IReadOnlyList<ManagedItem> items, CancellationToken cancellationToken)
        {
            _items = items.ToArray();
            throw new SnapshotCapturedException();
        }
    }

    private sealed class MemoryOrigin(Func<Uri, string> respond) : ISafeHttpClient
    {
        public Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(uri)) });
        }
    }
}
