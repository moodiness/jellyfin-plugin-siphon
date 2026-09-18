using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
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
using UserData = Jellyfin.Database.Implementations.Entities.UserData;

namespace Jellyfin.Plugin.Siphon.Recovery;

/// <summary>Explicit, bounded recovery of attributed native orphan history. Never runs automatically.</summary>
public sealed class WatchRecoveryService(
    ConfigurationAccessor configuration, ISiphonStateStore state, RecoveryLedger ledger,
    IDbContextFactory<JellyfinDbContext> database, ILibraryManager library, IUserManager users,
    IUserDataManager userDataManager, AddonRegistry registry, CatalogSyncService sync,
    LibraryMaterializer materializer, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, Grant> _grants = new(StringComparer.Ordinal);

    public async Task<RecoveryPreview> PreviewAsync(Guid administrator, int startIndex, int limit, CancellationToken ct)
    {
        if (administrator == Guid.Empty || startIndex is < 0 or > 100000 || limit is < 1 or > 100)
            throw new ArgumentException("Invalid recovery page.");
        await configuration.SynchronizationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var context = await database.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await context.UserData.AsNoTracking()
                .Where(row => row.ItemId == RecoveryPolicy.PlaceholderId)
                .OrderBy(row => row.UserId).ThenBy(row => row.CustomDataKey)
                .Skip(startIndex).Take(limit + 1).ToArrayAsync(ct).ConfigureAwait(false);
            var history = ledger.Load().ToLookup(entry => (entry.UserId, entry.DataKey));
            var candidates = new List<Candidate>();
            foreach (var row in rows.Take(limit))
            {
                var evidence = history[(row.UserId, row.CustomDataKey ?? "")]
                    .Where(entry => row.RetentionDate >= entry.CapturedAtUtc
                        && row.RetentionDate <= entry.CapturedAtUtc.AddMinutes(5)
                        && entry.Fingerprint == RecoveryPolicy.Fingerprint(row, false))
                    .DistinctBy(entry => (entry.NativeKey, entry.NativeType)).ToArray();
                RecoveryLedger.Entry? identity = evidence.Length == 1 ? evidence[0] : null;
                // A provider key such as imdb is not proof of Siphon ownership. Only an exact
                // native item GUID may recover pre-ledger rows without removal evidence.
                if (identity is null && evidence.Length == 0 && Guid.TryParse(row.CustomDataKey, out var oldId))
                {
                    var existing = library.GetItemById(oldId);
                    var key = existing?.GetProviderId("Siphon");
                    var managed = key is null ? null : state.FindByKey(key) ?? state.FindByContentKey(key);
                    if (existing is Movie or Episode or Series && managed is not null)
                        identity = new(oldId, key!, existing.GetType().Name, managed, row.UserId, row.CustomDataKey!,
                            RecoveryPolicy.Fingerprint(row, false), row.RetentionDate ?? DateTime.MinValue);
                }
                var targets = identity is null ? [] : FindTargets(identity);
                var target = targets.Length == 1 ? targets[0] : null;
                var collision = target is not null && await context.UserData.AsNoTracking()
                    .Where(data => data.ItemId == target.Id && data.UserId == row.UserId)
                    .ToArrayAsync(ct).ConfigureAwait(false) is { } live && live.Any(RecoveryPolicy.Meaningful);
                var reason = identity is null ? evidence.Length > 1 ? "AmbiguousOwnership" : "OwnershipUnknown"
                    : targets.Length > 1 ? "AmbiguousTarget" : collision ? "LiveHistoryPresent" : !RecoveryPolicy.Meaningful(row) ? "EmptyHistory"
                    : target is null && string.IsNullOrEmpty(configuration.Current.MetadataAddonId) ? "SelectMetadataAddon"
                    : "Ready";
                var dto = new RecoveryCandidate(Guid.NewGuid().ToString("N"), row.UserId,
                    identity?.Identity.SeriesName is { } series && identity?.NativeType is "Series" or "Season" ? series : identity?.Identity.Name ?? "Unattributed deleted item",
                    identity?.NativeType ?? "Unknown", identity is null ? "Unattributed" : "ProvenSiphon",
                    reason != "Ready" ? "None" : target is null ? "RestoreContent" : "RestoreHistory", reason,
                    reason == "Ready", row.Played, row.IsFavorite, row.PlaybackPositionTicks, row.PlayCount, row.LastPlayedDate);
                candidates.Add(new(dto, row, RecoveryPolicy.Fingerprint(row), identity, target?.Id));
            }
            var now = _clock.GetUtcNow();
            foreach (var expired in _grants.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray()) _grants.Remove(expired);
            while (_grants.Count >= 16) _grants.Remove(_grants.MinBy(pair => pair.Value.Expires).Key);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var expiry = now.AddMinutes(10);
            _grants[token] = new(administrator, expiry, ConfigurationFingerprint(), candidates.ToDictionary(item => item.Dto.Id, StringComparer.Ordinal));
            return new(token, expiry, startIndex, limit, rows.Length > limit, candidates.Select(item => item.Dto).ToArray());
        }
        finally { configuration.SynchronizationGate.Release(); }
    }

    public async Task<RecoveryExecution> ExecuteAsync(Guid administrator, string token, IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        await configuration.SynchronizationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_grants.TryGetValue(token, out var grant) || grant.Administrator != administrator
                || grant.Expires <= _clock.GetUtcNow() || grant.Configuration != ConfigurationFingerprint())
                throw new InvalidOperationException("Recovery preview expired or changed.");
            if (!RecoveryPolicy.ValidSelection(ids, grant.Items.Where(pair => pair.Value.Dto.CanRestore).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal)))
                throw new ArgumentException("Select only restorable candidates from this preview.");
            _grants.Remove(token); // single use, including partial failures and cancellation
            var results = new List<RecoveryResult>();
            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = grant.Items[id];
                string status;
                try
                {
                    if (grant.Configuration != ConfigurationFingerprint() || !await SourceUnchangedAsync(candidate, ct).ConfigureAwait(false))
                    {
                        results.Add(new(id, "Changed"));
                        continue;
                    }
                    var targets = FindTargets(candidate.Identity!);
                    if (targets.Length > 1) { results.Add(new(id, "Collision")); continue; }
                    var target = targets.SingleOrDefault();
                    if (candidate.TargetId.HasValue && target?.Id != candidate.TargetId)
                    {
                        results.Add(new(id, "Changed"));
                        continue;
                    }
                    if (target is null)
                        target = await RestoreContentAsync(candidate.Identity!, ct).ConfigureAwait(false);
                    if (target is null) status = "MetadataUnavailable";
                    else
                    {
                        await configuration.MutationGate.WaitAsync(ct).ConfigureAwait(false);
                        try { status = await RestoreHistoryAsync(candidate, target, ct).ConfigureAwait(false); }
                        finally { configuration.MutationGate.Release(); }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (InvalidOperationException) { status = "MetadataUnavailable"; }
                catch (Exception) { status = "Failed"; } // Never surface provider URLs or database contents.
                results.Add(new(id, status));
            }
            return new(results);
        }
        finally { configuration.SynchronizationGate.Release(); }
    }

    private async Task<bool> SourceUnchangedAsync(Candidate candidate, CancellationToken ct)
    {
        await using var context = await database.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await context.UserData.AsNoTracking().SingleOrDefaultAsync(data => data.ItemId == RecoveryPolicy.PlaceholderId
            && data.UserId == candidate.Row.UserId && data.CustomDataKey == candidate.Row.CustomDataKey, ct).ConfigureAwait(false);
        return row is not null && RecoveryPolicy.Fingerprint(row) == candidate.Fingerprint;
    }

    private BaseItem[] FindTargets(RecoveryLedger.Entry identity)
    {
        var managed = identity.NativeType is "Series" or "Season"
            ? state.ReadByContentKey(identity.Identity.ContentKey) : state.ReadByKey(identity.Identity.Key);
        if (managed is null || managed.ContentKey != identity.Identity.ContentKey || managed.Type != identity.Identity.Type) return [];
        BaseItemKind? kind = identity.NativeType switch
        {
            "Movie" => BaseItemKind.Movie,
            "Episode" => BaseItemKind.Episode,
            "Series" => BaseItemKind.Series,
            "Season" => BaseItemKind.Season,
            _ => null
        };
        if (!kind.HasValue) return [];
        var original = library.GetItemById(identity.NativeId);
        if (original is not null && original.GetProviderId("Siphon") == identity.NativeKey && original.GetType().Name == identity.NativeType)
            return [original];
        var matches = library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [kind.Value],
            HasAnyProviderId = new() { ["Siphon"] = identity.NativeKey },
            Recursive = true,
            IncludeAlternateVersions = false,
            GroupByPresentationUniqueKey = false,
            Limit = 2
        }).Where(item => item.GetProviderId("Siphon") == identity.NativeKey).ToArray();
        return matches;
    }

    // Upstream requests hold SynchronizationGate but never MutationGate, matching native publication.
    private async Task<BaseItem?> RestoreContentAsync(RecoveryLedger.Entry identity, CancellationToken ct)
    {
        var selected = configuration.Current.MetadataAddonId;
        if (string.IsNullOrEmpty(selected)) return null;
        var item = identity.Identity;
        var addons = await registry.GetEnabledAsync(ct).ConfigureAwait(false);
        var origin = addons.FirstOrDefault(addon => addon.Configuration.Id == selected);
        if (origin is null) return null;
        var preview = new StremioMeta
        {
            Id = item.ContentId,
            Type = item.SearchResourceType ?? item.Type,
            Name = item.SeriesName ?? item.Name,
            ProviderIds = item.ProviderIds
        };
        var full = await sync.GetMetadataAsync(preview, origin, addons, [], item.Type == "series", ct).ConfigureAwait(false);
        var previous = state.GetItems();
        var current = previous.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        var candidates = new Dictionary<string, ManagedItem>(StringComparer.Ordinal);
        CatalogSyncService.ConvertMetadata(preview, full, selected, [CatalogLibraryService.ManualPrefix + item.Type],
            new Dictionary<(string, string), string> { [(item.Type, item.ContentId)] = item.ContentKey }, candidates, current,
            Math.Clamp(configuration.Current.MaxEpisodesPerSeries, 1, 10000), authoritativeMetadata: true);
        var selectedItems = candidates.Values.Where(candidate => identity.NativeType == "Series"
            || identity.NativeType == "Season" && candidate.Season == item.Season
            || identity.NativeType is "Movie" or "Episode" && candidate.Key == item.Key).ToArray();
        if (selectedItems.Length == 0 || current.Count + selectedItems.Count(candidate => !current.ContainsKey(candidate.Key)) > SiphonStateStore.MaximumItems) return null;
        if (identity.NativeType == "Episode" && selectedItems.Any(candidate => item.EpisodeProviderIds.Any(provider =>
            candidate.EpisodeProviderIds.TryGetValue(provider.Key, out var value) && !string.Equals(value, provider.Value, StringComparison.OrdinalIgnoreCase))))
            return null;
        if (item.Type == "series" && current.TryGetValue(item.ContentKey, out var shell) && shell.IsSearchPreview && shell.Season is null)
            current.Remove(item.ContentKey);
        foreach (var candidate in selectedItems)
        {
            // Existing metadata and ownership are retained; restoration adds only absent content.
            var restored = current.GetValueOrDefault(candidate.Key) ?? candidate;
            current[restored.Key] = restored with
            {
                Owners = restored.Owners.Append(CatalogLibraryService.ManualPrefix + restored.Type).Distinct(StringComparer.Ordinal).ToArray(),
                IsSearchPreview = false,
                PreviewExpiresUtc = null,
                MissingSinceUtc = null,
                MissingOwners = []
            };
            // Reject deterministic-ID collisions before any state or native library mutation.
            var expected = library.GetNewItemId("siphon:media:" + restored.Key, restored.Type == "movie" ? typeof(Movie) : typeof(Episode));
            if (library.GetItemById(expected) is { } occupied && occupied.GetProviderId("Siphon") != restored.Key) return null;
            if (restored.Type == "series")
            {
                var seriesId = library.GetNewItemId("siphon:series:" + restored.ContentKey, typeof(Series));
                var seasonKey = restored.ContentKey + ":season:" + (restored.Season ?? 0);
                var seasonId = library.GetNewItemId("siphon:season:" + seasonKey, typeof(Season));
                if (library.GetItemById(seriesId) is { } occupiedSeries && occupiedSeries.GetProviderId("Siphon") != restored.ContentKey
                    || library.GetItemById(seasonId) is { } occupiedSeason && occupiedSeason.GetProviderId("Siphon") != seasonKey) return null;
            }
        }
        await configuration.MutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await state.SaveAsync(current.Values.ToArray(), ct).ConfigureAwait(false);
            await materializer.ApplyAsync(current.Values.ToArray(), ct,
                scope: new LibraryMaterializer.PublicationScope(new HashSet<string>(StringComparer.Ordinal) { item.ContentKey }, previous)).ConfigureAwait(false);
        }
        finally { configuration.MutationGate.Release(); }
        var targets = FindTargets(identity);
        return targets.Length == 1 ? targets[0] : null;
    }

    private async Task<string> RestoreHistoryAsync(Candidate candidate, BaseItem target, CancellationToken ct)
    {
        var user = users.GetUserById(candidate.Row.UserId);
        var targets = FindTargets(candidate.Identity!);
        if (user is null || targets.Length != 1 || targets[0].Id != target.Id) return "Changed";
        var cache = userDataManager.GetUserDataBatch([target], user).GetValueOrDefault(target.Id);
        await using var context = await database.CreateDbContextAsync(ct).ConfigureAwait(false);
        var keys = target.GetUserDataKeys().Where(key => !string.IsNullOrEmpty(key)).Distinct(StringComparer.Ordinal).ToArray();
        var status = await RecoveryHistoryWriter.RestoreAsync(context, candidate.Row.UserId, candidate.Row.CustomDataKey,
            candidate.Fingerprint, target.Id, candidate.Identity!.NativeKey, keys, ct).ConfigureAwait(false);
        if (status != "Restored") return status;
        // Native GetUserData reads BaseItem.UserData. Batch access also retains a mutable DTO cache.
        // Refresh both without SaveUserData's unconditional UPDATE of a concurrently played item.
        var refreshed = await context.UserData.AsNoTracking().Where(row => row.ItemId == target.Id).ToArrayAsync(CancellationToken.None).ConfigureAwait(false);
        target.UserData = refreshed;
        var latest = refreshed.FirstOrDefault(row => row.UserId == user.Id && row.CustomDataKey == keys[0]);
        if (cache is not null && latest is not null && !cache.Played && !cache.IsFavorite
            && cache.PlayCount == 0 && cache.PlaybackPositionTicks == 0 && !cache.LastPlayedDate.HasValue
            && !cache.Rating.HasValue && !cache.Likes.HasValue && !cache.AudioStreamIndex.HasValue && !cache.SubtitleStreamIndex.HasValue)
        {
            cache.Played = latest.Played; cache.IsFavorite = latest.IsFavorite; cache.PlayCount = latest.PlayCount;
            cache.PlaybackPositionTicks = latest.PlaybackPositionTicks; cache.LastPlayedDate = latest.LastPlayedDate;
            cache.Rating = latest.Rating; cache.Likes = latest.Likes; cache.AudioStreamIndex = latest.AudioStreamIndex;
            cache.SubtitleStreamIndex = latest.SubtitleStreamIndex;
        }
        library.RegisterItem(target);
        return "Restored";
    }

    private string ConfigurationFingerprint() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(configuration.Current)));
    private sealed record Candidate(RecoveryCandidate Dto, UserData Row, string Fingerprint, RecoveryLedger.Entry? Identity, Guid? TargetId);
    private sealed record Grant(Guid Administrator, DateTimeOffset Expires, string Configuration, Dictionary<string, Candidate> Items);
}

public sealed record RecoveryCandidate(string Id, Guid UserId, string Name, string Type, string Ownership, string Action,
    string Reason, bool CanRestore, bool Played, bool IsFavorite, long PlaybackPositionTicks, int PlayCount, DateTime? LastPlayedDate);
public sealed record RecoveryPreview(string PreviewToken, DateTimeOffset ExpiresAtUtc, int StartIndex, int Limit, bool HasMore,
    IReadOnlyList<RecoveryCandidate> Items);
public sealed record RecoveryResult(string Id, string Status);
public sealed record RecoveryExecution(IReadOnlyList<RecoveryResult> Items);
