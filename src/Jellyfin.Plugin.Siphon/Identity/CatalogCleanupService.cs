using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Database.Implementations;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.Siphon.Identity;

/// <summary>One absence, grace and user-protection policy for automatic and confirmed manual cleanup.</summary>
public sealed class CatalogCleanupService(
    ConfigurationAccessor configuration,
    ISiphonStateStore state,
    LibraryMaterializer materializer,
    IDbContextFactory<JellyfinDbContext> database,
    Collections.CollectionRetentionService collections,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private PreviewGrant? _preview;
    private bool _uncertainSynchronization;

    // Caller owns SynchronizationGate. A cancelled/failed commit must not leave an older preview executable.
    internal void BeginSynchronization()
    {
        _preview = null;
        _uncertainSynchronization = true;
    }

    internal void CompleteSynchronization() => _uncertainSynchronization = false;

    internal async Task<IReadOnlyList<ManagedItem>> RetainAsync(IReadOnlyList<ManagedItem> items, CancellationToken ct)
    {
        if (!configuration.Current.RemoveMissingItems) return items;
        var assessment = await AssessAsync(items, _clock.GetUtcNow(), ct).ConfigureAwait(false);
        var remove = assessment.Where(item => item.Reason == "Eligible").Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        return items.Where(item => !remove.Contains(item.Key)).ToArray();
    }

    public async Task<CleanupPreview> PreviewAsync(int startIndex, int limit, CancellationToken ct, string? previewToken = null)
    {
        await configuration.SynchronizationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var generated = _clock.GetUtcNow();
            var items = state.GetItems();
            var assessment = await AssessAsync(items, generated, ct).ConfigureAwait(false);
            if (_uncertainSynchronization)
                assessment = assessment.Select(item => item with { Reason = "SynchronizationUnconfirmed" }).ToArray();
            var snapshotFingerprint = Fingerprint(items);
            var assessmentFingerprint = AssessmentFingerprint(assessment);
            PreviewGrant grant;
            if (previewToken is not null || startIndex > 0)
            {
                grant = _preview ?? throw new InvalidOperationException("Preview again before requesting more items.");
                if (grant.ExpiresAtUtc <= generated || grant.Token != previewToken
                    || grant.SnapshotFingerprint != snapshotFingerprint || grant.AssessmentFingerprint != assessmentFingerprint)
                    throw new InvalidOperationException("The preview changed. Preview again before requesting more items.");
            }
            else
            {
                grant = new PreviewGrant(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), generated,
                    generated.AddMinutes(5), snapshotFingerprint, assessmentFingerprint);
                _preview = grant;
            }
            return new CleanupPreview(grant.GeneratedAtUtc, grant.ExpiresAtUtc, grant.Token, configuration.Current.RemoveMissingItems,
                assessment.Count, assessment.Count(item => item.Reason == "Eligible"),
                assessment.Count(item => IsProtectionReason(item.Reason)), startIndex, limit,
                assessment.Skip(startIndex).Take(limit).ToArray());
        }
        finally
        {
            configuration.SynchronizationGate.Release();
        }
    }

    public async Task<CleanupExecution> ExecuteAsync(string previewToken, bool confirmed, CancellationToken ct)
    {
        if (!confirmed) throw new InvalidOperationException("Explicit cleanup confirmation is required.");
        await configuration.SynchronizationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var grant = _preview;
            _preview = null; // A grant is single-use, including failed attempts.
            var now = _clock.GetUtcNow();
            if (grant is null || grant.ExpiresAtUtc <= now || _uncertainSynchronization
                || !string.Equals(grant.Token, previewToken, StringComparison.Ordinal))
                throw new InvalidOperationException("The cleanup preview expired or changed. Preview again before confirming.");
            var items = state.GetItems();
            if (Fingerprint(items) != grant.SnapshotFingerprint)
                throw new InvalidOperationException("The saved settings or library snapshot changed. Preview again before confirming.");
            var assessment = await AssessAsync(items, now, ct).ConfigureAwait(false);
            if (AssessmentFingerprint(assessment) != grant.AssessmentFingerprint)
                throw new InvalidOperationException("Cleanup eligibility or user protection changed. Preview again before confirming.");
            var remove = assessment.Where(item => item.Reason == "Eligible").Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            if (remove.Count == 0) return new CleanupExecution(0);
            var retained = items.Where(item => !remove.Contains(item.Key)).ToArray();
            await configuration.MutationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await state.SaveAsync(retained, ct).ConfigureAwait(false);
                await materializer.ApplyAsync(retained, ct, previousItems: items).ConfigureAwait(false);
            }
            finally
            {
                configuration.MutationGate.Release();
            }
            return new CleanupExecution(remove.Count);
        }
        finally
        {
            configuration.SynchronizationGate.Release();
        }
    }

    private async Task<IReadOnlyList<CleanupItem>> AssessAsync(IReadOnlyList<ManagedItem> items, DateTimeOffset now, CancellationToken ct)
    {
        var config = configuration.Current;
        var missing = items.Where(item => item.MissingSinceUtc.HasValue).OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
        var activeOwners = config.Addons.Where(addon => addon.Enabled)
            .SelectMany(addon => addon.Catalogs.Where(catalog => catalog.Enabled).Select(catalog => addon.Id + ":" + catalog.Key))
            .ToHashSet(StringComparer.Ordinal);
        var protections = await GetProtectionsAsync(missing, config, ct).ConfigureAwait(false);
        return missing.Select(item => Evaluate(item, config, activeOwners, now, protections.GetValueOrDefault(item.Key))).ToArray();
    }

    internal static CleanupItem Evaluate(ManagedItem item, PluginConfiguration config, IReadOnlySet<string> activeOwners,
        DateTimeOffset now, string? protection)
    {
        var eligibleAfter = item.MissingSinceUtc?.AddDays(Math.Clamp(config.MissingItemRetentionDays, 0, 365));
        var reason = !item.MissingSinceUtc.HasValue || item.Owners.Length == 0
            || item.Owners.Any(owner => !activeOwners.Contains(owner) || !item.MissingOwners.Contains(owner, StringComparer.Ordinal))
            ? "OwnershipUnconfirmed"
            : protection ?? (eligibleAfter > now ? "GracePeriod" : "Eligible");
        return new CleanupItem(item.Key, item.Name, item.Type, item.MissingSinceUtc, eligibleAfter, reason);
    }

    private async Task<Dictionary<string, string>> GetProtectionsAsync(ManagedItem[] missing, PluginConfiguration config, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (missing.Length == 0) return result;
        try
        {
            var membership = collections.GetProtectedContentKeys();
            foreach (var item in missing)
                if (membership.Contains(item.ContentKey)) result[item.Key] = "CollectionOrPlaylist";
            if (!config.ProtectFavorites && !config.ProtectResumePositions) return result;
            await using var context = await database.CreateDbContextAsync(ct).ConfigureAwait(false);
            var relevantKeys = missing.SelectMany(item => item.Type == "series"
                ? new[] { item.Key, item.ContentKey, SeasonKey(item) } : new[] { item.Key }).Distinct(StringComparer.Ordinal);
            var favorites = new HashSet<string>(StringComparer.Ordinal);
            var resume = new HashSet<string>(StringComparer.Ordinal);
            // Jellyfin 12.1 stores user state by native item ID. Versions have the same Siphon
            // ownership key, so this includes every version and every user's persisted state
            // without issuing one database query per user or per media item.
            foreach (var batch in relevantKeys.Chunk(256))
            {
                var protectedRows = await context.UserData.AsNoTracking()
                    .Where(data => data.Item != null
                        && ((config.ProtectFavorites && data.IsFavorite)
                            || (config.ProtectResumePositions && data.PlaybackPositionTicks > 0))
                        && data.Item.Provider!.Any(provider => provider.ProviderId == "Siphon" && batch.Contains(provider.ProviderValue)))
                    .SelectMany(data => data.Item!.Provider!
                        .Where(provider => provider.ProviderId == "Siphon" && batch.Contains(provider.ProviderValue)),
                        (data, provider) => new { Key = provider.ProviderValue, data.IsFavorite, HasResume = data.PlaybackPositionTicks > 0 })
                    .Distinct().ToArrayAsync(ct).ConfigureAwait(false);
                foreach (var row in protectedRows)
                {
                    if (row.IsFavorite) favorites.Add(row.Key);
                    if (row.HasResume) resume.Add(row.Key);
                }
            }
            foreach (var item in missing)
            {
                if (!result.ContainsKey(item.Key) && ProtectionReason(item, config, favorites, resume) is { } reason) result[item.Key] = reason;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Failed queries are not evidence that no user has protected an item.
            foreach (var item in missing) result[item.Key] = "ProtectionUnavailable";
        }
        return result;
    }

    internal static string? ProtectionReason(ManagedItem item, PluginConfiguration config,
        IReadOnlySet<string> favorites, IReadOnlySet<string> resume)
    {
        if (config.ProtectFavorites && (favorites.Contains(item.Key)
            || (item.Type == "series" && (favorites.Contains(item.ContentKey) || favorites.Contains(SeasonKey(item))))))
            return "Favorite";
        return config.ProtectResumePositions && resume.Contains(item.Key) ? "ResumePosition" : null;
    }

    private static string SeasonKey(ManagedItem item) => item.ContentKey + ":season:" + (item.Season ?? 0).ToString(CultureInfo.InvariantCulture);
    private static bool IsProtectionReason(string reason) => reason is "Favorite" or "ResumePosition" or "CollectionOrPlaylist" or "ProtectionUnavailable" or "SynchronizationUnconfirmed";

    private string Fingerprint(IReadOnlyList<ManagedItem> items)
    {
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        JsonSerializer.Serialize(stream, configuration.Current);
        JsonSerializer.Serialize(stream, items);
        stream.FlushFinalBlock();
        return Convert.ToHexString(hash.Hash!);
    }

    private static string AssessmentFingerprint(IReadOnlyList<CleanupItem> items)
    {
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        JsonSerializer.Serialize(stream, items);
        stream.FlushFinalBlock();
        return Convert.ToHexString(hash.Hash!);
    }

    private sealed record PreviewGrant(string Token, DateTimeOffset GeneratedAtUtc, DateTimeOffset ExpiresAtUtc,
        string SnapshotFingerprint, string AssessmentFingerprint);
}

public sealed record CleanupItem(string Key, string Name, string Type, DateTimeOffset? MissingSinceUtc, DateTimeOffset? EligibleAfterUtc, string Reason);
public sealed record CleanupPreview(DateTimeOffset GeneratedAtUtc, DateTimeOffset ExpiresAtUtc, string PreviewToken,
    bool AutomaticRemovalEnabled, int TotalMissing, int TotalEligible, int TotalProtected, int StartIndex, int Limit, IReadOnlyList<CleanupItem> Items);
public sealed record CleanupExecution(int DeletedCount);
