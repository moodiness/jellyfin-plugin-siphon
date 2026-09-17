using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Metadata;
using Jellyfin.Plugin.Siphon.Protocol;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Siphon.Identity;

public enum SyncKind { Catalogs, FullRefresh, FollowedSeries, Targeted }

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


    public async Task SynchronizeAsync(IProgress<double> progress, CancellationToken cancellationToken, SyncKind kind = SyncKind.Catalogs, SyncTarget? target = null)
    {
        // Native task schedules can overlap. Wait without replacing the active run's diagnostics.
        await configuration.SynchronizationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        string[] diagnosticAddons = [];
        var diagnosticErrors = new Dictionary<string, string>(StringComparer.Ordinal);
        var catalogsCompleted = false;
        var runState = "Failed";
        try
        {
            diagnostics.BeginRun(kind.ToString(), target?.CatalogKey is not null ? "Catalog" : target?.ContentKey is not null ? "Series" : kind == SyncKind.FollowedSeries ? "FollowedSeries" : "All",
                target?.CatalogKey ?? target?.ContentKey);
            diagnostics.ReportStage("Preparing", "Items", 0, 0);
            var config = configuration.Current;
            var previous = state.GetItems().ToDictionary(item => item.Key, StringComparer.Ordinal);
            if (kind == SyncKind.Targeted && target is null)
                throw new ArgumentException("Select a catalog or series before running a targeted synchronization.");
            target?.Validate(config, previous.Values);
            if (target?.ContentKey is { } contentKey)
            {
                await SynchronizeFollowedAsync(previous, new HashSet<string>(StringComparer.Ordinal) { contentKey }, progress,
                    cancellationToken, kind, forceRefresh: false).ConfigureAwait(false);
                runState = "Completed";
                return;
            }
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
            var allSubscriptions = config.Addons.Where(a => a.Enabled)
                .SelectMany(a => a.Catalogs.Where(c => c.Enabled).Select(c => (Addon: a, Catalog: c))).ToArray();
            var configuredSubscriptions = target?.CatalogKey is { } catalogKey
                ? allSubscriptions.Where(s => SyncTarget.CatalogIdentity(s.Addon.Id, s.Catalog.Key) == catalogKey).ToArray()
                : allSubscriptions;
            diagnosticAddons = configuredSubscriptions.Select(subscription => subscription.Addon.Id).Distinct(StringComparer.Ordinal).ToArray();
            if (configuredSubscriptions.Length == 0 && previous.Count == 0)
            {
                progress.Report(100);
                diagnostics.SetSummary(0, 0, 0, 0, 0, 0);
                runState = "Completed";
                return;
            }

            cleanup.BeginSynchronization();
            var desired = new Dictionary<string, ManagedItem>(previous, StringComparer.Ordinal);
            const string manualSeriesOwner = CatalogLibraryService.ManualPrefix + "series";
            var savedSeries = previous.Values.Where(item => item.Type == "series" && item.Owners.Contains(manualSeriesOwner, StringComparer.Ordinal))
                .Select(item => item.ContentKey).ToHashSet(StringComparer.Ordinal);
            var activeOwners = allSubscriptions.Select(s => s.Addon.Id + ":" + s.Catalog.Key).ToHashSet(StringComparer.Ordinal);
            var selectedOwners = configuredSubscriptions.Select(s => s.Addon.Id + ":" + s.Catalog.Key).ToHashSet(StringComparer.Ordinal);
            var scopedTitles = target is null ? null : previous.Values.Where(item => item.Owners.Any(selectedOwners.Contains))
                .Select(item => item.ContentKey).ToHashSet(StringComparer.Ordinal);
            bool InScope(ManagedItem item) => target is null || item.Owners.Any(selectedOwners.Contains);
            foreach (var item in previous.Values.Where(InScope))
                diagnostics.RecordDecision(item.ContentKey, item.SeriesName ?? item.Name, "Pending", "Scheduled");
            var addons = await registry.GetEnabledAsync(cancellationToken).ConfigureAwait(false);
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
                    var priorContentKeys = previous.Values.Where(item => item.Owners.Contains(owner, StringComparer.Ordinal)
                            || item.SearchAddonId == addonConfig.Id)
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

                            MetadataProvenance.ObserveAddon(meta, addonConfig.Id);
                            var requiresVideos = meta.Type == "series"
                                || (meta.Type == "anime" && priorContentKeys.ContainsKey(("series", meta.Id)));
                            var full = await GetMetadataAsync(meta, addon, addons, metaCache, requiresVideos, cancellationToken).ConfigureAwait(false);
                            var conversion = ConvertMetadata(meta, full, addonConfig.Id, [owner], priorContentKeys,
                                candidates, desired, config.MaxEpisodesPerSeries, authoritativeMetadata: !string.IsNullOrEmpty(config.MetadataAddonId));
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
                        foreach (var item in previous.Values.Where(item => item.Owners.Contains(owner, StringComparer.Ordinal)))
                            diagnostics.RecordDecision(item.ContentKey, item.SeriesName ?? item.Name, "Preserved", "IncompleteCatalog");
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
                        scopedTitles?.Add(candidate.ContentKey);
                        diagnostics.RecordDecision(candidate.ContentKey, candidate.SeriesName ?? candidate.Name, "Pending", "Discovered");
                        var existing = desired.GetValueOrDefault(candidate.Key);
                        if (candidate.Type == "series" && candidate.Key != candidate.ContentKey
                            && desired.TryGetValue(candidate.ContentKey, out var shell) && shell.IsSearchPreview && shell.Season is null)
                            desired.Remove(shell.Key);
                        desired[candidate.Key] = MergeMetadata(candidate, existing, !string.IsNullOrEmpty(config.MetadataAddonId)) with
                        {
                            Path = existing?.Path ?? candidate.Path,
                            StreamIdentities = (existing?.StreamIdentities ?? []).Concat(candidate.StreamIdentities).Distinct().ToArray(),
                            MissingSinceUtc = null,
                            SearchMetadataAddonId = existing?.SearchAddonId is not null ? config.MetadataAddonId : null,
                            MissingOwners = (existing?.MissingOwners ?? []).Where(value => value != owner && !value.StartsWith("siphon:preview:", StringComparison.Ordinal)).ToArray(),
                            Owners = (existing?.Owners ?? []).Where(value => !value.StartsWith("siphon:preview:", StringComparison.Ordinal))
                                .Concat(candidate.Type == "series" && savedSeries.Contains(candidate.ContentKey) ? [manualSeriesOwner] : Array.Empty<string>())
                                .Append(owner).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
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
                    foreach (var item in previous.Values.Where(item => item.Owners.Contains(owner, StringComparer.Ordinal)))
                        diagnostics.RecordDecision(item.ContentKey, item.SeriesName ?? item.Name, "Preserved", "CatalogUnavailable");
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
            if (desired.Count > SiphonStateStore.MaximumItems)
            {
                throw new InvalidOperationException("Siphon is limited to 100,000 managed items. Reduce catalog and episode limits before synchronizing.");
            }

            var now = DateTimeOffset.UtcNow;
            var snapshot = desired.Values.Select(item => !InScope(item) ? item : item with
            {
                MissingSinceUtc = item.Owners.Length > 0
                    && item.Owners.All(owner => activeOwners.Contains(owner) && item.MissingOwners.Contains(owner, StringComparer.Ordinal))
                    ? item.MissingSinceUtc ?? now : null,
                MissingOwners = target is not null ? item.MissingOwners : item.MissingOwners.Where(owner => activeOwners.Contains(owner)
                    || owner.StartsWith("siphon:manual:", StringComparison.Ordinal) || owner.StartsWith("siphon:preview:", StringComparison.Ordinal)).ToArray()
            }).ToArray();
            progress.Report(70);
            IReadOnlyList<ManagedItem> retained;
            if (scopedTitles is null) retained = await cleanup.RetainAsync(snapshot, cancellationToken).ConfigureAwait(false);
            else
            {
                var selected = await cleanup.RetainAsync(snapshot.Where(InScope).ToArray(), cancellationToken).ConfigureAwait(false);
                retained = snapshot.Where(item => !InScope(item)).Concat(selected).ToArray();
            }
            progress.Report(71);
            retained = retained.OrderByDescending(item => priority.Contains(item.ContentKey)).ToArray();
            var metadataItems = retained.Where(item => !item.IsSearchPreview && InScope(item)).ToArray();
            diagnostics.ReportStage("Metadata", "Titles", 0, metadataItems.Select(item => item.ContentKey).Distinct(StringComparer.Ordinal).Count());
            var enriched = await enrichment.EnrichAsync(metadataItems, cancellationToken, new ProgressRange(progress, 71, 85),
                forceRefresh: kind == SyncKind.FullRefresh).ConfigureAwait(false);
            retained = retained.Where(item => item.IsSearchPreview || !InScope(item)).Concat(enriched).ToArray();
            await PublishAsync(previous, retained, catalogNames, progress, cancellationToken, kind,
                retained.Count(item => preserved.Contains(item.Key)), failures, scopedTitles).ConfigureAwait(false);
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
        IProgress<double> progress, CancellationToken ct, SyncKind kind = SyncKind.FollowedSeries, bool forceRefresh = true)
    {
        if (priority.Count == 0)
        {
            diagnostics.SetSummary(0, 0, 0, 0, 0, 0);
            progress.Report(100);
            return;
        }

        var config = configuration.Current;
        var desired = new Dictionary<string, ManagedItem>(previous, StringComparer.Ordinal);
        var groups = previous.Values.Where(item => item.Type == "series" && !item.IsSearchPreview && priority.Contains(item.ContentKey))
            .GroupBy(item => item.ContentKey).ToArray();
        foreach (var group in groups)
        {
            var item = group.First();
            diagnostics.RecordDecision(item.ContentKey, item.SeriesName ?? item.Name, "Pending", "SeriesRefresh");
        }
        var addons = await registry.GetEnabledAsync(ct).ConfigureAwait(false);
        var refreshed = new HashSet<string>(StringComparer.Ordinal);
        var preserved = 0;
        var failures = 0;
        diagnostics.ReportStage("Catalogs", "Series", 0, groups.Length);
        for (var index = 0; index < groups.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var group = groups[index];
            var existing = group.First();
            diagnostics.RecordDecision(existing.ContentKey, existing.SeriesName ?? existing.Name, "Pending", "SeriesRefresh");
            try
            {
                var owners = group.SelectMany(item => item.Owners).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var origin = addons.FirstOrDefault(addon => addon.Configuration.Id == existing.SearchAddonId)
                    ?? addons.FirstOrDefault(addon => owners.Any(owner => owner.StartsWith(addon.Configuration.Id + ":", StringComparison.Ordinal)))
                    ?? addons.FirstOrDefault(addon => owners.Any(owner => owner.StartsWith("siphon:manual:", StringComparison.Ordinal))
                        && AddonRegistry.Supports(addon.Manifest, "meta", existing.SearchResourceType ?? existing.StreamIdentities.FirstOrDefault()?.Type ?? "series", existing.ContentId))
                    ?? throw new InvalidOperationException("The series origin is unavailable.");
                var type = existing.SearchResourceType
                    ?? origin.Configuration.Catalogs.FirstOrDefault(catalog => owners.Contains(origin.Configuration.Id + ":" + catalog.Key, StringComparer.Ordinal))?.Type
                    ?? existing.StreamIdentities.FirstOrDefault()?.Type ?? "series";
                var preview = new StremioMeta
                {
                    Id = existing.ContentId,
                    Type = type,
                    Name = existing.SeriesName ?? existing.Name,
                    ProviderIds = existing.ProviderIds
                };
                var full = await GetMetadataAsync(preview, origin, addons, new Dictionary<string, StremioMeta>(StringComparer.Ordinal), true, ct).ConfigureAwait(false);
                // Unlike catalog snapshots, a targeted refresh has no authority to
                // establish absence, and incomplete results cannot replace old data.
                if (!full.VideosComplete || full.Videos.Count == 0)
                    throw new InvalidOperationException("The series metadata is incomplete.");
                var candidates = new Dictionary<string, ManagedItem>(StringComparer.Ordinal);
                ConvertMetadata(preview, full, origin.Configuration.Id, owners,
                    new Dictionary<(string, string), string> { [("series", existing.ContentId)] = existing.ContentKey },
                    candidates, desired, config.MaxEpisodesPerSeries, group, authoritativeMetadata: !string.IsNullOrEmpty(config.MetadataAddonId));
                foreach (var candidate in candidates.Values)
                {
                    var prior = desired.GetValueOrDefault(candidate.Key);
                    desired[candidate.Key] = MergeMetadata(candidate, prior, !string.IsNullOrEmpty(config.MetadataAddonId)) with
                    {
                        Path = prior?.Path ?? candidate.Path,
                        Owners = prior?.Owners ?? owners,
                        SearchAddonId = prior?.SearchAddonId ?? existing.SearchAddonId,
                        SearchResourceType = prior?.SearchResourceType ?? existing.SearchResourceType,
                        SearchMetadataAddonId = (prior?.SearchAddonId ?? existing.SearchAddonId) is not null ? config.MetadataAddonId : null,
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
                diagnostics.RecordDecision(existing.ContentKey, existing.SeriesName ?? existing.Name, "Preserved", "MetadataUnavailable");
                logger.LogWarning("Siphon retained followed series after metadata failure: {ErrorType}", exception.GetType().Name);
            }
            diagnostics.ReportStage("Catalogs", "Series", index + 1, groups.Length);
            progress.Report(70d * (index + 1) / groups.Length);
        }

        if (desired.Count > SiphonStateStore.MaximumItems)
            throw new InvalidOperationException("Siphon is limited to 100,000 managed items.");
        if (refreshed.Count > 0)
        {
            cleanup.BeginSynchronization();
            var selected = desired.Values.Where(item => refreshed.Contains(item.ContentKey)).ToArray();
            diagnostics.ReportStage("Metadata", "Titles", 0, refreshed.Count);
            var enriched = await enrichment.EnrichAsync(selected, ct, new ProgressRange(progress, 71, 85), forceRefresh: forceRefresh).ConfigureAwait(false);
            foreach (var item in enriched) desired[item.Key] = item;
            await PublishAsync(previous, desired.Values.ToArray(), null, progress, ct, kind, preserved, failures, refreshed).ConfigureAwait(false);
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
                kind == SyncKind.FullRefresh ? null : changed,
                scopedContentKeys is null ? null : new LibraryMaterializer.PublicationScope(scopedContentKeys, previous.Values.ToArray()),
                previousItems: previous.Values.ToArray()).ConfigureAwait(false);
            foreach (var group in retained.Where(item => scopedContentKeys is null || scopedContentKeys.Contains(item.ContentKey)).GroupBy(item => item.ContentKey))
            {
                var first = group.First();
                var action = group.Any(item => !previous.ContainsKey(item.Key)) ? "Added"
                    : group.Any(item => changed.Contains(item.Key)) ? "Updated" : "Unchanged";
                diagnostics.RecordDecision(group.Key, first.SeriesName ?? first.Name, action,
                    action == "Unchanged" ? "NoChanges" : "Published");
                if (group.Any(item => item.MissingSinceUtc.HasValue))
                    diagnostics.RecordDecision(group.Key, first.SeriesName ?? first.Name, "Preserved", "CleanupRetention");
            }
            var retainedTitles = retained.Select(item => item.ContentKey).ToHashSet(StringComparer.Ordinal);
            foreach (var group in previous.Values.Where(item => !retainedKeys.Contains(item.Key)).GroupBy(item => item.ContentKey))
            {
                var first = group.First();
                diagnostics.RecordDecision(group.Key, first.SeriesName ?? first.Name,
                    retainedTitles.Contains(group.Key) ? "Updated" : "Removed",
                    retainedTitles.Contains(group.Key) ? "ItemsRemoved" : "ConfirmedAbsence");
            }
            diagnostics.ReportStage("Finalizing", "Items", retained.Count, retained.Count);
        }
        finally
        {
            configuration.MutationGate.Release();
        }
    }

    internal static (bool Complete, bool Limited) ConvertMetadata(StremioMeta meta, StremioMeta full, string addonId,
        string[] owners, IReadOnlyDictionary<(string, string), string> priorContentKeys,
        Dictionary<string, ManagedItem> candidates, IReadOnlyDictionary<string, ManagedItem> desired, int maxEpisodes,
        IEnumerable<ManagedItem>? preservedEpisodes = null, bool allowSeriesPreview = false, bool authoritativeMetadata = false)
    {
        var fallback = authoritativeMetadata ? full : meta;
        var mediaKind = meta.Type == "anime" && full.Type is "movie" or "series" ? full.Type
            : meta.Type == "movie" || (meta.Type == "anime" && full.Videos.Count == 0 && meta.Videos.Count == 0) ? "movie" : "series";
        var ids = ContentIdentity.ProviderIds(meta.Id, MergeProviderIds(full.ProviderIds, meta.ProviderIds));
        // Richer metadata must not replace the established subscription identity.
        var contentKey = priorContentKeys.GetValueOrDefault((mediaKind, meta.Id))
            ?? ContentIdentity.Key(mediaKind, meta.Id, addonId, ids);
        var title = Prefer(full.Name, fallback.Name)!;
        var metadata = new ManagedItem
        {
            Key = contentKey,
            Type = mediaKind,
            ContentId = meta.Id,
            ContentKey = contentKey,
            ProviderIds = ids,
            VideoId = meta.Id,
            Name = title,
            Year = GetYear(full) ?? GetYear(fallback),
            Description = Prefer(full.Description, fallback.Description),
            PosterUrl = Prefer(full.Poster, fallback.Poster),
            BackdropUrl = Prefer(full.Background, fallback.Background),
            LogoUrl = Prefer(full.Logo, fallback.Logo),
            Released = Prefer(full.Released, fallback.Released),
            Genres = MergeValues(full.Genres, fallback.Genres),
            CommunityRating = full.CommunityRating ?? fallback.CommunityRating,
            RunTimeTicks = full.RunTimeTicks ?? fallback.RunTimeTicks,
            OfficialRating = Prefer(full.OfficialRating, fallback.OfficialRating),
            SeriesStatus = Prefer(full.Status, fallback.Status),
            ProductionLocations = MergeValues(full.ProductionLocations, fallback.ProductionLocations),
            People = MergePeople(full.People, fallback.People),
            Path = VirtualPath(mediaKind, contentKey),
            Owners = owners
        };
        metadata = MetadataProvenance.FromAddon(metadata, full, fallback, meta);
        if (mediaKind == "movie")
        {
            candidates[contentKey] = MergeMetadata(metadata, candidates.GetValueOrDefault(contentKey), authoritativeMetadata) with
            {
                StreamIdentities = ContentIdentity.MovieAliases(meta.Type, meta.Id, ids).ToArray()
            };
            return (true, false);
        }
        if (allowSeriesPreview)
        {
            candidates[contentKey] = MetadataProvenance.CopyParent(metadata with
            {
                SeriesName = title,
                SeriesDescription = metadata.Description,
                IsSearchPreview = true,
                StreamIdentities = [new StreamIdentity(meta.Type, meta.Id)]
            }, metadata);
            return (true, false);
        }
        if (preservedEpisodes is not null)
        {
            // An omitted episode remains playable, but shares the refreshed series
            // metadata. Keep its exact resource identity and all episode-specific data.
            foreach (var episode in preservedEpisodes)
            {
                candidates[episode.Key] = MergeMetadata(MetadataProvenance.CopyParent(episode with
                {
                    ProviderIds = metadata.ProviderIds,
                    SeriesName = title,
                    Year = metadata.Year,
                    SeriesDescription = metadata.Description,
                    SeriesReleased = metadata.Released,
                    PosterUrl = metadata.PosterUrl,
                    BackdropUrl = metadata.BackdropUrl,
                    LogoUrl = metadata.LogoUrl,
                    Genres = metadata.Genres,
                    CommunityRating = metadata.CommunityRating,
                    RunTimeTicks = metadata.RunTimeTicks,
                    OfficialRating = metadata.OfficialRating,
                    SeriesStatus = metadata.SeriesStatus,
                    ProductionLocations = metadata.ProductionLocations,
                    People = metadata.People
                }, metadata), episode, authoritativeMetadata);
            }
        }

        var videos = full.Videos.Count > 0 ? full.Videos : fallback.Videos;
        var complete = full.Videos.Count > 0 ? full.VideosComplete : fallback.VideosComplete;
        var previews = authoritativeMetadata ? null : meta.Videos.GroupBy(video => (video.Season, video.Episode)).ToDictionary(group => group.Key, group => group.First());
        foreach (var entry in videos.Where(video => video.Season is >= 0 and <= 9999 && video.Episode is >= 1 and <= 9999).Take(maxEpisodes))
        {
            var video = MergeVideo(entry, previews?.GetValueOrDefault((entry.Season, entry.Episode)));
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
                StreamIdentities = [new StreamIdentity(full.Type is "movie" or "series" ? full.Type : meta.Type, video.Id)]
            };
            candidate = MetadataProvenance.CopyParent(candidate, metadata);
            candidate = MetadataProvenance.FromEpisode(candidate, entry, previews?.GetValueOrDefault((entry.Season, entry.Episode)),
                full.Videos.Count > 0 ? full : fallback, meta);
            if (string.IsNullOrWhiteSpace(video.Name) && (candidates.GetValueOrDefault(key) ?? desired.GetValueOrDefault(key)) is { } named)
                candidate = MetadataProvenance.Retain(candidate, named, ["Name"], "IncomingValueMissing");
            candidates[key] = MergeMetadata(candidate, candidates.GetValueOrDefault(key), authoritativeMetadata);
        }
        return (complete, videos.Count > maxEpisodes);
    }

    internal async Task<StremioMeta> GetMetadataAsync(StremioMeta preview, RegisteredAddon origin, IReadOnlyList<RegisteredAddon> addons, Dictionary<string, StremioMeta> cache, bool requiresVideos, CancellationToken cancellationToken)
    {
        var selected = configuration.Current.MetadataAddonId;
        var authoritative = !string.IsNullOrEmpty(selected);
        var key = (authoritative ? selected : origin.Configuration.Id) + ":" + preview.Type + ":" + preview.Id;
        if (cache.TryGetValue(key, out var cached)) return cached;

        var providers = authoritative ? addons.Where(addon => addon.Configuration.Id == selected)
            : addons.OrderByDescending(addon => addon.Configuration.Id == origin.Configuration.Id);
        foreach (var provider in providers)
        {
            if (!AddonRegistry.Supports(provider.Manifest, "meta", preview.Type, preview.Id)) continue;
            try
            {
                var meta = await client.GetMetaAsync(provider.Configuration.ManifestUrl, preview.Type, preview.Id, cancellationToken).ConfigureAwait(false);
                if (meta is not null && MetadataIdentityMatches(preview, meta, requiresVideos)
                    && (!requiresVideos || meta.Videos.Count > 0 || (!authoritative && preview.Videos.Count > 0)))
                {
                    MetadataProvenance.ObserveAddon(meta, provider.Configuration.Id, authoritative);
                    cache[key] = meta;
                    return meta;
                }
            }
            catch (StremioException)
            {
                logger.LogWarning("Siphon metadata unavailable from installation {InstallationId}", provider.Configuration.Id);
            }
        }

        if (authoritative)
            throw new InvalidOperationException("The selected metadata addon could not supply matching metadata. Existing title data was retained; no other addon was substituted.");
        if (!requiresVideos || preview.Videos.Count > 0)
        {
            cache[key] = preview;
            return preview;
        }
        throw new InvalidOperationException("No configured metadata addon can expand the series into episodes.");
    }

    private static bool MetadataIdentityMatches(StremioMeta requested, StremioMeta returned, bool requiresVideos)
    {
        if (returned.Type != requested.Type
            && !(requested.Type == "anime" && returned.Type is "movie" or "series")) return false;
        if (requiresVideos && returned.Type == "movie") return false;
        if (returned.Id == requested.Id) return true;
        var expected = ContentIdentity.ProviderIds(requested.Id, requested.ProviderIds);
        var actual = ContentIdentity.ProviderIds(returned.Id, returned.ProviderIds);
        var matched = false;
        foreach (var (provider, id) in expected)
        {
            if (!actual.TryGetValue(provider, out var found)) continue;
            if (!string.Equals(id, found, StringComparison.Ordinal)) return false;
            matched = true;
        }
        return matched;
    }

    private static string? Prefer(string? value, string? fallback)
        => string.IsNullOrWhiteSpace(value) ? (string.IsNullOrWhiteSpace(fallback) ? null : fallback) : value;

    private static string[] MergeValues(IEnumerable<string> values, IEnumerable<string> fallback)
        => values.Concat(fallback).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Take(256).ToArray();

    private static ManagedPerson[] MergePeople(ManagedPerson[] values, ManagedPerson[] fallback)
    {
        if (fallback.Length == 0) return values;
        if (ReferenceEquals(values, fallback)) return values;
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

    internal static ManagedItem MergeMetadata(ManagedItem item, ManagedItem? previous, bool authoritativeMetadata)
    {
        if (previous is null) return item;
        var merged = item with
        {
            ProviderIds = MergeProviderIds(item.ProviderIds, previous.ProviderIds),
            SearchAddonId = item.SearchAddonId ?? previous.SearchAddonId,
            SearchResourceType = item.SearchResourceType ?? previous.SearchResourceType,
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
            Genres = authoritativeMetadata && item.Genres.Length > 0 ? item.Genres : MergeValues(item.Genres, previous.Genres),
            ProductionLocations = authoritativeMetadata && item.ProductionLocations.Length > 0 ? item.ProductionLocations : MergeValues(item.ProductionLocations, previous.ProductionLocations),
            People = authoritativeMetadata && item.People.Length > 0 ? item.People : MergePeople(item.People, previous.People),
            EpisodeCommunityRating = item.EpisodeCommunityRating ?? previous.EpisodeCommunityRating,
            EpisodeRunTimeTicks = item.EpisodeRunTimeTicks ?? previous.EpisodeRunTimeTicks,
            EpisodeOfficialRating = Prefer(item.EpisodeOfficialRating, previous.EpisodeOfficialRating),
            EpisodeBackdropUrl = Prefer(item.EpisodeBackdropUrl, previous.EpisodeBackdropUrl),
            EpisodeLogoUrl = Prefer(item.EpisodeLogoUrl, previous.EpisodeLogoUrl),
            EpisodeGenres = authoritativeMetadata && item.EpisodeGenres.Length > 0 ? item.EpisodeGenres : MergeValues(item.EpisodeGenres, previous.EpisodeGenres),
            EpisodeProductionLocations = authoritativeMetadata && item.EpisodeProductionLocations.Length > 0 ? item.EpisodeProductionLocations : MergeValues(item.EpisodeProductionLocations, previous.EpisodeProductionLocations),
            EpisodePeople = authoritativeMetadata && item.EpisodePeople.Length > 0 ? item.EpisodePeople : MergePeople(item.EpisodePeople, previous.EpisodePeople)
        };
        return MetadataProvenance.Merge(merged, item, previous);
    }

    private static Dictionary<string, string> MergeProviderIds(Dictionary<string, string> values, Dictionary<string, string> fallback)
    {
        if (fallback.Count == 0) return values;
        var result = new Dictionary<string, string>(fallback, StringComparer.OrdinalIgnoreCase);
        foreach (var (provider, id) in values) result[provider] = id;
        return result;
    }


    internal static int? GetYear(StremioMeta meta) => ParseYear(meta.ReleaseInfo) ?? ParseYear(meta.Year) ?? ParseYear(meta.Released);

    private static int? ParseYear(string? text)
        => text is { Length: >= 4 } && int.TryParse(text.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && year is >= 1800 and <= 2200 ? year : null;
    private static string VirtualPath(string type, string identity)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return "/siphon/items/" + type + "/" + digest;
    }

}
