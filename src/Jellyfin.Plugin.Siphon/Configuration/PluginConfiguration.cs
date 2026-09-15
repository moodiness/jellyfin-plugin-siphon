using System.Xml.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Siphon.Configuration;

/// <summary>
/// Plugin configuration persisted by Jellyfin.
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{

    private const string CinemetaInstallationId = "63962eafd14f4d8e93f57120e5f3e645";

    private const string CinemetaManifestUrl = "stremio://v3-cinemeta.strem.io/manifest.json";

    /// <summary>Gets or sets the URL clients can use to reach this Jellyfin server.</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;


    /// <summary>Gets or sets the maximum number of catalogue metas read per subscription.</summary>
    public int MaxItemsPerCatalog { get; set; } = 500;

    /// <summary>Gets or sets the maximum number of episodes expanded for one series.</summary>
    public int MaxEpisodesPerSeries { get; set; } = 500;


    /// <summary>Gets or sets explicit hostnames allowed to resolve to private IP addresses.</summary>
    [XmlArrayItem("Host")]
    public List<string> AllowedPrivateHosts { get; set; } = [];

    /// <summary>Gets or sets whether complete successful syncs remove unreferenced managed items.</summary>
    public bool RemoveMissingItems { get; set; }

    /// <summary>Gets or sets the time budget for one addon request.</summary>
    public int AddonTimeoutSeconds { get; set; } = 15;

    /// <summary>Gets or sets the number of concurrent addon requests.</summary>
    public int MaxConcurrentRequests { get; set; } = 4;

    /// <summary>Gets or sets configured addons.</summary>
    [XmlArrayItem("Addon")]
    public List<ConfiguredAddon> Addons { get; set; } =
    [
        new()
        {
            Id = CinemetaInstallationId,
            ManifestUrl = CinemetaManifestUrl,
            DisplayName = "Cinemata",
            Enabled = true
        }
    ];
}

/// <summary>One configured Stremio addon.</summary>
public sealed class ConfiguredAddon
{
    /// <summary>Gets or sets the persistent installation ID, distinct from the addon manifest ID.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string ManifestUrl { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    [XmlArrayItem("Catalog")]
    public List<CatalogSubscription> Catalogs { get; set; } = [];
}

/// <summary>A catalogue selected for synchronisation.</summary>
public sealed class CatalogSubscription
{
    public string Type { get; set; } = string.Empty;

    public string Id { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public int? MaxItems { get; set; }

    [XmlArrayItem("Extra")]
    public List<ExtraParameter> Extras { get; set; } = [];

    /// <summary>Gets or sets the stable identity of this subscription.</summary>
    public string Key { get; set; } = Guid.NewGuid().ToString("N");
}

/// <summary>A configured Stremio extra parameter.</summary>
public sealed class ExtraParameter
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}
