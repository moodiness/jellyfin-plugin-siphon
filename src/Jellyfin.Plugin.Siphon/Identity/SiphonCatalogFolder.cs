using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Siphon.Identity;

/// <summary>A catalog root exposed through Jellyfin's native plugin-folder and home-view APIs.</summary>
public sealed class SiphonCatalogFolder : BasePluginFolder
{
    public string AddonId { get; set; } = string.Empty;
    public string CatalogKey { get; set; } = string.Empty;
    public string CatalogType { get; set; } = string.Empty;
    public bool Published { get; set; }

    [JsonIgnore]
    public override CollectionType? CollectionType => CatalogType switch
    {
        "movie" => Jellyfin.Data.Enums.CollectionType.movies,
        "series" or "anime" => Jellyfin.Data.Enums.CollectionType.tvshows,
        _ => null
    };

    public override string GetClientTypeName() => nameof(CollectionFolder);

    public override bool IsSaveLocalMetadataEnabled() => false;

    public override bool IsVisible(User user, bool skipAllowedTagsCheck = false)
    {
        var config = Plugin.Instance?.Configuration;
        if (!Published || config is null || string.IsNullOrWhiteSpace(config.PublicBaseUrl)
            || !base.IsVisible(user, skipAllowedTagsCheck)) return false;
        foreach (var addon in config.Addons)
        {
            if (!addon.Enabled || addon.Id != AddonId) continue;
            foreach (var catalog in addon.Catalogs)
            {
                if (catalog.Enabled && catalog.Key == CatalogKey) return true;
            }
        }
        return false;
    }
}
