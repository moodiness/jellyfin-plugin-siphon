namespace Jellyfin.Plugin.Siphon.Configuration;

/// <summary>Rejects unsafe or unbounded configuration before it is persisted.</summary>
internal static class ConfigurationValidator
{
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
