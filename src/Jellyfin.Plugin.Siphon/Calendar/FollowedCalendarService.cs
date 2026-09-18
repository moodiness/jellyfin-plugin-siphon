using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.Siphon.Calendar;

/// <summary>Reads only native user state and published, permission-filtered episodes; never resolves streams.</summary>
public sealed class FollowedCalendarService(ISiphonStateStore state, ILibraryManager library,
    IDbContextFactory<JellyfinDbContext> database)
{
    internal const int MaximumEpisodes = 10_000;
    internal const string AvailabilityNote = "Dates are announced releases, not verified stream availability. Date-only releases begin at 00:00 UTC.";

    internal async Task<FollowedCalendarSnapshot> GetSnapshotAsync(User user, DateTimeOffset now, CancellationToken ct,
        IReadOnlyList<ManagedItemSnapshot>? managedSnapshot = null, CalendarWindow? window = null)
    {
        var managed = (managedSnapshot ?? state.GetReadSnapshot().Items).Where(item => item.Type == "series" && !string.IsNullOrEmpty(item.Key)).ToArray();
        var followed = await SelectFollowedAsync(user.Id, managed, ct).ConfigureAwait(false);
        var candidates = managed.Where(item => followed.Contains(item.ContentKey))
            .OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
        var result = new List<CalendarEpisode>();
        var seriesIds = new HashSet<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var batch in candidates.Chunk(256))
        {
            ct.ThrowIfCancellationRequested();
            var expected = batch.ToDictionary(item => library.GetNewItemId("siphon:media:" + item.Key, typeof(Episode)));
            foreach (var episode in Accessible(user, expected.Keys).OfType<Episode>())
            {
                if (!expected.TryGetValue(episode.Id, out var item) || !seen.Add(episode.Id)
                    || episode.GetProviderId("Siphon") != item.Key) continue;
                var seriesId = library.GetNewItemId("siphon:series:" + item.ContentKey, typeof(Series));
                seriesIds.Add(seriesId);
                if (seriesIds.Count > MaximumEpisodes) return new(result, seriesIds, true);
                if (episode.PremiereDate is not { } premiere) continue;
                var utc = CalendarDates.Utc(premiere);
                if (window is { } selectedWindow && !selectedWindow.Contains(utc)) continue;
                result.Add(new(episode.Id, seriesId, CalendarDates.SafeTitle(episode.SeriesName ?? item.SeriesName, "Series"),
                    CalendarDates.SafeTitle(episode.Name, "Episode"), episode.ParentIndexNumber, episode.IndexNumber,
                    CalendarWindow.Format(DateOnly.FromDateTime(utc.UtcDateTime)), utc,
                    CalendarDates.IsDateOnly(item.Released, utc), utc <= now));
                if (result.Count > MaximumEpisodes)
                    return new(result.Take(MaximumEpisodes).ToArray(), seriesIds, true);
            }
        }
        return new(result, seriesIds, false);
    }

    internal async Task<IReadOnlySet<string>> SelectFollowedAsync(Guid userId, IReadOnlyList<ManagedItemSnapshot> items, CancellationToken ct)
    {
        if (userId == Guid.Empty || items.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        var episodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            keys[item.Key] = item.ContentKey;
            keys[item.ContentKey] = item.ContentKey;
            keys[item.ContentKey + ":season:" + (item.Season ?? 0).ToString(CultureInfo.InvariantCulture)] = item.ContentKey;
            episodes.Add(item.Key);
        }
        await using var context = await database.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await context.UserData.AsNoTracking()
            .Where(data => data.UserId == userId && data.Item != null
                && (data.IsFavorite || data.Played || data.PlayCount > 0 || data.PlaybackPositionTicks > 0))
            .SelectMany(data => data.Item!.Provider!.Where(provider => provider.ProviderId == "Siphon"),
                (data, provider) => new { Key = provider.ProviderValue, data.IsFavorite })
            .Distinct().Take(100_001).ToArrayAsync(ct).ConfigureAwait(false);
        if (rows.Length > 100_000) throw new InvalidOperationException("Native followed state exceeds the bounded calendar query.");
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
            if ((row.IsFavorite || episodes.Contains(row.Key)) && keys.TryGetValue(row.Key, out var content)) selected.Add(content);
        return selected;
    }

    internal IReadOnlyList<CalendarNotification> FilterInbox(User user, IReadOnlyList<CalendarNotification> entries, CancellationToken ct)
    {
        if (entries.Count == 0) return [];
        var ids = entries.Select(entry => entry.Episode.Id).Distinct().ToArray();
        var accessible = new HashSet<Guid>();
        foreach (var batch in ids.Chunk(256))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var episode in Accessible(user, batch).OfType<Episode>())
                if (episode.GetProviderId("Siphon") is { } key && state.ReadByKey(key) is { Type: "series" }) accessible.Add(episode.Id);
        }
        return entries.Where(entry => accessible.Contains(entry.Episode.Id)).ToArray();
    }

    private IReadOnlyList<BaseItem> Accessible(User user, IEnumerable<Guid> ids)
    {
        var wanted = ids.ToHashSet();
        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            EnableTotalRecordCount = false,
            Limit = 256
        };
        // Jellyfin's access construction skips library roots if ItemIds is set first.
        library.ConfigureUserAccess(query, user);
        query.ItemIds = wanted.ToArray();
        return library.GetItemList(query).Where(item => wanted.Contains(item.Id)).ToArray();
    }
}
