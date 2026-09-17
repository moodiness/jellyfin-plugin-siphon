using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Protocol;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Siphon.Search;

/// <summary>Bounded native discoveries; only explicit additions and user activity acquire permanent ownership.</summary>
public sealed class AddonSearchService(
    ConfigurationAccessor configuration, AddonRegistry registry, StremioClient client,
    ISiphonStateStore state, LibraryMaterializer materializer, CatalogSyncService sync,
    ILibraryManager library, IUserManager users, IDbContextFactory<JellyfinDbContext> database,
    ILogger<AddonSearchService> logger, Metadata.MetadataEnrichmentService enrichment,
    UserPreferenceStore preferences, Collections.CollectionRetentionService collections) : BackgroundService
{
    private const int MaximumPreviews = 300;
    private readonly SemaphoreSlim _queries = new(4, 4);
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CachedQuery> _cache = new(StringComparer.OrdinalIgnoreCase);
    private sealed record CachedQuery(DateTimeOffset Expires, Task<IReadOnlyList<Discovery>> Task);
    private sealed record Discovery(RegisteredAddon Addon, StremioMeta Meta);

    public async Task<IReadOnlyList<SearchItem>> SearchAsync(SearchProviderQuery query, CancellationToken ct)
    {
        var term = query.SearchTerm.Trim();
        if (term.Length is < 2 or > 160 || query.Limit is <= 0 || query.UserId is not { } userId
            || users.GetUserById(userId) is not { } user || query.ParentId is { } parent && parent != Guid.Empty
            || string.IsNullOrWhiteSpace(configuration.Current.PublicBaseUrl)) return [];
        var types = new[] { "movie", "series" }.Where(type => AddonSearchPolicy.Allows(query, type)).ToArray();
        if (types.Length == 0 || configuration.SynchronizationGate.CurrentCount == 0) return [];
        // Delay is cancellable before any addon request, avoiding requests for abandoned keystrokes.
        await Task.Delay(200, ct).ConfigureAwait(false);
        var discoveries = await DiscoverAsync(term, types, ct).ConfigureAwait(false);
        // Native local search must remain available while a synchronization owns publication.
        if (discoveries.Count == 0 || !await configuration.SynchronizationGate.WaitAsync(0, ct).ConfigureAwait(false)) return [];
        try
        {
            var current = state.GetItems().ToDictionary(item => item.Key, StringComparer.Ordinal);
            var priorByAddon = new Dictionary<string, Dictionary<(string, string), string>>(StringComparer.Ordinal);
            var byContent = current.Values.GroupBy(item => item.ContentKey, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var resultIds = new HashSet<Guid>();
            var changed = new HashSet<string>(StringComparer.Ordinal);
            var previewCount = current.Values.Where(IsTemporary).Select(item => item.ContentKey).Distinct(StringComparer.Ordinal).Count();
            var hideUnreleased = preferences.HidesUnreleased(userId);
            var now = DateTimeOffset.UtcNow;
            foreach (var discovery in discoveries.Take(Math.Clamp(query.Limit ?? 40, 1, 60)))
            {
                ct.ThrowIfCancellationRequested();
                var meta = discovery.Meta;
                var addonId = discovery.Addon.Configuration.Id;
                if (!priorByAddon.TryGetValue(addonId, out var prior))
                {
                    prior = current.Values.Where(item => item.SearchAddonId == addonId
                            || item.Owners.Any(owner => owner.StartsWith(addonId + ":", StringComparison.Ordinal)))
                        .GroupBy(item => (item.Type, item.ContentId))
                        .ToDictionary(group => group.Key, group => group.First().ContentKey);
                    priorByAddon.Add(addonId, prior);
                }
                var ids = ContentIdentity.ProviderIds(meta.Id, meta.ProviderIds);
                var existing = FindAccessible(meta.Type, ids, user);
                if (existing is not null) { resultIds.Add(existing.Id); continue; }
                var contentKey = prior.GetValueOrDefault((meta.Type, meta.Id)) ?? ContentIdentity.Key(meta.Type, meta.Id, discovery.Addon.Configuration.Id, ids);
                var managed = byContent.GetValueOrDefault(contentKey);
                if (managed is not null)
                {
                    // Never move an existing title to a library the current user can access.
                    var native = FindNative(managed);
                    if (native is not null) resultIds.Add(native.Id);
                    else { changed.Add(managed.ContentKey); resultIds.Add(NativeId(managed)); }
                    continue;
                }
                if (previewCount >= MaximumPreviews || current.Count >= SiphonStateStore.MaximumItems) continue;
                var candidates = new Dictionary<string, ManagedItem>(StringComparer.Ordinal);
                CatalogSyncService.ConvertMetadata(meta, meta, discovery.Addon.Configuration.Id,
                    [CatalogLibraryService.PreviewPrefix + meta.Type], prior, candidates, current, 0, allowSeriesPreview: true);
                if (hideUnreleased && candidates.Values.Any(candidate => !AddonSearchPolicy.IsReleased(
                    ItemMetadataMapper.ParseDate(candidate.Type == "series" ? candidate.SeriesReleased : candidate.Released),
                    candidate.Year, now, configuration.Current.UnreleasedBufferDays))) continue;
                foreach (var candidate in candidates.Values)
                {
                    current[candidate.Key] = candidate with
                    {
                        IsSearchPreview = true,
                        PreviewExpiresUtc = DateTimeOffset.UtcNow.AddHours(24),
                        SearchAddonId = discovery.Addon.Configuration.Id,
                        SearchResourceType = meta.Type
                    };
                    byContent[candidate.ContentKey] = current[candidate.Key];
                    changed.Add(candidate.ContentKey);
                    resultIds.Add(NativeId(candidate));
                }
                previewCount++;
            }
            if (changed.Count > 0) await PublishAsync(current.Values.ToArray(), changed, ct).ConfigureAwait(false);
            return Accessible(resultIds, query, user)
                .Where(item => !hideUnreleased || AddonSearchPolicy.IsReleased(item, now, configuration.Current.UnreleasedBufferDays))
                .Select(ToResponse).ToArray();
        }
        finally { configuration.SynchronizationGate.Release(); }
    }

    private async Task<IReadOnlyList<Discovery>> DiscoverAsync(string term, string[] types, CancellationToken ct)
    {
        // Configuration participates in the key without storing or exposing installation URLs.
        var configKey = string.Join('|', configuration.Current.Addons.Where(addon => addon.Enabled).Select(addon =>
            addon.Id + AddonRegistry.Digest(addon.ManifestUrl) + string.Join(';', addon.Catalogs.SelectMany(catalog => catalog.Extras).Select(extra => extra.Name + "=" + extra.Value))));
        var key = AddonRegistry.Digest(configKey) + string.Join(',', types) + term;
        Task<IReadOnlyList<Discovery>> task;
        lock (_cacheLock)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var expired in _cache.Where(entry => entry.Value.Expires <= now || entry.Value.Task.IsFaulted || entry.Value.Task.IsCanceled).Select(entry => entry.Key).ToArray()) _cache.Remove(expired);
            if (_cache.TryGetValue(key, out var cached)) task = cached.Task;
            else
            {
                if (_cache.Count >= 64)
                {
                    var completed = _cache.Where(entry => entry.Value.Task.IsCompleted).MinBy(entry => entry.Value.Expires);
                    if (completed.Key is null) return [];
                    _cache.Remove(completed.Key);
                }
                task = FetchAsync(term, types);
                _cache[key] = new(now.AddSeconds(30), task);
            }
        }
        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Discovery>> FetchAsync(string term, string[] types)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        await _queries.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var addons = await registry.GetEnabledAsync(ct).ConfigureAwait(false);
            var requests = addons.SelectMany(addon => addon.Manifest.Catalogs.Where(catalog => types.Contains(catalog.Type)).SelectMany(catalog =>
            {
                var subscriptions = addon.Configuration.Catalogs.Where(saved => saved.Type == catalog.Type && saved.Id == catalog.Id).Cast<CatalogSubscription?>().ToArray();
                return (subscriptions.Length == 0 ? new CatalogSubscription?[] { null } : subscriptions)
                    .Select(saved => (Addon: addon, Catalog: catalog, Extras: AddonSearchPolicy.Extras(catalog, saved, term)));
            })).Where(request => request.Extras is not null).Take(32).ToArray();
            var output = new IReadOnlyList<Discovery>[requests.Length];
            try
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, requests.Length), new ParallelOptions
                {
                    CancellationToken = ct,
                    MaxDegreeOfParallelism = Math.Clamp(configuration.Current.MaxConcurrentRequests, 1, 8)
                }, async (index, token) =>
                {
                    var request = requests[index];
                    try
                    {
                        var page = await client.GetCatalogAsync(request.Addon.Configuration.ManifestUrl, request.Catalog.Type, request.Catalog.Id, request.Extras, token).ConfigureAwait(false);
                        output[index] = page.Items.Where(meta => meta.Type == request.Catalog.Type && !string.IsNullOrWhiteSpace(meta.Id) && !string.IsNullOrWhiteSpace(meta.Name))
                            .Take(40).Select(meta =>
                            {
                                Metadata.MetadataProvenance.ObserveAddon(meta, request.Addon.Configuration.Id);
                                return new Discovery(request.Addon, meta);
                            }).ToArray();
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception) { logger.LogDebug("Siphon search unavailable for installation {InstallationId}", request.Addon.Configuration.Id); output[index] = []; }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // A slow/unavailable installation must not discard completed sibling results.
            }
            return output.SelectMany(items => items ?? []).DistinctBy(item => ContentIdentity.Key(item.Meta.Type, item.Meta.Id, item.Addon.Configuration.Id,
                ContentIdentity.ProviderIds(item.Meta.Id, item.Meta.ProviderIds))).Take(60).ToArray();
        }
        finally { _queries.Release(); }
    }

    public async Task<SearchItem?> AddAsync(Guid id, User user, CancellationToken ct)
    {
        if (!IsAccessible(id, user)) return null;
        await ExpandAsync(id, user, true, ct).ConfigureAwait(false);
        var item = library.GetItemById(id);
        return item is null ? null : ToResponse(item);
    }

    public async Task ExpandAsync(Guid id, User user, bool permanent, CancellationToken ct)
    {
        if (!IsAccessible(id, user)) return;
        var native = library.GetItemById(id);
        var key = native?.GetProviderId("Siphon");
        var initial = key is null ? null : state.FindByContentKey(key) ?? state.FindByKey(key);
        if (initial is null) return; // Personal media is never adopted.
        var metadataAddonId = configuration.Current.MetadataAddonId;
        var needsMetadata = initial.Type == "series" && initial.Season is null
            || initial.SearchAddonId is not null && initial.SearchMetadataAddonId != metadataAddonId;
        if (!permanent && !needsMetadata) return;
        if (!await configuration.SynchronizationGate.WaitAsync(0, ct).ConfigureAwait(false))
            throw new SearchAdmissionException("Busy", "Siphon is synchronizing. Retry this title when synchronization completes.");
        try
        {
            var current = state.GetItems().ToDictionary(item => item.Key, StringComparer.Ordinal);
            var content = current.Values.Where(item => item.ContentKey == initial.ContentKey).ToArray();
            if (content.Length == 0) return;
            var first = content[0];
            var shell = content.All(item => item.Type == "series" && item.Season is null);
            needsMetadata = shell || first.SearchAddonId is not null && first.SearchMetadataAddonId != metadataAddonId;
            if (!permanent && !needsMetadata) return;
            var owners = content.SelectMany(item => item.Owners).Distinct(StringComparer.Ordinal).ToArray();
            if (permanent) owners = owners.Where(owner => !owner.StartsWith(CatalogLibraryService.PreviewPrefix, StringComparison.Ordinal))
                .Append(CatalogLibraryService.ManualPrefix + first.Type).Distinct(StringComparer.Ordinal).ToArray();
            var candidates = new Dictionary<string, ManagedItem>(StringComparer.Ordinal);
            if (needsMetadata)
            {
                var addons = await registry.GetEnabledAsync(ct).ConfigureAwait(false);
                var origin = addons.FirstOrDefault(addon => addon.Configuration.Id == first.SearchAddonId)
                    ?? addons.FirstOrDefault(addon => AddonRegistry.Supports(addon.Manifest, "meta", first.SearchResourceType ?? first.Type, first.ContentId))
                    ?? throw new InvalidOperationException("No enabled addon can load this discovery.");
                var preview = new StremioMeta
                {
                    Id = first.ContentId,
                    Type = first.SearchResourceType ?? first.Type,
                    Name = first.SeriesName ?? first.Name,
                    ProviderIds = first.ProviderIds,
                    Poster = first.PosterUrl,
                    Background = first.BackdropUrl,
                    Description = first.SeriesDescription ?? first.Description
                };
                var full = await sync.GetMetadataAsync(preview, origin, addons, [], first.Type == "series", ct).ConfigureAwait(false);
                var prior = new Dictionary<(string, string), string> { [(first.Type, first.ContentId)] = first.ContentKey };
                CatalogSyncService.ConvertMetadata(preview, full, origin.Configuration.Id, owners, prior, candidates, current,
                    Math.Clamp(configuration.Current.MaxEpisodesPerSeries, 1, 10000), content.Where(item => item.Season.HasValue),
                    authoritativeMetadata: !string.IsNullOrEmpty(metadataAddonId));
                if (candidates.Count == 0) throw new InvalidOperationException("The addon returned no usable episodes for this title.");
                if (current.Count - content.Length + candidates.Count > SiphonStateStore.MaximumItems)
                    throw new SearchAdmissionException("LibraryLimit", "Siphon has reached its 100,000-item limit. Reduce catalog or episode limits before adding this title.");
                foreach (var item in content) current.Remove(item.Key);
            }
            else foreach (var item in content) candidates[item.Key] = item;
            var detailed = needsMetadata ? await enrichment.EnrichAsync(candidates.Values.ToArray(), ct).ConfigureAwait(false)
                : candidates.Values.ToArray();
            foreach (var item in detailed)
            {
                current[item.Key] = CatalogSyncService.MergeMetadata(item, content.FirstOrDefault(old => old.Key == item.Key), !string.IsNullOrEmpty(metadataAddonId)) with
                {
                    Owners = owners,
                    IsSearchPreview = !permanent && IsTemporary(first),
                    PreviewExpiresUtc = permanent || !IsTemporary(first) ? null : first.PreviewExpiresUtc,
                    SearchAddonId = first.SearchAddonId,
                    SearchResourceType = first.SearchResourceType,
                    SearchMetadataAddonId = metadataAddonId,
                    MissingSinceUtc = null,
                    MissingOwners = []
                };
            }
            await PublishAsync(current.Values.ToArray(), new HashSet<string>(StringComparer.Ordinal) { first.ContentKey }, ct).ConfigureAwait(false);
        }
        finally { configuration.SynchronizationGate.Release(); }
    }

    public IReadOnlyList<SearchItem> GetAdded(User user)
    {
        var keys = state.GetItems().Where(item => item.Owners.Any(owner => owner.StartsWith(CatalogLibraryService.ManualPrefix, StringComparison.Ordinal)))
            .Select(item => item.ContentKey).Distinct(StringComparer.Ordinal).ToArray();
        if (keys.Length == 0) return [];
        var query = new InternalItemsQuery(user) { IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series], HasAnyProviderIds = new() { ["Siphon"] = keys }, Recursive = true };
        library.ConfigureUserAccess(query, user);
        return library.GetItemList(query).Select(ToResponse).ToArray();
    }

    public bool IsAccessible(Guid id, User user)
        => Accessible([id], new SearchProviderQuery { SearchTerm = "", UserId = user.Id }, user).Count > 0;

    private IReadOnlyList<BaseItem> Accessible(IEnumerable<Guid> ids, SearchProviderQuery query, User user)
    {
        var wanted = ids.ToHashSet();
        if (wanted.Count == 0) return [];
        var filter = new InternalItemsQuery(user)
        {
            IncludeItemTypes = query.IncludeItemTypes.Length > 0 ? query.IncludeItemTypes : [BaseItemKind.Movie, BaseItemKind.Series],
            ExcludeItemTypes = query.IncludeItemTypes.Length > 0 ? [] : query.ExcludeItemTypes,
            MediaTypes = query.MediaTypes,
            Recursive = true,
            ParentId = query.ParentId ?? Guid.Empty
        };
        library.ConfigureUserAccess(filter, user);
        // Configure access before assigning candidate IDs; native query construction skips
        // TopParentIds if ItemIds was already populated.
        filter.ItemIds = wanted.ToArray();
        return library.GetItemList(filter).Where(item => wanted.Contains(item.Id)).ToArray();
    }

    private BaseItem? FindAccessible(string type, Dictionary<string, string> ids, User user)
    {
        if (ids.Count == 0) return null;
        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [type == "movie" ? BaseItemKind.Movie : BaseItemKind.Series],
            HasAnyProviderId = ids,
            Recursive = true,
            Limit = 1
        };
        library.ConfigureUserAccess(query, user);
        return library.GetItemList(query).FirstOrDefault();
    }

    private Guid NativeId(ManagedItem item) => library.GetNewItemId(item.Type == "series" ? "siphon:series:" + item.ContentKey : "siphon:media:" + item.Key,
        item.Type == "series" ? typeof(Series) : typeof(Movie));
    private BaseItem? FindNative(ManagedItem item) => library.GetItemById(NativeId(item));
    private SearchItem ToResponse(BaseItem item)
    {
        var key = item.GetProviderId("Siphon");
        var managed = key is null ? null : state.FindByContentKey(key) ?? state.FindByKey(key);
        var added = managed?.Owners.Any(owner => owner.StartsWith(CatalogLibraryService.ManualPrefix, StringComparison.Ordinal)) == true;
        return new(item.Id, item.Name, item is Series ? "Series" : "Movie", item.ProductionYear, managed is not null && !added, added);
    }
    private static bool IsTemporary(ManagedItem item) => item.Owners.Length > 0 && item.Owners.All(owner => owner.StartsWith(CatalogLibraryService.PreviewPrefix, StringComparison.Ordinal));

    private async Task PublishAsync(IReadOnlyList<ManagedItem> retained, IReadOnlySet<string>? scope, CancellationToken ct)
    {
        if (retained.Count > SiphonStateStore.MaximumItems)
            throw new SearchAdmissionException("LibraryLimit", "Siphon has reached its 100,000-item limit. Reduce catalog or episode limits before adding this title.");
        await configuration.MutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previous = state.GetItems();
            await state.SaveAsync(retained, ct).ConfigureAwait(false);
            await materializer.ApplyAsync(retained, ct, catalogNames: new Dictionary<string, string>(),
                scope: scope is null ? null : new LibraryMaterializer.PublicationScope(scope, previous),
                previousItems: previous).ConfigureAwait(false);
        }
        finally { configuration.MutationGate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await MaintainAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Siphon search discovery maintenance could not complete; discoveries were retained."); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task MaintainAsync(CancellationToken ct)
    {
        await configuration.SynchronizationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = state.GetItems();
            var previews = current.Where(IsTemporary).ToArray();
            if (previews.Length == 0) return;
            var protectedKeys = new HashSet<string>(StringComparer.Ordinal);
            await using (var context = await database.CreateDbContextAsync(ct).ConfigureAwait(false))
            {
                foreach (var keys in previews.SelectMany(item => new[] { item.Key, item.ContentKey, item.ContentKey + ":season:" + (item.Season ?? 0) }).Distinct(StringComparer.Ordinal).Chunk(256))
                {
                    var rows = await context.UserData.AsNoTracking().Where(data => data.Item != null && (data.IsFavorite || data.Played || data.PlayCount > 0 || data.PlaybackPositionTicks > 0)
                        && data.Item.Provider!.Any(provider => provider.ProviderId == "Siphon" && keys.Contains(provider.ProviderValue)))
                        .SelectMany(data => data.Item!.Provider!.Where(provider => provider.ProviderId == "Siphon" && keys.Contains(provider.ProviderValue)),
                            (data, provider) => provider.ProviderValue).ToArrayAsync(ct).ConfigureAwait(false);
                    protectedKeys.UnionWith(rows);
                }
            }
            var protectedContent = previews.Where(item => protectedKeys.Contains(item.Key) || protectedKeys.Contains(item.ContentKey)
                || protectedKeys.Contains(item.ContentKey + ":season:" + (item.Season ?? 0))).Select(item => item.ContentKey).ToHashSet(StringComparer.Ordinal);
            protectedContent.UnionWith(collections.GetProtectedContentKeys());
            protectedContent.IntersectWith(previews.Select(item => item.ContentKey));
            var now = DateTimeOffset.UtcNow;
            var retained = current.Where(item => !IsTemporary(item) || protectedContent.Contains(item.ContentKey) || item.PreviewExpiresUtc > now)
                .Select(item => IsTemporary(item) && protectedContent.Contains(item.ContentKey) ? item with
                {
                    Owners = [CatalogLibraryService.ManualPrefix + item.Type],
                    PreviewExpiresUtc = null,
                    IsSearchPreview = item.Type == "series" && item.Season is null
                } : item).ToArray();
            if (retained.Length != current.Count || protectedContent.Count > 0) await PublishAsync(retained, null, ct).ConfigureAwait(false);
        }
        finally { configuration.SynchronizationGate.Release(); }
    }
}

public sealed record SearchItem(Guid Id, string Name, string Type, int? Year, bool CanAdd, bool IsAdded);
public sealed record SearchItems(IReadOnlyList<SearchItem> Items, int TotalRecordCount);

internal sealed class SearchAdmissionException(string code, string message) : InvalidOperationException(message)
{
    internal string Code { get; } = code;
}
