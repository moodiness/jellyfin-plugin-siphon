using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Recovery;
using Jellyfin.Plugin.Siphon.Tests.Infrastructure;
using MediaBrowser.Common.Configuration;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Identity;

public sealed class WatchRecoveryTests
{
    [Fact]
    public async Task EmptyCurrentKeyCannotOverwriteMeaningfulOlderLiveKey()
    {
        using var database = new FollowedSeriesSelectorTests.Database();
        var fixture = await SeedAsync(database);
        await using (var context = database.CreateDbContext())
        {
            context.UserData.Add(new UserData
            {
                ItemId = fixture.TargetId,
                UserId = fixture.Source.UserId,
                CustomDataKey = "older-metadata-key",
                PlaybackPositionTicks = 900,
                Item = null!,
                User = null!
            });
            await context.SaveChangesAsync();
        }
        await using (var context = database.CreateDbContext())
            Assert.Equal("Collision", await RecoveryHistoryWriter.RestoreAsync(context, fixture.Source.UserId,
                fixture.Source.CustomDataKey, RecoveryPolicy.Fingerprint(fixture.Source), fixture.TargetId,
                "movie:owned", ["current-key"], CancellationToken.None));
        await using var verify = database.CreateDbContext();
        var live = await verify.UserData.Where(row => row.ItemId == fixture.TargetId && row.UserId == fixture.Source.UserId).ToArrayAsync();
        Assert.Equal(900, live.Single(row => row.CustomDataKey == "older-metadata-key").PlaybackPositionTicks);
        Assert.False(live.Single(row => row.CustomDataKey == "current-key").IsFavorite);
        Assert.Equal(RecoveryPolicy.Fingerprint(fixture.Source), RecoveryPolicy.Fingerprint(await verify.UserData.SingleAsync(row => row.ItemId == RecoveryPolicy.PlaceholderId)));
    }

    [Fact]
    public async Task SelectedOrphanRestoresEveryHistoryFieldWithoutConsumingOriginalOrTouchingOtherUser()
    {
        using var database = new FollowedSeriesSelectorTests.Database();
        var fixture = await SeedAsync(database);
        await using (var context = database.CreateDbContext())
            Assert.Equal("Restored", await RecoveryHistoryWriter.RestoreAsync(context, fixture.Source.UserId,
                fixture.Source.CustomDataKey, RecoveryPolicy.Fingerprint(fixture.Source), fixture.TargetId,
                "movie:owned", ["current-key", "alternate-current-key"], CancellationToken.None));
        await using var verify = database.CreateDbContext();
        var restored = await verify.UserData.SingleAsync(row => row.ItemId == fixture.TargetId && row.UserId == fixture.Source.UserId && row.CustomDataKey == "current-key");
        Assert.True(restored.Played);
        Assert.True(restored.IsFavorite);
        Assert.Equal(7, restored.PlayCount);
        Assert.Equal(1234567, restored.PlaybackPositionTicks);
        Assert.Equal(fixture.Source.LastPlayedDate, restored.LastPlayedDate);
        Assert.Equal(fixture.Source.Rating, restored.Rating);
        Assert.False(restored.Likes);
        Assert.Equal(2, restored.AudioStreamIndex);
        Assert.Equal(-1, restored.SubtitleStreamIndex);
        Assert.Null(restored.RetentionDate);
        Assert.Equal(1234567, (await verify.UserData.SingleAsync(row => row.ItemId == fixture.TargetId && row.UserId == fixture.Source.UserId && row.CustomDataKey == "alternate-current-key")).PlaybackPositionTicks);
        Assert.Equal(42, (await verify.UserData.SingleAsync(row => row.ItemId == fixture.TargetId && row.UserId != fixture.Source.UserId)).PlayCount);
        Assert.Equal(RecoveryPolicy.Fingerprint(fixture.Source), RecoveryPolicy.Fingerprint(await verify.UserData.SingleAsync(row => row.ItemId == RecoveryPolicy.PlaceholderId)));
    }

    [Fact]
    public async Task ChangedSourceAndPersonalTargetFailBeforeAnyWrite()
    {
        using var database = new FollowedSeriesSelectorTests.Database();
        var fixture = await SeedAsync(database);
        var fingerprint = RecoveryPolicy.Fingerprint(fixture.Source);
        await using (var context = database.CreateDbContext())
        {
            var orphan = await context.UserData.SingleAsync(row => row.ItemId == RecoveryPolicy.PlaceholderId);
            orphan.PlayCount++;
            await context.SaveChangesAsync();
        }
        await using (var context = database.CreateDbContext())
            Assert.Equal("Changed", await RecoveryHistoryWriter.RestoreAsync(context, fixture.Source.UserId,
                fixture.Source.CustomDataKey, fingerprint, fixture.TargetId, "movie:owned", ["current-key"], CancellationToken.None));
        await using (var context = database.CreateDbContext())
        {
            var source = await context.UserData.SingleAsync(row => row.ItemId == RecoveryPolicy.PlaceholderId);
            await context.BaseItemProviders.Where(provider => provider.ItemId == fixture.TargetId).ExecuteDeleteAsync();
            Assert.Equal("Changed", await RecoveryHistoryWriter.RestoreAsync(context, source.UserId, source.CustomDataKey,
                RecoveryPolicy.Fingerprint(source), fixture.TargetId, "movie:owned", ["current-key"], CancellationToken.None));
        }
        await using var verify = database.CreateDbContext();
        Assert.Equal(0, (await verify.UserData.SingleAsync(row => row.ItemId == fixture.TargetId && row.UserId == fixture.Source.UserId)).PlayCount);
    }

    [Fact]
    public void SelectionRejectsUnreviewedDuplicateAndUnboundedCandidates()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "selected", "other" };
        Assert.True(RecoveryPolicy.ValidSelection(["selected"], allowed));
        Assert.False(RecoveryPolicy.ValidSelection(["selected", "unreviewed"], allowed));
        Assert.False(RecoveryPolicy.ValidSelection(["selected", "selected"], allowed));
        Assert.False(RecoveryPolicy.ValidSelection([], allowed));
        var tooMany = Enumerable.Range(0, 51).Select(number => number.ToString()).ToArray();
        Assert.False(RecoveryPolicy.ValidSelection(tooMany, tooMany.ToHashSet(StringComparer.Ordinal)));
    }

    [Fact]
    public async Task PreviewCannotClaimUnattributedHistoryAndGrantsAreAdminBoundAndExpire()
    {
        using var database = new FollowedSeriesSelectorTests.Database();
        var fixture = await SeedAsync(database);
        var paths = DispatchProxy.Create<IApplicationPaths, MetadataQuotaStoreTests.PathsProxy>();
        var directory = Path.Combine(Path.GetTempPath(), "siphon-recovery-" + Guid.NewGuid().ToString("N"));
        ((MetadataQuotaStoreTests.PathsProxy)(object)paths).Directory = directory;
        var settings = new PluginConfiguration();
        var config = new ConfigurationAccessor(() => settings);
        var clock = new Clock();
        var service = new WatchRecoveryService(config, null!, new RecoveryLedger(new SiphonPaths(paths), database),
            database, null!, null!, null!, null!, null!, null!, clock);
        var administrator = Guid.NewGuid();
        var preview = await service.PreviewAsync(administrator, 0, 50, CancellationToken.None);
        var candidate = Assert.Single(preview.Items);
        Assert.Equal("Unattributed", candidate.Ownership);
        Assert.False(candidate.CanRestore);
        Assert.False(Directory.Exists(directory));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(Guid.NewGuid(), preview.PreviewToken,
            [candidate.Id], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteAsync(administrator, preview.PreviewToken,
            [candidate.Id], CancellationToken.None));
        clock.Now = preview.ExpiresAtUtc;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(administrator, preview.PreviewToken,
            [candidate.Id], CancellationToken.None));
        await using var verify = database.CreateDbContext();
        Assert.Equal(RecoveryPolicy.Fingerprint(fixture.Source), RecoveryPolicy.Fingerprint(await verify.UserData.SingleAsync(row => row.ItemId == RecoveryPolicy.PlaceholderId)));
        Assert.Equal(0, (await verify.UserData.SingleAsync(row => row.ItemId == fixture.TargetId && row.UserId == fixture.Source.UserId)).PlayCount);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task<(Guid TargetId, UserData Source)> SeedAsync(FollowedSeriesSelectorTests.Database database)
    {
        await using var context = database.CreateDbContext();
        var target = new BaseItemEntity { Id = Guid.NewGuid(), Name = "Managed", Type = "MediaBrowser.Controller.Entities.Movies.Movie" };
        target.Provider = [new BaseItemProvider { Item = target, ItemId = target.Id, ProviderId = "Siphon", ProviderValue = "movie:owned" }];
        if (!await context.BaseItems.AnyAsync(item => item.Id == RecoveryPolicy.PlaceholderId))
            context.Add(new BaseItemEntity { Id = RecoveryPolicy.PlaceholderId, Name = "Placeholder", Type = "MediaBrowser.Controller.Entities.Folder" });
        var user = new User("first", "authentication", "password-reset");
        var other = new User("second", "authentication", "password-reset");
        context.AddRange(target, user, other);
        var source = new UserData
        {
            ItemId = RecoveryPolicy.PlaceholderId,
            UserId = user.Id,
            CustomDataKey = "old-key",
            Item = null!,
            User = null!,
            Played = true,
            IsFavorite = true,
            PlayCount = 7,
            PlaybackPositionTicks = 1234567,
            LastPlayedDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            RetentionDate = new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc),
            Rating = 8,
            Likes = false,
            AudioStreamIndex = 2,
            SubtitleStreamIndex = -1
        };
        context.UserData.AddRange(source,
            new UserData { ItemId = target.Id, UserId = user.Id, CustomDataKey = "current-key", Item = null!, User = null! },
            new UserData { ItemId = target.Id, UserId = other.Id, CustomDataKey = "current-key", PlayCount = 42, Item = null!, User = null! });
        await context.SaveChangesAsync();
        return (target.Id, source);
    }
}
