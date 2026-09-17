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
            || (config.EnableTvdbMetadata && string.IsNullOrWhiteSpace(config.TvdbApiKey))
            || (config.EnableFanartMetadata && string.IsNullOrWhiteSpace(config.FanartApiKey))
            || (config.EnableMdbListMetadata && string.IsNullOrWhiteSpace(config.MdbListApiKey)))
        {
            throw new ArgumentException("Enabled metadata providers require their corresponding credential.");
        }
        if (config.MetadataUpdateMode == "RefreshSelected" && config.MetadataRefreshFields.Length == 0
            && (config.EnableTmdbMetadata || config.EnableTvdbMetadata || config.EnableFanartMetadata || config.EnableMdbListMetadata))
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

            try
            {
                if (addon.ManifestUrl is null || addon.ManifestUrl.Length > 16384)
                {
                    throw new ArgumentException("The addon manifest URL is missing or too long.");
                }

                _ = Protocol.StremioClient.ManifestUri(addon.ManifestUrl);
            }
            catch (Protocol.StremioException)
            {
                throw new ArgumentException("Use an HTTP(S) or stremio:// addon manifest URL.");
            }

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
                    || catalog.Extras.Any(e => e is null || string.IsNullOrWhiteSpace(e.Name) || e.Name == "skip" || e.Value is null || e.Value.Length > 16384)
                    || catalog.Extras.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() != catalog.Extras.Count)
                {
                    throw new ArgumentException("Catalog subscriptions require unique keys, type and ID, valid item limits, and unique extras excluding skip.");
                }
            }
        }
    }

}
