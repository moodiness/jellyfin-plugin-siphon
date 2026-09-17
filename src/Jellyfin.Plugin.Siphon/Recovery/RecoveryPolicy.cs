using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.Siphon.Recovery;

internal static class RecoveryPolicy
{
    // Jellyfin 12 ItemPersistenceService retains deleted rows on this sentinel item.
    internal static readonly Guid PlaceholderId = new("00000000-0000-0000-0000-000000000001");

    internal static bool Meaningful(UserData row) => row.Played || row.IsFavorite || row.PlayCount != 0
        || row.PlaybackPositionTicks != 0 || row.LastPlayedDate.HasValue || row.Rating.HasValue
        || row.Likes.HasValue || row.AudioStreamIndex.HasValue || row.SubtitleStreamIndex.HasValue;

    internal static string Fingerprint(UserData row, bool retention = true) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            row.UserId,
            row.CustomDataKey,
            row.Played,
            row.IsFavorite,
            row.PlayCount,
            row.PlaybackPositionTicks,
            LastPlayedTicks = row.LastPlayedDate?.Ticks,
            row.Rating,
            row.Likes,
            row.AudioStreamIndex,
            row.SubtitleStreamIndex,
            RetentionTicks = retention ? row.RetentionDate?.Ticks : null
        })));

    internal static UserData Copy(UserData row, Guid itemId, string key) => new()
    {
        ItemId = itemId,
        UserId = row.UserId,
        CustomDataKey = key,
        Item = null!,
        User = null!,
        Played = row.Played,
        IsFavorite = row.IsFavorite,
        PlayCount = row.PlayCount,
        PlaybackPositionTicks = row.PlaybackPositionTicks,
        LastPlayedDate = row.LastPlayedDate,
        Rating = row.Rating,
        Likes = row.Likes,
        AudioStreamIndex = row.AudioStreamIndex,
        SubtitleStreamIndex = row.SubtitleStreamIndex,
        RetentionDate = null
    };

    internal static bool ValidSelection(IReadOnlyCollection<string>? ids, IReadOnlySet<string> allowed)
        => ids is { Count: > 0 and <= 50 } && ids.All(id => id is not null && allowed.Contains(id))
            && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count;
}
