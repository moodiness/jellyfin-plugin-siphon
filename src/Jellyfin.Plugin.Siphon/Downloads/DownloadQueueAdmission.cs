using System.Security.Cryptography;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Siphon.Playback;
using Jellyfin.Plugin.Siphon.Protocol;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Siphon.Downloads;

public sealed partial class DownloadQueueService
{
    private const int MaximumBatchItems = 50;
    private readonly SemaphoreSlim _preparationSlots = new(4, 4);
    private readonly Dictionary<string, PreviewGrant> _previews = new(StringComparer.Ordinal);
    private sealed record PreviewGrant(Guid UserId, Guid ParentId, string Profile, string Transport, DateTimeOffset Expires,
        IReadOnlyDictionary<Guid, PreviewSelection> Sources);
    private sealed record PreviewSelection(string ItemKey, IReadOnlyDictionary<Guid, string> Sources);

    internal async Task<DownloadPreparation> PreparationAsync(Guid userId, DownloadPreparationRequest request, CancellationToken ct)
    {
        if (!Enabled || !Allowed(userId)) throw new DownloadQueueException(403, "Server downloads are not enabled for this account.");
        var profile = AddonRegistry.PlaybackKey(configuration.Current, userId);
        var transport = TransportKey;
        var source = await ResolveSelectionAsync(userId, request.ItemId, request.MediaSourceId, ct).ConfigureAwait(false);
        var prepared = await PrepareSourceAsync(source, ct).ConfigureAwait(false);
        if (!Enabled || !Allowed(userId) || profile != AddonRegistry.PlaybackKey(configuration.Current, userId) || transport != TransportKey
            || access.GetVideo(request.ItemId, userId, download: true) is null
            || !Guid.TryParse(request.MediaSourceId, out var selected) || access.GetVideo(selected, userId, download: true) is not { } version
            || version.GetProviderId(NativeVersionService.SourceProvider) != source.Id
            || version.GetProviderId(NativeVersionService.OwnerProvider) != userId.ToString("N"))
            throw new DownloadQueueException(403, "Download permission or the playback profile changed.");
        return prepared;
    }

    private async Task<ResolvedStream> ResolveSelectionAsync(Guid userId, Guid itemId, string selectedId, CancellationToken ct)
    {
        if (!Guid.TryParse(selectedId, out var id) || id == Guid.Empty
            || access.GetVideo(itemId, userId, download: true) is not { } item || item.GetProviderId("Siphon") is not { } key
            || state.FindByKey(key) is not { } managed || access.GetVideo(id, userId, download: true) is not { } selected
            || selected.GetProviderId("Siphon") != key || selected.GetProviderId(NativeVersionService.OwnerProvider) != userId.ToString("N")
            || selected.GetProviderId(NativeVersionService.SourceProvider) is not { } sourceId)
            throw new DownloadQueueException(404, "The selected version is unavailable.");
        return (await resolver.GetSourcesAsync(managed, userId, ct).ConfigureAwait(false)).FirstOrDefault(value => value.Id == sourceId && value.UserId == userId)
            ?? throw new DownloadQueueException(409, "The exact selected version is no longer available.", "DownloadSourceUnavailable");
    }

    private async Task<DownloadPreparation> PrepareSourceAsync(ResolvedStream source, CancellationToken ct)
    {
        if (!await _preparationSlots.WaitAsync(0, ct).ConfigureAwait(false)) throw new DownloadQueueException(429, "Download preparation is busy. Try again shortly.");
        try { return await transfer.PrepareAsync(source, ct).ConfigureAwait(false); }
        catch (DownloadSelectionException exception) { throw new DownloadQueueException(409, exception.Message, "DownloadSelectionUnavailable"); }
        catch (InvalidDataException) { throw new DownloadQueueException(409, "This HLS playlist uses an unsupported format. Choose a finite, non-DRM source with supported tracks.", "DownloadFormatUnsupported"); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        { throw new DownloadQueueException(409, "The selected source could not be prepared within its metadata limits. Choose another version or try again.", "DownloadSourceUnavailable"); }
        finally { _preparationSlots.Release(); }
    }

    private static void ValidateSelection(DownloadTransferOptions options, DownloadPreparation preparation)
    {
        if (preparation.Kind != "Hls" && options.HasSelection)
            throw new DownloadQueueException(409, "Progressive and peer downloads retain all tracks. Choose all tracks or a finite HLS version for track filtering.", "DownloadSelectionUnavailable");
        if (options.AudioLanguages is { } audio && audio.Any(value => !preparation.AudioLanguages.Contains(value, StringComparer.OrdinalIgnoreCase))
            || options.SubtitleLanguages is { } subtitles && subtitles.Any(value => !preparation.SubtitleLanguages.Contains(value, StringComparer.OrdinalIgnoreCase)))
            throw new DownloadQueueException(409, "A requested language is not advertised for the selected version. Prepare that version again.", "DownloadSelectionUnavailable");
    }

    internal async Task<DownloadBatchPreview> BatchPreviewAsync(Guid userId, Guid itemId, CancellationToken ct)
    {
        RequireBatchParent(userId, itemId);
        if (!await _preparationSlots.WaitAsync(0, ct).ConfigureAwait(false)) throw new DownloadQueueException(429, "Download preparation is busy. Try again shortly.");
        try
        {
            var profile = AddonRegistry.PlaybackKey(configuration.Current, userId);
            var transport = TransportKey;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            var user = users.GetUserById(userId)!;
            var query = new InternalItemsQuery(user)
            {
                ParentId = itemId,
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Episode],
                IncludeAlternateVersions = false,
                GroupByPresentationUniqueKey = true,
                EnableTotalRecordCount = false,
                Limit = MaximumBatchItems + 1,
                OrderBy = [(ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending)]
            };
            library.ConfigureUserAccess(query, user);
            var episodes = library.GetItemList(query).OfType<Episode>().Where(value => value.PrimaryVersionId is null)
                .Take(MaximumBatchItems + 1).ToArray();
            var items = new List<DownloadBatchItem>();
            var grants = new Dictionary<Guid, PreviewSelection>();
            var truncated = episodes.Length > MaximumBatchItems;
            // Sequential bounded resolution avoids a season multiplying addon fanout.
            foreach (var episode in episodes.Take(MaximumBatchItems))
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (access.GetVideo(episode.Id, userId, download: true) is null || !NativeVersionService.IsManaged(episode)) continue;
                if (episode.GetProviderId("Siphon") is not { Length: > 0 } itemKey) continue;
                var available = await versions.GetVersionsAsync(episode, userId, deadline.Token).ConfigureAwait(false);
                var eligible = available.Where(value => value.Stream.UserId == userId
                    && value.Item.GetProviderId(NativeVersionService.OwnerProvider) == userId.ToString("N")
                    && value.Item.GetProviderId(NativeVersionService.SourceProvider) == value.Stream.Id
                    && value.Item.GetProviderId("Siphon") == itemKey
                    && access.GetVideo(value.Item.Id, userId, download: true) is not null).ToArray();
                truncated |= eligible.Length > 128;
                grants.Add(episode.Id, new(itemKey, eligible.Take(128).ToDictionary(value => value.Item.Id, value => value.Stream.Id)));
                var sources = eligible.Take(128)
                    .Select(value => new DownloadBatchSource(value.Item.Id.ToString("N"), SourceLabel(value.Stream.Name), value.Stream.Size is >= 0 ? value.Stream.Size : null,
                        value.Stream.P2p is not null ? "P2p" : value.Stream.Url.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? "Hls" : "Unknown")).ToArray();
                items.Add(new(episode.Id, Label(episode.Name), episode.ParentIndexNumber, episode.IndexNumber, sources));
            }
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                RequireBatchParent(userId, itemId);
                if (profile != AddonRegistry.PlaybackKey(configuration.Current, userId) || transport != TransportKey)
                    throw new DownloadQueueException(403, "Download permission or the playback profile changed.");
                var now = DateTimeOffset.UtcNow;
                foreach (var key in _previews.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray()) _previews.Remove(key);
                if (_previews.Count >= 256 || _previews.Values.Count(value => value.UserId == userId) >= 4)
                    throw new DownloadQueueException(429, "Too many batch previews. Wait for a preview to expire.");
                var visible = items.Where(value => access.GetVideo(value.ItemId, userId, download: true)?.GetProviderId("Siphon") == grants[value.ItemId].ItemKey)
                    .Select(value => value with
                    {
                        Sources = value.Sources.Where(source =>
                        access.GetVideo(Guid.Parse(source.MediaSourceId), userId, download: true)?.GetProviderId(NativeVersionService.SourceProvider)
                            == grants[value.ItemId].Sources[Guid.Parse(source.MediaSourceId)]).ToArray()
                    }).ToArray();
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                var expires = now.AddMinutes(5);
                _previews.Add(token, new(userId, itemId, profile, transport, expires,
                    visible.ToDictionary(value => value.ItemId, value => new PreviewSelection(grants[value.ItemId].ItemKey,
                        value.Sources.ToDictionary(source => Guid.Parse(source.MediaSourceId), source => grants[value.ItemId].Sources[Guid.Parse(source.MediaSourceId)])))));
                return new(token, expires, visible, truncated, MaximumBatchItems);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new DownloadQueueException(429, "Batch preparation exceeded its time limit. Preview a smaller season."); }
        finally { _preparationSlots.Release(); }
    }

    internal async Task<DownloadBatchResult> BatchAsync(Guid userId, DownloadBatchRequest request, CancellationToken ct)
    {
        if (request.Items is null || request.Items.Length is < 1 or > MaximumBatchItems || request.Items.Any(value => value is null)
            || request.Token is not { Length: 64 }) throw new DownloadQueueException(400, "Select between one and 50 previewed episodes.");
        PreviewGrant preview;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_previews.TryGetValue(request.Token, out var found) || found.UserId != userId || found.Expires <= DateTimeOffset.UtcNow)
                throw new DownloadQueueException(409, "The batch preview is unavailable or expired. Preview again.", "DownloadPreviewExpired");
            preview = found;
            _previews.Remove(request.Token);
        }
        finally { _gate.Release(); }
        var jobs = new List<DownloadJobSummary>();
        var rejected = new List<DownloadBatchRejection>();
        var seen = new HashSet<Guid>();
        foreach (var item in request.Items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                RequireBatchParent(userId, preview.ParentId);
                if (preview.Profile != AddonRegistry.PlaybackKey(configuration.Current, userId) || preview.Transport != TransportKey)
                    throw new DownloadQueueException(403, "Download permission or the playback profile changed.");
                if (!seen.Add(item.ItemId) || !Guid.TryParse(item.MediaSourceId, out var selected)
                    || !preview.Sources.TryGetValue(item.ItemId, out var allowed) || !allowed.Sources.TryGetValue(selected, out var sourceId)
                    || access.GetVideo(item.ItemId, userId, download: true)?.GetProviderId("Siphon") != allowed.ItemKey
                    || access.GetVideo(selected, userId, download: true)?.GetProviderId(NativeVersionService.SourceProvider) != sourceId)
                    throw new DownloadQueueException(409, "Select exactly one advertised version for each previewed episode.", "DownloadSelectionUnavailable");
                jobs.Add(await EnqueueAsync(userId, item, ct, allowed.ItemKey, sourceId).ConfigureAwait(false));
            }
            catch (DownloadQueueException exception) { rejected.Add(new(item.ItemId, exception.Message, exception.Code)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { rejected.Add(new(item.ItemId, "The selected episode could not be queued. Refresh its available versions.", "DownloadSourceUnavailable")); }
        }
        return new(jobs.ToArray(), rejected.ToArray());
    }

    private void RequireBatchParent(Guid userId, Guid itemId)
    {
        if (!Enabled || !Allowed(userId)) throw new DownloadQueueException(403, "Server downloads are not enabled for this account.");
        var user = users.GetUserById(userId)!;
        var query = new InternalItemsQuery(user) { Recursive = true, IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Season], Limit = 1 };
        library.ConfigureUserAccess(query, user);
        query.ItemIds = [itemId];
        if (!library.GetItemList(query).Any(value => value.Id == itemId && value is Series or Season))
            throw new DownloadQueueException(404, "The selected series or season is unavailable.");
    }

    private static string Label(string value) => new(value.Where(character => !char.IsControl(character)).Take(256).ToArray());
    private static string SourceLabel(string value) => value.Contains("://", StringComparison.Ordinal) ? "Selected version" : Label(value);
}
