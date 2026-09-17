using Jellyfin.Plugin.Siphon.Calendar;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Calendar;

public sealed class CalendarNotificationTests
{
    [Fact]
    public async Task BaselineCorrectionsReleaseAndReadStateSurviveRestartWithoutCrossUserDelivery()
    {
        var directory = Path.Combine(Path.GetTempPath(), "siphon-calendar-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "notifications.json");
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        var original = Episode(Guid.NewGuid(), now.AddDays(1));
        var corrected = original with { AnnouncedReleaseUtc = now.AddDays(2) };
        Guid notification;
        try
        {
            using (var store = new CalendarNotificationStore(path))
            {
                Assert.Empty(await store.ObserveAsync(alice, Snapshot(original), true, now, CancellationToken.None));
                Assert.Empty(await store.ObserveAsync(bob, Snapshot(original), true, now, CancellationToken.None));
                var change = Assert.Single(await store.ObserveAsync(alice, Snapshot(corrected), true, now.AddMinutes(1), CancellationToken.None));
                Assert.Equal("DateChanged", change.Kind);
                Assert.Equal(original.AnnouncedReleaseUtc, change.PreviousAnnouncedReleaseUtc);
                notification = change.Id;
                Assert.Empty(store.Get(bob));
                Assert.False(await store.MarkReadAsync(bob, notification, now, CancellationToken.None));
            }
            using (var store = new CalendarNotificationStore(path))
            {
                Assert.Empty(await store.ObserveAsync(alice, Snapshot(corrected), true, now.AddHours(1), CancellationToken.None));
                Assert.Equal(notification, Assert.Single(store.Get(alice)).Id);
                Assert.True(await store.MarkReadAsync(alice, notification, now.AddHours(1), CancellationToken.None));
                var release = Assert.Single(await store.ObserveAsync(alice, Snapshot(corrected), true, corrected.AnnouncedReleaseUtc, CancellationToken.None));
                Assert.Equal("AnnouncedRelease", release.Kind);
            }
            using (var store = new CalendarNotificationStore(path))
            {
                Assert.Empty(await store.ObserveAsync(alice, Snapshot(corrected), true, now, CancellationToken.None));
                Assert.Empty(await store.ObserveAsync(alice, Snapshot(corrected), true, now.AddDays(3), CancellationToken.None));
                Assert.Equal(now.AddHours(1), store.Get(alice).Single(entry => entry.Id == notification).ReadUtc);
                Assert.Equal(2, store.Get(alice).Count);
                Assert.Empty(store.Get(bob));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task NewlyFollowedSeriesAndReenabledOptInBaselineButNewDatedEpisodeNotifies()
    {
        var directory = Path.Combine(Path.GetTempPath(), "siphon-calendar-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "notifications.json");
        var user = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var first = Episode(Guid.NewGuid(), now.AddDays(1));
        var newlyFollowed = Episode(Guid.NewGuid(), now.AddDays(-10));
        var newEpisode = first with { Id = Guid.NewGuid(), Name = "Second episode" };
        try
        {
            using var store = new CalendarNotificationStore(path);
            Assert.Empty(await store.ObserveAsync(user, Snapshot(first), true, now, CancellationToken.None));
            Assert.Empty(await store.ObserveAsync(user, Snapshot(first, newlyFollowed), true, now, CancellationToken.None));
            var announced = Assert.Single(await store.ObserveAsync(user, Snapshot(first, newlyFollowed, newEpisode), true, now, CancellationToken.None));
            Assert.Equal("NewEpisode", announced.Kind);
            Assert.Equal(newEpisode.Id, announced.Episode.Id);
            await store.ObserveAsync(user, Snapshot(first), false, now, CancellationToken.None);
            var corrected = first with { AnnouncedReleaseUtc = now.AddDays(3) };
            Assert.Empty(await store.ObserveAsync(user, Snapshot(corrected), true, now, CancellationToken.None));
            Assert.Single(store.Get(user));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task InboxBoundDoesNotReplayDiscardedEventsAndIncompleteSnapshotsDoNotAdvanceCursors()
    {
        var directory = Path.Combine(Path.GetTempPath(), "siphon-calendar-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "notifications.json");
        var user = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var first = Episode(Guid.NewGuid(), now.AddDays(1));
        var episodes = Enumerable.Range(0, 240).Select(_ => first with { Id = Guid.NewGuid() }).Prepend(first).ToArray();
        try
        {
            using (var store = new CalendarNotificationStore(path))
            {
                await store.ObserveAsync(user, Snapshot(first), true, now, CancellationToken.None);
                Assert.Empty(await store.ObserveAsync(user, Snapshot(episodes) with { Truncated = true }, true, now, CancellationToken.None));
                Assert.Equal(CalendarNotificationStore.MaximumInboxPerUser,
                    (await store.ObserveAsync(user, Snapshot(episodes), true, now, CancellationToken.None)).Count);
            }
            using (var store = new CalendarNotificationStore(path))
            {
                Assert.Equal(CalendarNotificationStore.MaximumInboxPerUser, store.Get(user).Count);
                Assert.Empty(await store.ObserveAsync(user, Snapshot(episodes), true, now, CancellationToken.None));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task TemporarilyHiddenEpisodeKeepsDurableCursorWithoutEmittingHiddenEvents()
    {
        var directory = Path.Combine(Path.GetTempPath(), "siphon-calendar-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "notifications.json");
        var user = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        var first = Episode(Guid.NewGuid(), now.AddDays(10));
        var second = first with { Id = Guid.NewGuid(), AnnouncedReleaseUtc = now.AddDays(1) };
        var correctedFirst = first with { AnnouncedReleaseUtc = now.AddDays(11) };
        var correctedSecond = second with { AnnouncedReleaseUtc = now.AddDays(2) };
        try
        {
            using (var store = new CalendarNotificationStore(path))
            {
                Assert.Empty(await store.ObserveAsync(user, Snapshot(first), true, now, CancellationToken.None));
                var added = Assert.Single(await store.ObserveAsync(user, Snapshot(first, second), true, now, CancellationToken.None));
                Assert.Equal("NewEpisode", added.Kind);
                Assert.Equal(second.Id, added.Episode.Id);
                // Changing A forces a durable commit while B is inaccessible.
                var changed = Assert.Single(await store.ObserveAsync(user, Snapshot(correctedFirst), true, now.AddMinutes(1), CancellationToken.None));
                Assert.Equal("DateChanged", changed.Kind);
                Assert.Equal(first.Id, changed.Episode.Id);
            }
            using (var store = new CalendarNotificationStore(path))
            {
                Assert.Empty(await store.ObserveAsync(user, Snapshot(correctedFirst, second), true, now.AddMinutes(2), CancellationToken.None));
                Assert.Single(store.Get(user), entry => entry.Episode.Id == second.Id);
                var changed = Assert.Single(await store.ObserveAsync(user, Snapshot(correctedFirst, correctedSecond), true, now.AddMinutes(3), CancellationToken.None));
                Assert.Equal("DateChanged", changed.Kind);
                Assert.Equal(second.AnnouncedReleaseUtc, changed.PreviousAnnouncedReleaseUtc);
                // Retaining a hidden cursor must not announce its release in absentia.
                Assert.Empty(await store.ObserveAsync(user, Snapshot(correctedFirst), true, now.AddDays(3), CancellationToken.None));
            }
            using (var store = new CalendarNotificationStore(path))
            {
                var released = Assert.Single(await store.ObserveAsync(user, Snapshot(correctedFirst, correctedSecond), true, now.AddDays(3), CancellationToken.None));
                Assert.Equal("AnnouncedRelease", released.Kind);
                Assert.Equal(second.Id, released.Episode.Id);
                Assert.Empty(await store.ObserveAsync(user, Snapshot(correctedFirst, correctedSecond), true, now.AddDays(3), CancellationToken.None));
                var third = first with { Id = Guid.NewGuid() };
                var added = Assert.Single(await store.ObserveAsync(user, Snapshot(correctedFirst, correctedSecond, third), true, now.AddDays(3), CancellationToken.None));
                Assert.Equal("NewEpisode", added.Kind);
                Assert.Equal(third.Id, added.Episode.Id);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task RetainedObservationLimitDoesNotEvictHiddenCursorsAndUnfollowReclaimsCapacity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "siphon-calendar-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "notifications.json");
        var user = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        var first = Episode(Guid.NewGuid(), now.AddDays(10));
        var otherSeries = Episode(Guid.NewGuid(), now.AddDays(10));
        var episodes = Enumerable.Range(0, FollowedCalendarService.MaximumEpisodes - 1)
            .Select(_ => first with { Id = Guid.NewGuid() }).Append(otherSeries).ToArray();
        var addedEpisode = first with { Id = Guid.NewGuid() };
        var overflow = episodes.Skip(1).Append(addedEpisode).ToArray();
        try
        {
            using (var store = new CalendarNotificationStore(path))
            {
                Assert.Empty(await store.ObserveAsync(user, Snapshot(episodes), true, now, CancellationToken.None));
                Assert.Empty(await store.ObserveAsync(user, Snapshot(overflow), true, now, CancellationToken.None));
            }
            using (var store = new CalendarNotificationStore(path))
            {
                Assert.Empty(await store.ObserveAsync(user, Snapshot(episodes), true, now, CancellationToken.None));
                var reclaimed = episodes.Where(episode => episode.SeriesId == first.SeriesId).Append(addedEpisode).ToArray();
                var added = Assert.Single(await store.ObserveAsync(user, Snapshot(reclaimed), true, now, CancellationToken.None));
                Assert.Equal("NewEpisode", added.Kind);
                Assert.Equal(addedEpisode.Id, added.Episode.Id);
            }
            using (var store = new CalendarNotificationStore(path))
            {
                var reclaimed = episodes.Where(episode => episode.SeriesId == first.SeriesId).Append(addedEpisode).ToArray();
                Assert.Empty(await store.ObserveAsync(user, Snapshot(reclaimed), true, now, CancellationToken.None));
                // Re-following a removed series is still a baseline, not a backlog.
                Assert.Empty(await store.ObserveAsync(user, Snapshot(otherSeries), true, now, CancellationToken.None));
                Assert.Single(store.Get(user));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static CalendarEpisode Episode(Guid seriesId, DateTimeOffset release)
        => new(Guid.NewGuid(), seriesId, "Series", "Episode", 1, 1, release.ToString("yyyy-MM-dd"), release, false, false);

    private static FollowedCalendarSnapshot Snapshot(params CalendarEpisode[] episodes)
        => new(episodes, episodes.Select(episode => episode.SeriesId).ToHashSet(), false);
}
