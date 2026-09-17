namespace Jellyfin.Plugin.Siphon.Configuration;

/// <summary>Rejects unsafe or unbounded configuration before it is persisted.</summary>
internal static class ConfigurationValidator
{
    private static readonly HashSet<string> MetadataLanguages = System.Globalization.CultureInfo
        .GetCultures(System.Globalization.CultureTypes.AllCultures).Select(culture => culture.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static void Validate(PluginConfiguration config)
    {
        if (config.PublicBaseUrl is null)
        {
            throw new ArgumentException("URL cannot be null.");
        }

        if (!string.IsNullOrWhiteSpace(config.PublicBaseUrl)
            && (!Uri.TryCreate(config.PublicBaseUrl, UriKind.Absolute, out var baseUri)
                || baseUri.Scheme is not ("http" or "https")
                || baseUri.UserInfo.Length != 0 || baseUri.Query.Length != 0 || baseUri.Fragment.Length != 0))
        {
            throw new ArgumentException("PublicBaseUrl must be an HTTP(S) server URL without credentials, query or fragment.");
        }

        if (config.MetadataAddonId is null || config.MetadataAddonId.Length > 32)
            throw new ArgumentException("Choose a configured metadata addon, or automatic selection.");
        var useAddonMetadata = config.MetadataAddonId.Length > 0;

        foreach (var credential in new[] { config.TmdbReadAccessToken, config.TvdbApiKey, config.FanartApiKey, config.MdbListApiKey, config.TvdbSubscriberPin })
        {
            if (credential is null || credential.Length > 4096 || credential.Any(char.IsControl))
            {
                throw new ArgumentException("Provider credentials must be at most 4096 characters without control characters.");
            }
        }

        if (config.MetadataLanguage is null || config.MetadataLanguage.Length > 32 || !MetadataLanguages.Contains(config.MetadataLanguage))
        {
            throw new ArgumentException("Metadata language must be a recognized language tag, or empty to inherit Jellyfin settings.");
        }
        if (config.MetadataUpdateMode is not ("FillMissing" or "RefreshSelected")
            || config.MetadataRefreshFields is null || config.MetadataRefreshFields.Length > 10
            || config.MetadataRefreshFields.Any(field => field is not ("Name" or "Overview" or "ReleaseDate" or "Genres" or "Ratings" or "People" or "Images" or "Runtime" or "ProductionLocations" or "Status"))
            || config.MetadataRefreshFields.Distinct(StringComparer.Ordinal).Count() != config.MetadataRefreshFields.Length)
        {
            throw new ArgumentException("Choose a supported metadata update mode and distinct metadata fields.");
        }
        if ((config.EnableTmdbMetadata && string.IsNullOrWhiteSpace(config.TmdbReadAccessToken))
            || (!useAddonMetadata && ((config.EnableTvdbMetadata && string.IsNullOrWhiteSpace(config.TvdbApiKey))
            || (config.EnableFanartMetadata && string.IsNullOrWhiteSpace(config.FanartApiKey))
            || (config.EnableMdbListMetadata && string.IsNullOrWhiteSpace(config.MdbListApiKey)))))
        {
            throw new ArgumentException("Enabled metadata providers require their corresponding credential.");
        }
        if (config.MetadataUpdateMode == "RefreshSelected" && config.MetadataRefreshFields.Length == 0
            && (useAddonMetadata || config.EnableTmdbMetadata || config.EnableTvdbMetadata || config.EnableFanartMetadata || config.EnableMdbListMetadata))
        {
            throw new ArgumentException("Choose at least one metadata field for enabled providers to refresh.");
        }
        if (config.MissingItemRetentionDays is < 0 or > 365)
        {
            throw new ArgumentException("Missing-item retention must be between 0 and 365 days.");
        }
        if (config.MetadataCacheHours is < 1 or > 168)
        {
            throw new ArgumentException("Metadata cache lifetime must be between 1 and 168 hours.");
        }


        if (config.MaxItemsPerCatalog is < 1 or > 10000 || config.MaxEpisodesPerSeries is < 1 or > 10000
            || config.AddonTimeoutSeconds is < 1 or > 120 || config.MaxConcurrentRequests is < 1 or > 16)
        {
            throw new ArgumentException("Item limits must be 1-10000, timeout 1-120 seconds, and concurrency 1-16.");
        }

        if (config.DefaultSearchMode is not ("All" or "Local") || config.UnreleasedBufferDays is < 0 or > 30)
            throw new ArgumentException("Choose All or Local search and a release buffer of 0-30 days.");
        if (config.IntroDbCacheHours is < 1 or > 168 || config.IntroDbSegments is null
            || config.IntroDbSegments.Length > 4
            || config.IntroDbSegments.Any(segment => segment is not ("intro" or "recap" or "outro" or "post-credits"))
            || config.IntroDbSegments.Distinct(StringComparer.Ordinal).Count() != config.IntroDbSegments.Length)
            throw new ArgumentException("IntroDB requires distinct supported segment types and a cache lifetime of 1-168 hours.");
        if (config.P2pMaxConcurrentStreams is < 1 or > 16 || config.P2pMaxCacheMiB is < 128 or > 1048576
            || config.P2pDownloadLimitKiB is < 1 or > 1048576 || config.P2pUploadLimitKiB is < 1 or > 262144
            || config.P2pIdleMinutes is < 1 or > 120 || config.P2pMetadataTimeoutSeconds is < 10 or > 600
            || (config.P2pListenPort != 0 && (config.P2pListenPort < 1024 || config.P2pListenPort > 65535 - config.P2pMaxConcurrentStreams + 1)))
            throw new ArgumentException("P2P requires bounded concurrency (1-16), cache (128-1048576 MiB), positive rate limits, idle cleanup (1-120 minutes), metadata timeout (10-600 seconds), and an available listen-port range.");
        if (config.DownloadMaxConcurrentJobs is < 1 or > 8 || config.DownloadMaxStorageMiB is < 128 or > 1048576
            || config.DownloadMaxFileMiB is < 16 or > 1048576 || config.DownloadMaxFileMiB > config.DownloadMaxStorageMiB
            || config.DownloadMaxJobsPerUser is < 1 or > 100 || config.DownloadRetentionDays is < 1 or > 365)
            throw new ArgumentException("Downloads require concurrency 1-8, storage 128-1048576 MiB, a file budget of 16 MiB up to the storage limit, 1-100 jobs per user, and retention of 1-365 days.");
        if (config.UserProfiles is null || config.UserProfiles.Length > 256)
            throw new ArgumentException("At most 256 user playback profiles are supported.");
        var profileUsers = new HashSet<Guid>();
        var profileAddonCount = 0;
        foreach (var profile in config.UserProfiles)
        {
            if (profile is null || profile.UserId == Guid.Empty || !profileUsers.Add(profile.UserId)
                || profile.Addons is null || profile.Addons.Length > 16)
                throw new ArgumentException("Playback profiles require distinct users and at most 16 addons each.");
            profileAddonCount += profile.Addons.Length;
            if (profileAddonCount > 256)
                throw new ArgumentException("At most 256 user-specific addon installations are supported in total.");
            var installationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var addon in profile.Addons)
            {
                if (addon is null || !Guid.TryParseExact(addon.Id, "N", out _) || !installationIds.Add(addon.Id)
                    || addon.Catalogs is null || addon.Catalogs.Count != 0)
                    throw new ArgumentException("Profile addons require unique installation IDs and no private catalog subscriptions.");
                ValidateManifest(addon);
            }
        }

        if (config.Addons is null || config.Addons.Length > 64 || config.AllowedPrivateHosts is null || config.AllowedPrivateHosts.Count > 64)
        {
            throw new ArgumentException("At most 64 addons and 64 private host exceptions are supported.");
        }

        foreach (var host in config.AllowedPrivateHosts)
        {
            if (string.IsNullOrWhiteSpace(host) || host.Contains('*') || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            {
                throw new ArgumentException("Private host exceptions must be exact hostnames or IP addresses, without schemes or wildcards.");
            }
        }

        var addonIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var addon in config.Addons)
        {
            if (addon is null || !Guid.TryParseExact(addon.Id, "N", out _) || !addonIds.Add(addon.Id))
            {
                throw new ArgumentException("Addon installation IDs must be unique GUIDs.");
            }

            ValidateManifest(addon);

            if (addon.Catalogs is null || addon.Catalogs.Count > 128)
            {
                throw new ArgumentException("At most 128 catalog subscriptions per addon are supported.");
            }

            var catalogKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var catalog in addon.Catalogs)
            {
                if (catalog is null || !Guid.TryParseExact(catalog.Key, "N", out _) || !catalogKeys.Add(catalog.Key)
                    || string.IsNullOrWhiteSpace(catalog.Id) || string.IsNullOrWhiteSpace(catalog.Type)
                    || catalog.MaxItems is < 1 or > 10000 || catalog.Extras is null || catalog.Extras.Count > 32
                    || catalog.Presentation is not ("Library" or "Collection" or "Both")
                    || catalog.Extras.Any(e => e is null || string.IsNullOrWhiteSpace(e.Name) || e.Name == "skip" || e.Value is null || e.Value.Length > 16384)
                    || catalog.Extras.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() != catalog.Extras.Count)
                {
                    throw new ArgumentException("Catalog subscriptions require unique keys, type and ID, valid item limits, and unique extras excluding skip.");
                }
            }
        }
        if (useAddonMetadata && (!Guid.TryParseExact(config.MetadataAddonId, "N", out _)
            || !config.Addons.Any(addon => addon.Enabled && addon.Id == config.MetadataAddonId)))
            throw new ArgumentException("The selected metadata addon must be configured and enabled. Choose a replacement or automatic selection before removing it.");
    }

    private static void ValidateManifest(ConfiguredAddon addon)
    {
        if (addon.ManifestUrl is null || addon.ManifestUrl.Length > 16384
            || addon.DisplayName is null || addon.DisplayName.Length > 512 || addon.DisplayName.Any(char.IsControl))
            throw new ArgumentException("Addon URLs must be at most 16384 characters and names at most 512 characters without controls.");
        try
        {
            _ = Protocol.StremioClient.ManifestUri(addon.ManifestUrl);
        }
        catch (Protocol.StremioException)
        {
            throw new ArgumentException("Use an HTTP(S) or stremio:// addon manifest URL.");
        }
    }

}
