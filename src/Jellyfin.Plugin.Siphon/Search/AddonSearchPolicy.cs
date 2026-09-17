using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Protocol;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Siphon.Search;

internal static class AddonSearchPolicy
{
    internal static bool Allows(SearchProviderQuery query, string type)
    {
        var kind = type == "movie" ? BaseItemKind.Movie : BaseItemKind.Series;
        return (query.IncludeItemTypes.Length > 0 ? query.IncludeItemTypes.Contains(kind) : !query.ExcludeItemTypes.Contains(kind))
            && (query.MediaTypes.Length == 0 || query.MediaTypes.Contains(MediaType.Video));
    }

    internal static bool IsReleased(DateTime? premiere, int? year, DateTimeOffset now, int bufferDays)
    {
        // A missing date is not evidence that a title is unavailable.
        if (!premiere.HasValue) return !year.HasValue || year.Value <= now.Year;
        var utc = premiere.Value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(premiere.Value, DateTimeKind.Utc) : premiere.Value.ToUniversalTime();
        return utc <= now.UtcDateTime.AddDays(-Math.Clamp(bufferDays, 0, 30));
    }

    internal static bool IsReleased(BaseItem? item, DateTimeOffset now, int bufferDays)
        => item is null || string.IsNullOrEmpty(item.GetProviderId("Siphon"))
            || IsReleased(item.PremiereDate, item.ProductionYear, now, bufferDays);

    internal static Dictionary<string, string>? Extras(StremioCatalog catalog, CatalogSubscription? saved, string term)
    {
        if (catalog.Type is not ("movie" or "series") || !catalog.GetExtras().Any(extra => extra.Name == "search")) return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["search"] = term };
        foreach (var extra in catalog.GetExtras())
        {
            if (extra.Name is "search" or "skip") continue;
            var value = saved?.Extras.LastOrDefault(value => value.Name == extra.Name)?.Value;
            if (string.IsNullOrWhiteSpace(value)) value = extra.Default;
            if (string.IsNullOrWhiteSpace(value))
            {
                if (extra.IsRequired) return null;
                continue;
            }
            if (extra.Options.Count > 0 && !extra.Options.Contains(value, StringComparer.Ordinal)) return null;
            values[extra.Name] = value;
        }
        if (catalog.GetExtras().Any(extra => extra.Name == "skip" && extra.IsRequired)) values["skip"] = "0";
        return values;
    }
}
