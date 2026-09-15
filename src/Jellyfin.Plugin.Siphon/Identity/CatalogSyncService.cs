using System.Globalization;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Protocol;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Siphon.Identity;

/// <summary>Reconciles complete catalog snapshots with owned movies and episodes.</summary>
public sealed class CatalogSyncService(
    ConfigurationAccessor configuration,
    AddonRegistry registry,
    StremioClient client,
    ISiphonStateStore state,
    LibraryWriter writer,
    ILogger<CatalogSyncService> logger)
{


    public async Task SynchronizeAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!await configuration.MutationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A Siphon synchronization is already running.");
        }

        try
        {
            var config = configuration.Current;
            var configuredSubscriptions = config.Addons.Where(a => a.Enabled)
                .SelectMany(a => a.Catalogs.Where(c => c.Enabled).Select(c => (Addon: a, Catalog: c))).ToArray();
            if (configuredSubscriptions.Length == 0 && state.GetItems().Count == 0)
            {
                progress.Report(100);
                return;
            }
            writer.ValidateTargets();

            var addons = await registry.GetEnabledAsync(cancellationToken).ConfigureAwait(false);
            var previous = state.GetItems().ToDictionary(item => item.Key, StringComparer.Ordinal);
            var desired = new Dictionary<string, ManagedItem>(previous, StringComparer.Ordinal);
            var activeOwners = configuredSubscriptions.Select(s => s.Addon.Id + ":" + s.Catalog.Key).ToHashSet(StringComparer.Ordinal);
            if (config.RemoveMissingItems)
            {
                foreach (var existing in desired.Values.ToArray())
                {
                    desired[existing.Key] = existing with { Owners = existing.Owners.Where(activeOwners.Contains).ToArray() };
                }
            }
            var failures = 0;
            for (var index = 0; index < configuredSubscriptions.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (addonConfig, subscription) = configuredSubscriptions[index];
                var owner = addonConfig.Id + ":" + subscription.Key;
                try
                {
                    var addon = addons.FirstOrDefault(a => a.Configuration.Id == addonConfig.Id)
                        ?? throw new InvalidOperationException("The selected addon is unavailable.");
                    var catalog = addon.Manifest.Catalogs.FirstOrDefault(c => c.Id == subscription.Id && c.Type == subscription.Type)
                        ?? throw new InvalidOperationException("The selected catalog is no longer advertised by the addon.");
                    var extras = subscription.Extras.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
                    foreach (var required in catalog.GetExtras().Where(e => e.IsRequired))
                    {
                        if (required.Name != "skip" && (!extras.TryGetValue(required.Name, out var value) || string.IsNullOrWhiteSpace(value)))
                        {
                            throw new InvalidOperationException("A selected catalog is missing a required extra parameter.");
                        }
                    }

                    var candidates = new Dictionary<string, ManagedItem>(StringComparer.Ordinal);
                    var metaCache = new Dictionary<string, StremioMeta>(StringComparer.Ordinal);
                    var canPaginate = catalog.GetExtras().Any(e => e.Name == "skip");
                    var limit = subscription.MaxItems ?? config.MaxItemsPerCatalog;
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var complete = true;
                    for (var skip = 0; skip < limit; skip += 100)
                    {
                        if (canPaginate)
                        {
                            extras["skip"] = skip.ToString(CultureInfo.InvariantCulture);
                        }

                        var page = await client.GetCatalogAsync(addonConfig.ManifestUrl, subscription.Type, subscription.Id, extras, cancellationToken).ConfigureAwait(false);
                        complete &= page.IsComplete;
                        var newItems = 0;
                        foreach (var meta in page.Items.Take(limit - skip))
                        {
                            if (!seen.Add(meta.Type + ":" + meta.Id))
                            {
                                continue;
                            }

                            newItems++;
                            if (meta.Type is not ("movie" or "series" or "anime"))
                            {
                                continue;
                            }

                            var full = await GetMetadataAsync(meta, addon, addons, metaCache, meta.Type == "series", cancellationToken).ConfigureAwait(false);
                            var mediaKind = meta.Type == "movie" || (meta.Type == "anime" && full.Videos.Count == 0) ? "movie" : "series";
                            var ids = ContentIdentity.ProviderIds(meta.Id, full.ProviderIds);
                            var contentKey = ContentIdentity.Key(mediaKind, meta.Id, addonConfig.Id, ids);
                            var title = full.Name;
                            var year = GetYear(full) ?? GetYear(meta);
                            var priorContent = state.FindByContentKey(contentKey);
                            if (mediaKind == "movie")
                            {
                                var key = contentKey;
                                candidates[key] = new ManagedItem
                                {
                                    Key = key,
                                    Type = mediaKind,
                                    ContentId = meta.Id,
                                    ContentKey = contentKey,
                                    ProviderIds = ids,
                                    VideoId = meta.Id,
                                    Name = title,
                                    Year = year,
                                    Description = full.Description ?? meta.Description,
                                    PosterUrl = full.Poster ?? meta.Poster,
                                    Released = full.Released ?? meta.Released,
                                    Genres = full.Genres.Count > 0 ? full.Genres.ToArray() : meta.Genres.ToArray(),
                                    StreamIdentities = ContentIdentity.MovieAliases(meta.Type, meta.Id, ids).ToArray(),
                                    Path = previous.GetValueOrDefault(key)?.Path ?? writer.GetPath(mediaKind, contentKey, ids, title, year, null, null),
                                    Owners = [owner]
                                };
                                continue;
                            }

                            complete &= full.VideosComplete;
                            foreach (var video in full.Videos.Where(v => v.Season is >= 0 and <= 9999 && v.Episode is >= 1 and <= 9999).Take(config.MaxEpisodesPerSeries))
                            {
                                var key = $"{contentKey}:{video.Season}:{video.Episode}";
                                var old = previous.GetValueOrDefault(key);
                                candidates[key] = new ManagedItem
                                {
                                    Key = key,
                                    Type = mediaKind,
                                    ContentId = meta.Id,
                                    ContentKey = contentKey,
                                    ProviderIds = ids,
                                    EpisodeProviderIds = video.ProviderIds,
                                    VideoId = video.Id,
                                    Name = string.IsNullOrWhiteSpace(video.Name) ? $"Episode {video.Episode}" : video.Name,
                                    SeriesName = title,
                                    Year = year,
                                    Season = video.Season,
                                    Episode = video.Episode,
                                    Description = video.Description,
                                    SeriesDescription = full.Description ?? meta.Description,
                                    PosterUrl = full.Poster ?? meta.Poster,
                                    Released = video.Released,
                                    Genres = full.Genres.Count > 0 ? full.Genres.ToArray() : meta.Genres.ToArray(),
                                    StreamIdentities = [new StreamIdentity(meta.Type, video.Id)],
                                    Path = old?.Path ?? writer.GetPath(mediaKind, contentKey, ids, priorContent?.SeriesName ?? title, year, video.Season, video.Episode),
                                    Owners = [owner]
                                };
                            }
                        }

                        if (!canPaginate || page.RawCount < 100 || newItems == 0)
                        {
                            break;
                        }
                    }
                    foreach (var candidate in candidates.Values)
                    {
                        if (candidate.Type == "movie") _ = NfoMetadata.Movie(candidate);
                        else
                        {
                            _ = NfoMetadata.Series(candidate);
                            _ = NfoMetadata.Episode(candidate);
                        }
                    }


                    // An owner is replaced only after every page and series expansion succeeded.
                    if (complete)
                    {
                        foreach (var existing in desired.Values.Where(item => item.Owners.Contains(owner, StringComparer.Ordinal)).ToArray())
                        {
                            desired[existing.Key] = existing with { Owners = existing.Owners.Where(o => o != owner).ToArray() };
                        }
                    }

                    foreach (var candidate in candidates.Values)
                    {
                        var existing = desired.GetValueOrDefault(candidate.Key);
                        desired[candidate.Key] = candidate with
                        {
                            Path = existing?.Path ?? candidate.Path,
                            StreamIdentities = (existing?.StreamIdentities ?? []).Concat(candidate.StreamIdentities).Distinct().ToArray(),
                            Owners = (existing?.Owners ?? []).Append(owner).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
                        };
                    }

                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or OperationCanceledException or StremioException or FormatException or System.Xml.XmlException)
                {
                    failures++;
                    logger.LogWarning("Siphon retained prior items for subscription {SubscriptionId}: {ErrorType}", subscription.Key, exception.GetType().Name);
                }

                progress.Report(70d * (index + 1) / configuredSubscriptions.Length);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (desired.Count > 100000)
            {
                throw new InvalidOperationException("Siphon is limited to 100,000 managed items. Reduce catalog and episode limits before synchronizing.");
            }

            var removals = desired.Values.Where(item => item.Owners.Length == 0 && config.RemoveMissingItems).ToArray();
            var retained = desired.Values.ExceptBy(removals.Select(item => item.Key), item => item.Key).ToArray();
            // Persist a write-ahead ownership snapshot: an interrupted write can safely be retried.
            await state.SaveAsync(desired.Values.ToArray(), cancellationToken).ConfigureAwait(false);
            var written = await writer.WriteAsync(retained, cancellationToken).ConfigureAwait(false);
            foreach (var item in removals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Delete(item);
            }

            await state.SaveAsync(retained, cancellationToken).ConfigureAwait(false);
            progress.Report(80);
            await writer.IngestAsync(new Progress<double>(value => progress.Report(80 + value * .2)), cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Siphon synchronized {ItemCount} items, wrote {WriteCount} shortcuts, removed {RemoveCount}, failed subscriptions {Failures}", retained.Length, written, removals.Length, failures);
            if (failures > 0)
            {
                throw new InvalidOperationException($"{failures} catalog subscription(s) failed. Their previous media were preserved. See Siphon logs for subscription IDs.");
            }

            progress.Report(100);
        }
        finally
        {
            configuration.MutationGate.Release();
        }
    }

    private async Task<StremioMeta> GetMetadataAsync(StremioMeta preview, RegisteredAddon origin, IReadOnlyList<RegisteredAddon> addons, Dictionary<string, StremioMeta> cache, bool requiresVideos, CancellationToken cancellationToken)
    {
        var key = preview.Type + ":" + preview.Id;
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var providers = addons.OrderByDescending(a => a.Configuration.Id == origin.Configuration.Id)
            .Where(a => AddonRegistry.Supports(a.Manifest, "meta", preview.Type, preview.Id));
        foreach (var provider in providers)
        {
            try
            {
                var meta = await client.GetMetaAsync(provider.Configuration.ManifestUrl, preview.Type, preview.Id, cancellationToken).ConfigureAwait(false);
                if (meta is not null && (!requiresVideos || meta.Videos.Count > 0))
                {
                    cache[key] = meta;
                    return meta;
                }
            }
            catch (StremioException)
            {
                logger.LogWarning("Siphon series metadata unavailable from installation {InstallationId}", provider.Configuration.Id);
            }
        }

        if (!requiresVideos || preview.Videos.Count > 0)
        {
            cache[key] = preview;
            return preview;
        }

        throw new InvalidOperationException("No configured metadata addon can expand the series into episodes.");
    }


    private static int? GetYear(StremioMeta meta)
    {
        var text = meta.ReleaseInfo ?? meta.Year;
        return text is { Length: >= 4 } && int.TryParse(text.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && year is >= 1800 and <= 2200 ? year : null;
    }
}
