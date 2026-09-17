using System.Data;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.Siphon.Recovery;

internal static class RecoveryHistoryWriter
{
    internal static async Task<string> RestoreAsync(JellyfinDbContext context, Guid userId, string? dataKey,
        string fingerprint, Guid targetId, string managedKey, string[] targetKeys, CancellationToken ct)
    {
        if (targetKeys.Length is 0 or > 32 || targetKeys.Any(string.IsNullOrEmpty)
            || targetKeys.Distinct(StringComparer.Ordinal).Count() != targetKeys.Length) return "Changed";
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
        var source = await context.UserData.AsNoTracking().SingleOrDefaultAsync(row => row.ItemId == RecoveryPolicy.PlaceholderId
            && row.UserId == userId && row.CustomDataKey == dataKey, ct).ConfigureAwait(false);
        if (source is null || RecoveryPolicy.Fingerprint(source) != fingerprint) return "Changed";
        // Inspect all keys: an empty current key must never hide meaningful older-key history.
        var live = await context.UserData.Where(row => row.ItemId == targetId && row.UserId == userId).ToArrayAsync(ct).ConfigureAwait(false);
        if (live.Any(RecoveryPolicy.Meaningful)) return "Collision";
        if (!await context.BaseItems.AnyAsync(item => item.Id == targetId
            && item.Provider!.Any(provider => provider.ProviderId == "Siphon" && provider.ProviderValue == managedKey), ct).ConfigureAwait(false))
            return "Changed";
        context.UserData.RemoveRange(live);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        context.UserData.AddRange(targetKeys.Select(key => RecoveryPolicy.Copy(source, targetId, key)));
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        // Originals stay untouched, even after success. Jellyfin's normal orphan retention policy applies.
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return "Restored";
    }
}
