using System.Globalization;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.Siphon.Identity;

/// <summary>Aggregates native user state across all users and every owned version.</summary>
public sealed class FollowedSeriesSelector(IDbContextFactory<JellyfinDbContext> database)
{
    public async Task<IReadOnlySet<string>> SelectAsync(IReadOnlyList<ManagedItem> items, CancellationToken ct)
    {
        var series = items.Where(item => item.Type == "series").ToArray();
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (series.Length == 0) return result;
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        var episodeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in series)
        {
            keys[item.Key] = item.ContentKey;
            keys[item.ContentKey] = item.ContentKey;
            keys[item.ContentKey + ":season:" + (item.Season ?? 0).ToString(CultureInfo.InvariantCulture)] = item.ContentKey;
            episodeKeys.Add(item.Key);
        }

        await using var context = await database.CreateDbContextAsync(ct).ConfigureAwait(false);
        foreach (var batch in keys.Keys.Chunk(256))
        {
            // Alternate native versions retain the same Siphon provider key. Querying
            // persisted ownership, rather than visible primaries, includes their state.
            var rows = await context.UserData.AsNoTracking()
                .Where(data => data.Item != null
                    && (data.IsFavorite || data.Played || data.PlayCount > 0 || data.PlaybackPositionTicks > 0)
                    && data.Item.Provider!.Any(provider => provider.ProviderId == "Siphon" && batch.Contains(provider.ProviderValue)))
                .SelectMany(data => data.Item!.Provider!
                    .Where(provider => provider.ProviderId == "Siphon" && batch.Contains(provider.ProviderValue)),
                    (data, provider) => new { Key = provider.ProviderValue, data.IsFavorite })
                .Distinct().ToArrayAsync(ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                if (row.IsFavorite || episodeKeys.Contains(row.Key)) result.Add(keys[row.Key]);
            }
        }
        return result;
    }
}
