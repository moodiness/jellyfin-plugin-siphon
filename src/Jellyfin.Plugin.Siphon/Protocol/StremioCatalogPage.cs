namespace Jellyfin.Plugin.Siphon.Protocol;

/// <summary>Retains raw page length so malformed entries cannot truncate pagination or trigger deletion.</summary>
public sealed record StremioCatalogPage(IReadOnlyList<StremioMeta> Items, int RawCount, bool IsComplete);
