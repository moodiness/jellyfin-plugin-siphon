using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Metadata;
using Jellyfin.Plugin.Siphon.Protocol;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Siphon.Identity;

public enum SyncKind { Catalogs, FullRefresh, FollowedSeries }

/// <summary>Reconciles complete catalog snapshots with owned movies and episodes.</summary>
public sealed class CatalogSyncService(
    Configuration.ConfigurationAccessor configuration,
    AddonRegistry registry,
    StremioClient client,
    ISiphonStateStore state,
    LibraryMaterializer materializer,
    ILogger<CatalogSyncService> logger,
    CatalogCleanupService cleanup,
    MetadataEnrichmentService enrichment,
    SyncDiagnostics diagnostics,
    FollowedSeriesSelector? followedSeries = null)
{


    public async Task SynchronizeAsync(IProgress<double> progress, CancellationToken cancellationToken, SyncKind kind = SyncKind.Catalogs)
    {
        if (!await configuration.SynchronizationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A Siphon synchronization is already running.");
        }

        string[] diagnosticAddons = [];
        var diagnosticErrors = new Dictionary<string, string>(StringComparer.Ordinal);
        var catalogsCompleted = false;
        var runState = "Failed";
        try
        {
            diagnostics.BeginRun(kind.ToString());
            diagnostics.ReportStage("Preparing", "Items", 0, 0);
            var config = configuration.Current;
            var previous = state.GetItems().ToDictionary(item => item.Key, StringComparer.Ordinal);
            var priority = followedSeries is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : await followedSeries.SelectAsync(previous.Values.ToArray(), cancellationToken).ConfigureAwait(false);
            diagnostics.SetPrioritySeries(priority.Count);
            if (kind == SyncKind.FollowedSeries)
            {
                await SynchronizeFollowedAsync(previous, priority, progress, cancellationToken).ConfigureAwait(false);
                runState = "Completed";
                return;
            }
            var configuredSubscriptions = config.Addons.Where(a => a.Enabled)
                .SelectMany(a => a.Catalogs.Where(c => c.Enabled).Select(c => (Addon: a, Catalog: c))).ToArray();
            diagnosticAddons = configuredSubscriptions.Select(subscription => subscription.Addon.Id).Distinct(StringComparer.Ordinal).ToArray();
            if (configuredSubscriptions.Length == 0 && state.GetItems().Count == 0)
            {
                progress.Report(100);
                diagnostics.SetSummary(0, 0, 0, 0, 0, 0);
                runState = "Completed";
                return;
            }

            cleanup.BeginSynchronization();
            var addons = await registry.GetEnabledAsync(cancellationToken).ConfigureAwait(false);
            var desired = new Dictionary<string, ManagedItem>(previous, StringComparer.Ordinal);
            var activeOwners = configuredSubscriptions.Select(s => s.Addon.Id + ":" + s.Catalog.Key).ToHashSet(StringComparer.Ordinal);
            var catalogNames = new Dictionary<string, string>(StringComparer.Ordinal);
            // A disabled or removed subscription is not a successful empty snapshot.
            // Retain historical owners rather than silently converting deselection into deletion.
            var failures = 0;
            var preserved = new HashSet<string>(StringComparer.Ordinal);
            diagnostics.ReportStage("Catalogs", "Catalogs", 0, configuredSubscriptions.Length);
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
                    catalogNames[owner] = catalog.Name;
                    var extras = subscription.Extras.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
                    foreach (var required in catalog.GetExtras().Where(e => e.IsRequired))
                    {
                        if (required.Name == "skip" || !string.IsNullOrWhiteSpace(extras.GetValueOrDefault(required.Name))) continue;
                        // A required extra that declares a default is satisfied by that default;
                        // Stremio addons use this for "all genres"-style filters.
                        if (!string.IsNullOrWhiteSpace(required.Default)) extras[required.Name] = required.Default;
                        else throw new InvalidOperationException($"The catalog requires a value for '{required.Name}'.");
                    }

                    var candidates = new Dictionary<string, ManagedItem>(StringComparer.Ordinal);
                    var priorContentKeys = previous.Values.Where(item => item.Owners.Contains(owner, StringComparer.Ordinal))
                        .GroupBy(item => (item.Type, item.ContentId))
                        .ToDictionary(group => group.Key, group => group.First().ContentKey);
                    var metaCache = new Dictionary<string, StremioMeta>(StringComparer.Ordinal);
                    var canPaginate = catalog.GetExtras().Any(e => e.Name == "skip");
                    var limit = subscription.MaxItems ?? config.MaxItemsPerCatalog;
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var complete = true;
                    var reachedEnd = false;
                    var limited = false;
                    for (var skip = 0; skip < limit;)
                    {
                        if (canPaginate)
                        {
                            extras["skip"] = skip.ToString(CultureInfo.InvariantCulture);
                        }

                        var page = await client.GetCatalogAsync(addonConfig.ManifestUrl, subscription.Type, subscription.Id, extras, cancellationToken).ConfigureAwait(false);
                        complete &= page.IsComplete;
                        limited |= page.RawCount > limit - skip;
                        var newItems = 0;
                        foreach (var meta in page.Items.Take(limit - skip).OrderByDescending(meta =>
                            priorContentKeys.TryGetValue(("series", meta.Id), out var key) && priority.Contains(key)))
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

                            var requiresVideos = meta.Type == "series"
                                || (meta.Type == "anime" && priorContentKeys.ContainsKey(("series", meta.Id)));
                            var full = await GetMetadataAsync(meta, addon, addons, metaCache, requiresVideos, cancellationToken).ConfigureAwait(false);
                            var conversion = ConvertMetadata(meta, full, addonConfig.Id, [owner], priorContentKeys,
                                candidates, desired, config.MaxEpisodesPerSeries);
                            complete &= conversion.Complete;
                            limited |= conversion.Limited;
                        }

                        if (!canPaginate || page.RawCount == 0)
                        {
                            reachedEnd = true;
                            break;
                        }
                        if (newItems == 0)
                        {
                            // An addon ignoring skip did not provide a complete snapshot.
                            complete = false;
                            break;
                        }
                        skip += page.RawCount;
                    }
                    limited |= canPaginate && !reachedEnd;
                    if (!complete)
                    {
                        failures++;
                        diagnosticErrors.TryAdd(addonConfig.Id, "IncompleteCatalog");
                        preserved.UnionWith(previous.Values.Where(item => item.Owners.Contains(owner, StringComparer.Ordinal)).Select(item => item.Key));
                    }
                    // Import limits are not failures, but only a complete, unbounded snapshot
                    // establishes absence. Deselection and partial snapshots never authorize deletion.
                    foreach (var existing in desired.Values.Where(item => item.Owners.Contains(owner, StringComparer.Ordinal)).ToArray())
                    {
                        var absent = complete && !limited && !candidates.ContainsKey(existing.Key);
                        desired[existing.Key] = existing with
                        {
                            MissingOwners = existing.MissingOwners.Where(value => value != owner)
                                .Concat(absent ? [owner] : Array.Empty<string>()).ToArray(),
                            MissingSinceUtc = absent ? existing.MissingSinceUtc : null
                        };
                    }

                    foreach (var candidate in candidates.Values)
                    {
                        var existing = desired.GetValueOrDefault(candidate.Key);
                        desired[candidate.Key] = MergeMetadata(candidate, existing) with
                        {
                            Path = existing?.Path ?? candidate.Path,
                            StreamIdentities = (existing?.StreamIdentities ?? []).Concat(candidate.StreamIdentities).Distinct().ToArray(),
                            MissingSinceUtc = null,
                            MissingOwners = (existing?.MissingOwners ?? []).Where(value => value != owner).ToArray(),
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
                    preserved.UnionWith(previous.Values.Where(item => item.Owners.Contains(owner, StringComparer.Ordinal)).Select(item => item.Key));
                    diagnosticErrors.TryAdd(addonConfig.Id, !addons.Any(addon => addon.Configuration.Id == addonConfig.Id)
                        ? "ManifestUnavailable" : "RequestFailed");
                    foreach (var existing in desired.Values.Where(item => item.Owners.Contains(owner, StringComparer.Ordinal)).ToArray())
                    {
                        desired[existing.Key] = existing with
                        {
                            MissingOwners = existing.MissingOwners.Where(value => value != owner).ToArray(),
                            MissingSinceUtc = null
                        };
                    }
                    logger.LogWarning("Siphon retained prior items for subscription {SubscriptionId}: {ErrorType}", subscription.Key, exception.GetType().Name);
                }

                progress.Report(70d * (index + 1) / configuredSubscriptions.Length);
                diagnostics.ReportStage("Catalogs", "Catalogs", index + 1, configuredSubscriptions.Length);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (desired.Count > 100000)
            {
                throw new InvalidOperationException("Siphon is limited to 100,000 managed items. Reduce catalog and episode limits before synchronizing.");
            }

            var now = DateTimeOffset.UtcNow;
            var snapshot = desired.Values.Select(item => item with
            {
                MissingSinceUtc = item.Owners.Length > 0
                    && item.Owners.All(owner => activeOwners.Contains(owner) && item.MissingOwners.Contains(owner, StringComparer.Ordinal))
                    ? item.MissingSinceUtc ?? now : null,
                MissingOwners = item.MissingOwners.Where(activeOwners.Contains).ToArray()
            }).ToArray();
            progress.Report(70);
            var retained = await cleanup.RetainAsync(snapshot, cancellationToken).ConfigureAwait(false);
            progress.Report(71);
            retained = retained.OrderByDescending(item => priority.Contains(item.ContentKey)).ToArray();
            diagnostics.ReportStage("Metadata", "Titles", 0, retained.Select(item => item.ContentKey).Distinct(StringComparer.Ordinal).Count());
            retained = await enrichment.EnrichAsync(retained, cancellationToken, new ProgressRange(progress, 71, 85),
                forceRefresh: kind == SyncKind.FullRefresh).ConfigureAwait(false);
            await PublishAsync(previous, retained, catalogNames, progress, cancellationToken, kind,
                retained.Count(item => preserved.Contains(item.Key)), failures).ConfigureAwait(false);
            cleanup.CompleteSynchronization();
            catalogsCompleted = true;
            logger.LogInformation("Siphon synchronized {ItemCount} library media items, removed {RemoveCount}, failed subscriptions {Failures}", retained.Count, snapshot.Length - retained.Count, failures);
            if (failures > 0)
            {
                throw new InvalidOperationException($"{failures} catalog subscription(s) failed. Their previous items were preserved. See Siphon logs for subscription IDs.");
            }

            progress.Report(100);
            runState = "Completed";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            runState = "Cancelled";
            throw;
        }
        finally
        {
            try
            {
                foreach (var addonId in diagnosticAddons)
                {
                    var error = cancellationToken.IsCancellationRequested ? "Cancelled"
                        : diagnosticErrors.GetValueOrDefault(addonId) ?? (catalogsCompleted ? null : "SyncFailed");
                    diagnostics.RecordSync(addonId, error is null, error);
                }
                diagnostics.FinishRun(runState);
            }
            finally
            {
                configuration.SynchronizationGate.Release();
            }
        }
    }

    private async Task SynchronizeFollowedAsync(Dictionary<string, ManagedItem> previous, IReadOnlySet<string> priority,
        IProgress<double> progress, CancellationToken ct)
    {
        if (priority.Count == 0)
        {
            diagnostics.SetSummary(0, 0, 0, 0, 0, 0);
            progress.Report(100);
            return;
        }

        var config = configuration.Current;
        var addons = await registry.GetEnabledAsync(ct).ConfigureAwait(false);
        var desired = new Dictionary<string, ManagedItem>(previous, StringComparer.Ordinal);
        var groups = previous.Values.Where(item => item.Type == "series" && priority.Contains(item.ContentKey))
            .GroupBy(item => item.ContentKey).ToArray();
        var refreshed = new HashSet<string>(StringComparer.Ordinal);
        var preserved = 0;
        var failures = 0;
        diagnostics.ReportStage("Catalogs", "Series", 0, groups.Length);
        for (var index = 0; index < groups.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var group = groups[index];
            var existing = group.First();
            try
            {
                var owners = group.SelectMany(item => item.Owners).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var origin = addons.FirstOrDefault(addon => owners.Any(owner => owner.StartsWith(addon.Configuration.Id + ":", StringComparison.Ordinal)))
                    ?? throw new InvalidOperationException("The series origin is unavailable.");
                var type = origin.Configuration.Catalogs.FirstOrDefault(catalog => owners.Contains(origin.Configuration.Id + ":" + catalog.Key, StringComparer.Ordinal))?.Type
                    ?? existing.StreamIdentities.FirstOrDefault()?.Type ?? "series";
                var preview = new StremioMeta
                {
                    Id = existing.ContentId,
                    Type = type,
                    Name = existing.SeriesName ?? existing.Name
                };
                var full = await GetMetadataAsync(preview, origin, addons, new Dictionary<string, StremioMeta>(StringComparer.Ordinal), true, ct).ConfigureAwait(false);
                // Unlike catalog snapshots, a targeted refresh has no authority to
                // establish absence, and incomplete results cannot replace old data.
                if (!full.VideosComplete || full.Videos.Count == 0)
                    throw new InvalidOperationException("The series metadata is incomplete.");
                var candidates = new Dictionary<string, ManagedItem>(StringComparer.Ordinal);
                ConvertMetadata(preview, full, origin.Configuration.Id, owners,
                    new Dictionary<(string, string), string> { [("series", existing.ContentId)] = existing.ContentKey },
                    candidates, desired, config.MaxEpisodesPerSeries, group);
                foreach (var candidate in candidates.Values)
                {
                    var prior = desired.GetValueOrDefault(candidate.Key);
                    desired[candidate.Key] = MergeMetadata(candidate, prior) with
                    {
                        Path = prior?.Path ?? candidate.Path,
                        Owners = prior?.Owners ?? owners,
                        MissingSinceUtc = prior?.MissingSinceUtc,
                        MissingOwners = prior?.MissingOwners ?? [],
                        StreamIdentities = (prior?.StreamIdentities ?? []).Concat(candidate.StreamIdentities).Distinct().ToArray()
                    };
                }
                refreshed.Add(group.Key);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException
                or ArgumentException or System.Text.Json.JsonException or OperationCanceledException or StremioException or FormatException or System.Xml.XmlException)
            {
                failures++;
                preserved += group.Count();
                logger.LogWarning("Siphon retained followed series after metadata failure: {ErrorType}", exception.GetType().Name);
            }
            diagnostics.ReportStage("Catalogs", "Series", index + 1, groups.Length);
            progress.Report(70d * (index + 1) / groups.Length);
        }

        if (desired.Count > 100000)
            throw new InvalidOperationException("Siphon is limited to 100,000 managed items.");
        if (refreshed.Count > 0)
        {
            cleanup.BeginSynchronization();
            var selected = desired.Values.Where(item => refreshed.Contains(item.ContentKey)).ToArray();
            diagnostics.ReportStage("Metadata", "Titles", 0, refreshed.Count);
            var enriched = await enrichment.EnrichAsync(selected, ct, new ProgressRange(progress, 71, 85), forceRefresh: true).ConfigureAwait(false);
            foreach (var item in enriched) desired[item.Key] = item;
            await PublishAsync(previous, desired.Values.ToArray(), null, progress, ct, SyncKind.FollowedSeries, preserved, failures, refreshed).ConfigureAwait(false);
            cleanup.CompleteSynchronization();
        }
        else diagnostics.SetSummary(0, 0, 0, 0, preserved, failures);
        if (failures > 0)
            throw new InvalidOperationException($"{failures} followed series could not be refreshed. Their previous items were preserved.");
        progress.Report(100);
    }

    private async Task PublishAsync(Dictionary<string, ManagedItem> previous, IReadOnlyList<ManagedItem> retained,
        IReadOnlyDictionary<string, string>? catalogNames, IProgress<double> progress, CancellationToken ct,
        SyncKind kind, int preserved, int failures, IReadOnlySet<string>? scopedContentKeys = null)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var added = 0;
        var updated = 0;
        var unchanged = 0;
        foreach (var item in retained)
        {
            if (scopedContentKeys is not null && !scopedContentKeys.Contains(item.ContentKey)) continue;
            if (!previous.TryGetValue(item.Key, out var prior)) { added++; changed.Add(item.Key); }
            else if (!ManagedItemComparison.Equals(item, prior)) { updated++; changed.Add(item.Key); }
            else unchanged++;
        }
        var retainedKeys = retained.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var removed = previous.Keys.Count(key => !retainedKeys.Contains(key));
        diagnostics.SetSummary(added, updated, unchanged, removed, preserved, failures);
        await configuration.MutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            diagnostics.ReportStage("Saving", "Items", 0, retained.Count);
            await state.SaveAsync(retained, ct).ConfigureAwait(false);
            diagnostics.ReportStage("Saving", "Items", retained.Count, retained.Count);
            progress.Report(86);
            diagnostics.ReportStage("Publishing", "Items", 0, retained.Count);
            await materializer.ApplyAsync(retained, ct, catalogNames, new ProgressRange(progress, 86, 99),
                kind == SyncKind.FullRefresh ? null : changed, scopedContentKeys).ConfigureAwait(false);
            diagnostics.ReportStage("Finalizing", "Items", retained.Count, retained.Count);
        }
        finally
        {
            configuration.MutationGate.Release();
        }
    }

    private static (bool Complete, bool Limited) ConvertMetadata(StremioMeta meta, StremioMeta full, string addonId,
        string[] owners, IReadOnlyDictionary<(string, string), string> priorContentKeys,
        Dictionary<string, ManagedItem> candidates, IReadOnlyDictionary<string, ManagedItem> desired, int maxEpisodes,
        IEnumerable<ManagedItem>? preservedEpisodes = null)
    {
        var mediaKind = meta.Type == "movie" || (meta.Type == "anime" && full.Videos.Count == 0 && meta.Videos.Count == 0) ? "movie" : "series";
        var ids = ContentIdentity.ProviderIds(meta.Id, MergeProviderIds(full.ProviderIds, meta.ProviderIds));
        // Richer metadata must not replace the established subscription identity.
        var contentKey = priorContentKeys.GetValueOrDefault((mediaKind, meta.Id))
            ?? ContentIdentity.Key(mediaKind, meta.Id, addonId, ids);
        var title = Prefer(full.Name, meta.Name)!;
        var metadata = new ManagedItem
        {
            Key = contentKey,
            Type = mediaKind,
            ContentId = meta.Id,
            ContentKey = contentKey,
            ProviderIds = ids,
            VideoId = meta.Id,
            Name = title,
            Year = GetYear(full) ?? GetYear(meta),
            Description = Prefer(full.Description, meta.Description),
            PosterUrl = Prefer(full.Poster, meta.Poster),
            BackdropUrl = Prefer(full.Background, meta.Background),
            LogoUrl = Prefer(full.Logo, meta.Logo),
            Released = Prefer(full.Released, meta.Released),
            Genres = MergeValues(full.Genres, meta.Genres),
            CommunityRating = full.CommunityRating ?? meta.CommunityRating,
            RunTimeTicks = full.RunTimeTicks ?? meta.RunTimeTicks,
            OfficialRating = Prefer(full.OfficialRating, meta.OfficialRating),
            SeriesStatus = Prefer(full.Status, meta.Status),
            ProductionLocations = MergeValues(full.ProductionLocations, meta.ProductionLocations),
            People = MergePeople(full.People, meta.People),
            Path = VirtualPath(mediaKind, contentKey),
            Owners = owners
        };
        if (mediaKind == "movie")
        {
            candidates[contentKey] = MergeMetadata(metadata, candidates.GetValueOrDefault(contentKey)) with
            {
                StreamIdentities = ContentIdentity.MovieAliases(meta.Type, meta.Id, ids).ToArray()
            };
            return (true, false);
        }
        if (preservedEpisodes is not null)
        {
            // An omitted episode remains playable, but shares the refreshed series
            // metadata. Keep its exact resource identity and all episode-specific data.
            foreach (var episode in preservedEpisodes)
            {
                candidates[episode.Key] = MergeMetadata(episode with
                {
                    ProviderIds = metadata.ProviderIds,
                    SeriesName = title,
                    Year = metadata.Year,
                    SeriesDescription = metadata.Description,
                    SeriesReleased = metadata.Released,
                    PosterUrl = metadata.PosterUrl,
                    BackdropUrl = metadata.BackdropUrl,
                    LogoUrl = metadata.LogoUrl,
                    SeasonPosterUrl = Prefer(full.SeasonPosters.ElementAtOrDefault(episode.Season ?? 0),
                        meta.SeasonPosters.ElementAtOrDefault(episode.Season ?? 0)),
                    Genres = metadata.Genres,
                    CommunityRating = metadata.CommunityRating,
                    RunTimeTicks = metadata.RunTimeTicks,
                    OfficialRating = metadata.OfficialRating,
                    SeriesStatus = metadata.SeriesStatus,
                    ProductionLocations = metadata.ProductionLocations,
                    People = metadata.People
                }, episode);
            }
        }

        var videos = full.Videos.Count > 0 ? full.Videos : meta.Videos;
        var complete = full.Videos.Count > 0 ? full.VideosComplete : meta.VideosComplete;
        var previews = meta.Videos.GroupBy(video => (video.Season, video.Episode)).ToDictionary(group => group.Key, group => group.First());
        foreach (var entry in videos.Where(video => video.Season is >= 0 and <= 9999 && video.Episode is >= 1 and <= 9999).Take(maxEpisodes))
        {
            var video = MergeVideo(entry, previews.GetValueOrDefault((entry.Season, entry.Episode)));
            var key = $"{contentKey}:{video.Season}:{video.Episode}";
            var candidate = metadata with
            {
                Key = key,
                EpisodeProviderIds = video.ProviderIds,
                VideoId = video.Id,
                Name = Prefer(video.Name, candidates.GetValueOrDefault(key)?.Name ?? desired.GetValueOrDefault(key)?.Name) ?? $"Episode {video.Episode}",
                SeriesName = title,
                Season = video.Season,
                Episode = video.Episode,
                SeasonPosterUrl = Prefer(full.SeasonPosters.ElementAtOrDefault(video.Season!.Value), meta.SeasonPosters.ElementAtOrDefault(video.Season.Value)),
                Description = video.Description,
                SeriesDescription = metadata.Description,
                SeriesReleased = metadata.Released,
                Released = video.Released,
                ThumbnailUrl = video.Thumbnail,
                EpisodeBackdropUrl = video.Background,
                EpisodeLogoUrl = video.Logo,
                EpisodeCommunityRating = video.CommunityRating,
                EpisodeRunTimeTicks = video.RunTimeTicks,
                EpisodeOfficialRating = video.OfficialRating,
                EpisodeGenres = video.Genres,
                EpisodeProductionLocations = video.ProductionLocations,
                EpisodePeople = video.People,
                Path = VirtualPath(mediaKind, key),
                StreamIdentities = [new StreamIdentity(meta.Type, video.Id)]
            };
            candidates[key] = MergeMetadata(candidate, candidates.GetValueOrDefault(key));
        }
        return (complete, videos.Count > maxEpisodes);
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
                if (meta is not null && meta.Id == preview.Id && meta.Type == preview.Type
                    && (!requiresVideos || meta.Videos.Count > 0 || preview.Videos.Count > 0))
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

    private static string? Prefer(string? value, string? fallback)
        => string.IsNullOrWhiteSpace(value) ? (string.IsNullOrWhiteSpace(fallback) ? null : fallback) : value;

    private static string[] MergeValues(IEnumerable<string> values, IEnumerable<string> fallback)
        => values.Concat(fallback).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Take(256).ToArray();

    private static ManagedPerson[] MergePeople(ManagedPerson[] values, ManagedPerson[] fallback)
    {
        if (fallback.Length == 0) return values;
        if (values.Length == 0) return fallback;
        // Preserve prior indices: native person images may retain a signed credit selector.
        var result = fallback.ToList();
        foreach (var person in values)
        {
            var index = result.FindIndex(existing => existing.Type == person.Type && existing.Name.Equals(person.Name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                result[index] = result[index] with
                {
                    Role = Prefer(person.Role, result[index].Role),
                    PhotoUrl = Prefer(person.PhotoUrl, result[index].PhotoUrl)
                };
            }
            else if (result.Count < 256) result.Add(person);
        }
        return result.ToArray();
    }

    private static StremioVideo MergeVideo(StremioVideo video, StremioVideo? preview)
    {
        if (preview is null || ReferenceEquals(video, preview)) return video;
        return video with
        {
            ProviderIds = MergeProviderIds(video.ProviderIds, preview.ProviderIds),
            Name = Prefer(video.Name, preview.Name) ?? "",
            Description = Prefer(video.Description, preview.Description),
            Released = Prefer(video.Released, preview.Released),
            Thumbnail = Prefer(video.Thumbnail, preview.Thumbnail),
            Background = Prefer(video.Background, preview.Background),
            Logo = Prefer(video.Logo, preview.Logo),
            CommunityRating = video.CommunityRating ?? preview.CommunityRating,
            RunTimeTicks = video.RunTimeTicks ?? preview.RunTimeTicks,
            OfficialRating = Prefer(video.OfficialRating, preview.OfficialRating),
            Genres = MergeValues(video.Genres, preview.Genres),
            ProductionLocations = MergeValues(video.ProductionLocations, preview.ProductionLocations),
            People = MergePeople(video.People, preview.People)
        };
    }

    private static ManagedItem MergeMetadata(ManagedItem item, ManagedItem? previous)
    {
        if (previous is null) return item;
        return item with
        {
            ProviderIds = MergeProviderIds(item.ProviderIds, previous.ProviderIds),
            EpisodeProviderIds = MergeProviderIds(item.EpisodeProviderIds, previous.EpisodeProviderIds),
            Name = Prefer(item.Name, previous.Name)!,
            SeriesName = Prefer(item.SeriesName, previous.SeriesName),
            Year = item.Year ?? previous.Year,
            Description = Prefer(item.Description, previous.Description),
            SeriesDescription = Prefer(item.SeriesDescription, previous.SeriesDescription),
            Released = Prefer(item.Released, previous.Released),
            SeriesReleased = Prefer(item.SeriesReleased, previous.SeriesReleased),
            PosterUrl = Prefer(item.PosterUrl, previous.PosterUrl),
            BackdropUrl = Prefer(item.BackdropUrl, previous.BackdropUrl),
            SeasonPosterUrl = Prefer(item.SeasonPosterUrl, previous.SeasonPosterUrl),
            LogoUrl = Prefer(item.LogoUrl, previous.LogoUrl),
            ThumbnailUrl = Prefer(item.ThumbnailUrl, previous.ThumbnailUrl),
            CommunityRating = item.CommunityRating ?? previous.CommunityRating,
            RunTimeTicks = item.RunTimeTicks ?? previous.RunTimeTicks,
            OfficialRating = Prefer(item.OfficialRating, previous.OfficialRating),
            SeriesStatus = Prefer(item.SeriesStatus, previous.SeriesStatus),
            Genres = MergeValues(item.Genres, previous.Genres),
            ProductionLocations = MergeValues(item.ProductionLocations, previous.ProductionLocations),
            People = MergePeople(item.People, previous.People),
            EpisodeCommunityRating = item.EpisodeCommunityRating ?? previous.EpisodeCommunityRating,
            EpisodeRunTimeTicks = item.EpisodeRunTimeTicks ?? previous.EpisodeRunTimeTicks,
            EpisodeOfficialRating = Prefer(item.EpisodeOfficialRating, previous.EpisodeOfficialRating),
            EpisodeBackdropUrl = Prefer(item.EpisodeBackdropUrl, previous.EpisodeBackdropUrl),
            EpisodeLogoUrl = Prefer(item.EpisodeLogoUrl, previous.EpisodeLogoUrl),
            EpisodeGenres = MergeValues(item.EpisodeGenres, previous.EpisodeGenres),
            EpisodeProductionLocations = MergeValues(item.EpisodeProductionLocations, previous.EpisodeProductionLocations),
            EpisodePeople = MergePeople(item.EpisodePeople, previous.EpisodePeople)
        };
    }

    private static Dictionary<string, string> MergeProviderIds(Dictionary<string, string> values, Dictionary<string, string> fallback)
    {
        if (fallback.Count == 0) return values;
        var result = new Dictionary<string, string>(fallback, StringComparer.OrdinalIgnoreCase);
        foreach (var (provider, id) in values) result[provider] = id;
        return result;
    }


    private static int? GetYear(StremioMeta meta) => ParseYear(meta.ReleaseInfo) ?? ParseYear(meta.Year) ?? ParseYear(meta.Released);

    private static int? ParseYear(string? text)
        => text is { Length: >= 4 } && int.TryParse(text.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && year is >= 1800 and <= 2200 ? year : null;
    private static string VirtualPath(string type, string identity)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return "/siphon/items/" + type + "/" + digest;
    }

}
