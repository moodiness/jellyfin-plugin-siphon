using Jellyfin.Database.Implementations;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Identity;

public sealed class CatalogCleanupTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GraceBoundaryIsInclusiveAndPreviewMatchesAutomaticRetention()
    {
        var config = Configuration();
        config.RemoveMissingItems = true;
        var expired = Missing("expired", Now.AddDays(-7));
        var waiting = Missing("waiting", Now.AddDays(-7).AddTicks(1));
        var state = new MemoryState([expired, waiting]);
        var cleanup = Service(config, state);
        var preview = await cleanup.PreviewAsync(0, 50, CancellationToken.None);
        Assert.Equal(1, preview.TotalEligible);
        Assert.Equal("Eligible", preview.Items.Single(item => item.Key == expired.Key).Reason);
        Assert.Equal("GracePeriod", preview.Items.Single(item => item.Key == waiting.Key).Reason);
        Assert.Equal(waiting.Key, Assert.Single(await cleanup.RetainAsync(state.GetItems(), CancellationToken.None)).Key);
        Assert.Equal(2, state.GetItems().Count); // Preview and policy evaluation never mutate persisted state.
    }

    [Fact]
    public async Task ManualConfirmationCanCleanWithoutTurningOnAutomaticRemoval()
    {
        var config = Configuration();
        var missing = Missing("missing", Now.AddDays(-8));
        var present = Missing("present", Now) with { MissingSinceUtc = null, MissingOwners = [] };
        var state = new MemoryState([missing, present]) { StopAtCommit = true };
        var cleanup = Service(config, state);
        Assert.Equal(2, (await cleanup.RetainAsync(state.GetItems(), CancellationToken.None)).Count);
        var preview = await cleanup.PreviewAsync(0, 50, CancellationToken.None);
        Assert.False(preview.AutomaticRemovalEnabled);
        Assert.Equal(1, preview.TotalEligible);
        await Assert.ThrowsAsync<CommitBoundaryException>(() => cleanup.ExecuteAsync(preview.PreviewToken, true, CancellationToken.None));
        Assert.Equal(present.Key, Assert.Single(state.GetItems()).Key);
        Assert.False(config.RemoveMissingItems);
    }

    [Fact]
    public async Task ReappearedItemInvalidatesPreviewBeforeAnyCommit()
    {
        var item = Missing("returning", Now.AddDays(-8));
        var state = new MemoryState([item]);
        var cleanup = Service(Configuration(), state);
        var preview = await cleanup.PreviewAsync(0, 50, CancellationToken.None);
        state.Items = [item with { MissingSinceUtc = null, MissingOwners = [] }];
        await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.ExecuteAsync(preview.PreviewToken, true, CancellationToken.None));
        Assert.Equal(0, state.Saves);
        Assert.Equal(item.Key, Assert.Single(state.GetItems()).Key);
    }

    [Fact]
    public async Task ChangedSavedPolicyInvalidatesPreview()
    {
        var config = Configuration();
        var state = new MemoryState([Missing("missing", Now.AddDays(-8))]);
        var cleanup = Service(config, state);
        var preview = await cleanup.PreviewAsync(0, 50, CancellationToken.None);
        config.MissingItemRetentionDays = 30;
        await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.ExecuteAsync(preview.PreviewToken, true, CancellationToken.None));
        Assert.Equal(0, state.Saves);
    }

    [Fact]
    public async Task ExpiredTokenCannotDeleteAndCancellationDoesNotLeaveAnOlderGrantUsable()
    {
        var clock = new Clock(Now);
        var state = new MemoryState([Missing("missing", Now.AddDays(-8))]);
        var cleanup = Service(Configuration(), state, clock);
        var preview = await cleanup.PreviewAsync(0, 50, CancellationToken.None);
        clock.UtcNow = preview.ExpiresAtUtc;
        await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.ExecuteAsync(preview.PreviewToken, true, CancellationToken.None));
        preview = await cleanup.PreviewAsync(0, 50, CancellationToken.None);
        cleanup.BeginSynchronization();
        await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.ExecuteAsync(preview.PreviewToken, true, CancellationToken.None));
        var uncertain = await cleanup.PreviewAsync(0, 50, CancellationToken.None);
        Assert.Equal(0, uncertain.TotalEligible);
        Assert.Equal("SynchronizationUnconfirmed", Assert.Single(uncertain.Items).Reason);
        Assert.Equal(0, state.Saves);
    }

    [Fact]
    public async Task ContinuationPinsEntirePreviewIncludingTimeDependentEligibility()
    {
        var clock = new Clock(Now);
        var state = new MemoryState([Missing("first", Now.AddDays(-8)), Missing("second", Now.AddDays(-7).AddMinutes(1))]);
        var cleanup = Service(Configuration(), state, clock);
        var preview = await cleanup.PreviewAsync(0, 1, CancellationToken.None);
        var continuation = await cleanup.PreviewAsync(1, 1, CancellationToken.None, preview.PreviewToken);
        Assert.Equal(preview.PreviewToken, continuation.PreviewToken);
        Assert.Equal(preview.ExpiresAtUtc, continuation.ExpiresAtUtc);
        Assert.NotEqual(Assert.Single(preview.Items).Key, Assert.Single(continuation.Items).Key);
        clock.UtcNow = Now.AddMinutes(2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.PreviewAsync(1, 1, CancellationToken.None, preview.PreviewToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.ExecuteAsync(preview.PreviewToken, true, CancellationToken.None));
        Assert.Equal(0, state.Saves);
    }

    [Fact]
    public async Task FailedProtectionLookupFailsClosedForPreviewAndAutomaticRemoval()
    {
        var config = Configuration();
        config.ProtectFavorites = true;
        config.RemoveMissingItems = true;
        var state = new MemoryState([Missing("missing", Now.AddDays(-8))]);
        var cleanup = Service(config, state);
        var preview = await cleanup.PreviewAsync(0, 50, CancellationToken.None);
        Assert.Equal(0, preview.TotalEligible);
        Assert.Equal(1, preview.TotalProtected);
        Assert.Equal("ProtectionUnavailable", Assert.Single(preview.Items).Reason);
        Assert.Single(await cleanup.RetainAsync(state.GetItems(), CancellationToken.None));
        Assert.Equal(0, (await cleanup.ExecuteAsync(preview.PreviewToken, true, CancellationToken.None)).DeletedCount);
        Assert.Equal(0, state.Saves);
    }

    [Fact]
    public async Task ChangedProtectionCannotBeBypassedWithOldPreview()
    {
        var config = Configuration();
        var state = new MemoryState([Missing("missing", Now.AddDays(-8))]);
        var cleanup = Service(config, state);
        var preview = await cleanup.PreviewAsync(0, 50, CancellationToken.None);
        config.ProtectResumePositions = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.ExecuteAsync(preview.PreviewToken, true, CancellationToken.None));
        Assert.Equal(0, state.Saves);
    }

    [Fact]
    public void SharedOrDisabledOwnershipCannotBecomeEligible()
    {
        var item = Missing("shared", Now.AddDays(-30)) with { Owners = ["installation:subscription", "other:subscription"] };
        var active = new HashSet<string>(StringComparer.Ordinal) { "installation:subscription", "other:subscription" };
        Assert.Equal("OwnershipUnconfirmed", CatalogCleanupService.Evaluate(item, Configuration(), active, Now, null).Reason);
        item = item with { MissingOwners = item.Owners };
        active.Remove("other:subscription");
        Assert.Equal("OwnershipUnconfirmed", CatalogCleanupService.Evaluate(item, Configuration(), active, Now, null).Reason);
    }

    [Fact]
    public void EpisodeProtectionIncludesItsOwnSeriesAndSeasonButNotOtherSeasons()
    {
        var config = Configuration();
        config.ProtectFavorites = true;
        config.ProtectResumePositions = true;
        var episode = Missing("show:2:1", Now.AddDays(-30)) with { Type = "series", ContentKey = "show", Season = 2, Episode = 1 };
        var favorites = new HashSet<string>(StringComparer.Ordinal) { "show:season:1" };
        var resume = new HashSet<string>(StringComparer.Ordinal);
        Assert.Null(CatalogCleanupService.ProtectionReason(episode, config, favorites, resume));
        favorites.Add("show:season:2");
        Assert.Equal("Favorite", CatalogCleanupService.ProtectionReason(episode, config, favorites, resume));
        favorites.Clear();
        favorites.Add("show");
        Assert.Equal("Favorite", CatalogCleanupService.ProtectionReason(episode, config, favorites, resume));
        favorites.Clear();
        resume.Add(episode.Key); // All native versions carry their canonical item's ownership key.
        Assert.Equal("ResumePosition", CatalogCleanupService.ProtectionReason(episode, config, favorites, resume));
        config.ProtectResumePositions = false;
        Assert.Null(CatalogCleanupService.ProtectionReason(episode, config, favorites, resume));
    }

    private static PluginConfiguration Configuration() => new()
    {
        MissingItemRetentionDays = 7,
        ProtectFavorites = false,
        ProtectResumePositions = false,
        Addons = [new ConfiguredAddon { Id = "installation", ManifestUrl = "https://example.org/manifest.json",
            Catalogs = [new CatalogSubscription { Key = "subscription", Type = "movie", Id = "all" }] }]
    };

    private static ManagedItem Missing(string key, DateTimeOffset since) => new()
    {
        Key = key,
        ContentKey = key,
        Type = "movie",
        ContentId = key,
        VideoId = key,
        Name = key,
        Path = Path.Combine(Path.GetTempPath(), "siphon-cleanup-fixture", key + ".strm"),
        Owners = ["installation:subscription"],
        MissingOwners = ["installation:subscription"],
        MissingSinceUtc = since
    };

    private static CatalogCleanupService Service(PluginConfiguration config, MemoryState state, Clock? clock = null)
        => new(new ConfigurationAccessor(() => config), state, null!, new UnavailableDatabase(), clock ?? new Clock(Now));

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class UnavailableDatabase : IDbContextFactory<JellyfinDbContext>
    {
        public JellyfinDbContext CreateDbContext() => throw new IOException("Database unavailable.");
    }

    private sealed class CommitBoundaryException : Exception { }

    private sealed class MemoryState(IReadOnlyList<ManagedItem> initial) : ISiphonStateStore
    {
        public IReadOnlyList<ManagedItem> Items { get; set; } = initial;
        public int Saves { get; private set; }
        public bool StopAtCommit { get; init; }
        public IReadOnlyList<ManagedItem> GetItems() => Items;
        public ManagedItem? FindByKey(string key) => Items.FirstOrDefault(item => item.Key == key);
        public ManagedItem? FindByPath(string path) => Items.FirstOrDefault(item => item.Path == path);
        public ManagedItem? FindByContentKey(string contentKey) => Items.FirstOrDefault(item => item.ContentKey == contentKey);
        public Task SaveAsync(IReadOnlyList<ManagedItem> items, CancellationToken cancellationToken)
        {
            Saves++;
            Items = items;
            if (StopAtCommit) throw new CommitBoundaryException();
            return Task.CompletedTask;
        }
    }
}
