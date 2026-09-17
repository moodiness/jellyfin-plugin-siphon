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

    /// <summary>Gets or sets whether Jellyfin Web shows Siphon catalogs in its top navigation.</summary>
    public bool ShowCatalogShortcuts { get; set; }

    /// <summary>Playback credentials may vary by user; catalogues and metadata remain shared.</summary>
    [XmlArrayItem("UserProfile")]
    public UserAddonProfile[] UserProfiles { get; set; } = [];

    public string DefaultSearchMode { get; set; } = "All";
    public bool HideUnreleased { get; set; }
    public int UnreleasedBufferDays { get; set; }

    public bool EnableIntroDb { get; set; }
    public int IntroDbCacheHours { get; set; } = 24;

    [XmlArrayItem("Segment")]
    public string[] IntroDbSegments { get; set; } = ["intro", "recap", "outro", "post-credits"];

    /// <summary>P2P is opt-in and uses the server's public network identity.</summary>
    public bool EnableP2p { get; set; }
    public int P2pMaxConcurrentStreams { get; set; } = 2;
    public int P2pMaxCacheMiB { get; set; } = 20480;
    public int P2pDownloadLimitKiB { get; set; } = 4096;
    public int P2pUploadLimitKiB { get; set; } = 256;
    public int P2pIdleMinutes { get; set; } = 5;
    public int P2pMetadataTimeoutSeconds { get; set; } = 90;
    public bool P2pEnableDht { get; set; } = true;
    public int P2pListenPort { get; set; }

    /// <summary>Server-side downloads are opt-in and remain private to the requesting user.</summary>
    public bool EnableDownloadQueue { get; set; }
    public int DownloadMaxConcurrentJobs { get; set; } = 2;
    public int DownloadMaxStorageMiB { get; set; } = 51200;
    public int DownloadMaxFileMiB { get; set; } = 10240;
    public int DownloadMaxJobsPerUser { get; set; } = 20;
    public int DownloadRetentionDays { get; set; } = 7;

    /// <summary>Users must also opt in individually before release notifications are emitted.</summary>
    public bool EnableCalendarNotifications { get; set; }
    public bool EnableNotificationWebhooks { get; set; }

    /// <summary>Selected metadata addon installation; empty keeps automatic addon selection.</summary>
    public string MetadataAddonId { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional TMDB application read-access token.</summary>
    public string TmdbReadAccessToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional TVDB API key.</summary>
    public string TvdbApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional Fanart API key.</summary>
    public string FanartApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional MDBList API key.</summary>
    public string MdbListApiKey { get; set; } = string.Empty;

    public bool EnableTmdbMetadata { get; set; }
    public bool EnableTvdbMetadata { get; set; }
    public bool EnableFanartMetadata { get; set; }
    public bool EnableMdbListMetadata { get; set; }
    public string TvdbSubscriberPin { get; set; } = string.Empty;

    /// <summary>Empty inherits the native Jellyfin metadata language.</summary>
    public string MetadataLanguage { get; set; } = string.Empty;
    public string MetadataUpdateMode { get; set; } = "FillMissing";
    public int MetadataCacheHours { get; set; } = 24;

    [XmlArrayItem("Field")]
    public string[] MetadataRefreshFields { get; set; } = ["Overview", "Genres", "Ratings", "Images"];


    /// <summary>Gets or sets the maximum number of catalogue metas read per subscription.</summary>
    public int MaxItemsPerCatalog { get; set; } = 500;

    /// <summary>Gets or sets the maximum number of episodes expanded for one series.</summary>
    public int MaxEpisodesPerSeries { get; set; } = 500;


    /// <summary>Gets or sets explicit hostnames allowed to resolve to private IP addresses.</summary>
    [XmlArrayItem("Host")]
    public List<string> AllowedPrivateHosts { get; set; } = [];

    /// <summary>Gets or sets whether complete successful syncs remove unreferenced managed items.</summary>
    public bool RemoveMissingItems { get; set; }

    /// <summary>Only confirmed absence from complete catalog snapshots starts this grace period.</summary>
    public int MissingItemRetentionDays { get; set; } = 7;
    public bool ProtectFavorites { get; set; } = true;
    public bool ProtectResumePositions { get; set; } = true;

    /// <summary>Gets or sets the time budget for one addon request.</summary>
    public int AddonTimeoutSeconds { get; set; } = 15;

    /// <summary>Gets or sets the number of concurrent addon requests.</summary>
    public int MaxConcurrentRequests { get; set; } = 4;

    /// <summary>Gets or sets configured addons. XML reloads replace the defaults rather than appending to them.</summary>
    [XmlArrayItem("Addon")]
    public ConfiguredAddon[] Addons { get; set; } =
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

/// <summary>An explicit per-user set of playback and subtitle providers.</summary>
public sealed class UserAddonProfile
{
    public Guid UserId { get; set; }
    public bool OverrideAddons { get; set; }

    [XmlArrayItem("Addon")]
    public ConfiguredAddon[] Addons { get; set; } = [];
}

/// <summary>A catalogue selected for synchronisation.</summary>
public sealed class CatalogSubscription
{
    public string Type { get; set; } = string.Empty;

    public string Id { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public int? MaxItems { get; set; }

    /// <summary>Library, Collection, or Both; identity and ownership do not depend on presentation.</summary>
    public string Presentation { get; set; } = "Library";

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
