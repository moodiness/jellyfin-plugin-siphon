using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Playback;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Siphon.Identity;

/// <summary>Publishes native media into catalog libraries while retaining their Jellyfin identities.</summary>
public sealed class LibraryMaterializer(
    ConfigurationAccessor configuration,
    CapabilityTokenService tokens,
    ISiphonStateStore state,
    ILibraryManager library,
    IItemPersistenceService persistence,
    IDbContextFactory<JellyfinDbContext> database,
    ItemMetadataMapper metadata,
    CatalogLibraryService catalogs,
    NativeVersionService versions,
    ILogger<LibraryMaterializer> logger,
    SiphonPaths paths,
    SyncDiagnostics diagnostics) : IHostedService
{
    private const string ItemProvider = "Siphon";
    private const string LegacyCatalogType = "Jellyfin.Plugin.Siphon.Identity.SiphonCatalogFolder";
    private readonly string _publicationPath = Path.Combine(paths.DataDirectory, "publication.json");
    private Dictionary<string, string>? _published;
    private bool _publicationLoaded;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await configuration.MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var owned = await LoadManagedItemsAsync(cancellationToken).ConfigureAwait(false);
            var retained = state.GetItems();
            if (retained.Count > 0 && string.IsNullOrWhiteSpace(configuration.Current.PublicBaseUrl))
            {
                logger.LogWarning("Configure PublicBaseUrl and synchronize Siphon to populate its media library");
                return;
            }
            var hashes = PublicationHashes(retained);
            var changed = PublicationChanges(hashes, includeUnknown: false);
            await MaterializeAsync(retained, owned.Items, owned.TopParents, owned.CatalogRoots, cancellationToken, null,
                changedKeys: changed).ConfigureAwait(false);
            await SavePublicationAsync(hashes, changed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            configuration.MutationGate.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task ApplyAsync(IReadOnlyList<ManagedItem> retained, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? catalogNames = null, IProgress<double>? progress = null,
        IReadOnlySet<string>? changedKeys = null, IReadOnlySet<string>? scopedContentKeys = null)
    {
        if (retained.Count > 0 && string.IsNullOrWhiteSpace(configuration.Current.PublicBaseUrl))
        {
            throw new InvalidOperationException("Set the Public Jellyfin base URL before synchronizing the Siphon library.");
        }
        var owned = await LoadManagedItemsAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(5);
        var hashes = PublicationHashes(retained);
        if (changedKeys is not null)
        {
            var changed = PublicationChanges(hashes, includeUnknown: true);
            changed.UnionWith(changedKeys);
            changedKeys = changed;
        }
        await MaterializeAsync(retained, owned.Items, owned.TopParents, owned.CatalogRoots, cancellationToken, catalogNames,
            progress, changedKeys, scopedContentKeys).ConfigureAwait(false);
        var publishedKeys = scopedContentKeys is null ? null
            : retained.Where(item => scopedContentKeys.Contains(item.ContentKey)).Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        await SavePublicationAsync(hashes, publishedKeys, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(Dictionary<Guid, BaseItem> Items, Dictionary<Guid, Guid?> TopParents, Guid[] CatalogRoots)> LoadManagedItemsAsync(CancellationToken cancellationToken)
    {
        await using var context = await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var folderType = typeof(Folder).FullName!;
        var catalogRoots = await context.BaseItems
            .Where(item => item.Type == LegacyCatalogType
                || (item.Type == folderType && item.Path != null && item.Path.StartsWith("siphon://catalog/")))
            .Select(item => item.Id)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (catalogRoots.Length > 0)
        {
            // v1.3 persisted a plugin-only CLR type. Normalize it before loading parents, then
            // remove these obsolete roots after their media have moved into the real library.
            await context.BaseItems.Where(item => item.Type == LegacyCatalogType)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Type, folderType), cancellationToken).ConfigureAwait(false);
        }

        var topParents = await context.BaseItems.Where(item => item.Provider!.Any(provider => provider.ProviderId == ItemProvider))
            .Select(item => new { item.Id, item.TopParentId })
            .ToDictionaryAsync(item => item.Id, item => (Guid?)item.TopParentId, cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<Guid, BaseItem>();
        foreach (var batch in topParents.Keys.Chunk(256))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in library.GetItemList(new InternalItemsQuery
            {
                ItemIds = batch,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode],
                IncludeAlternateVersions = true,
                GroupByPresentationUniqueKey = false
            }))
            {
                if (item.ChannelId == Guid.Empty) result.Add(item.Id, item);
            }
        }
        return (result, topParents, catalogRoots);
    }


    private async Task MaterializeAsync(IReadOnlyList<ManagedItem> retained, Dictionary<Guid, BaseItem> existing,
        IReadOnlyDictionary<Guid, Guid?> topParents, Guid[] obsoleteRoots, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? catalogNames, IProgress<double>? progress = null,
        IReadOnlySet<string>? changedKeys = null, IReadOnlySet<string>? scopedContentKeys = null)
    {
        IReadOnlyList<ManagedItem> selected = scopedContentKeys is null ? retained
            : retained.Where(item => scopedContentKeys.Contains(item.ContentKey)).ToArray();
        var changedContent = changedKeys is null ? null
            : selected.Where(item => changedKeys.Contains(item.Key)).Select(item => item.ContentKey).ToHashSet(StringComparer.Ordinal);
        var scopedOwners = scopedContentKeys is null ? null : selected.SelectMany(item => item.Owners).ToHashSet(StringComparer.Ordinal);
        var wanted = new HashSet<Guid>();
        if (scopedContentKeys is not null)
        {
            var selectedKeys = selected.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var item in selected.Where(item => item.Type == "series"))
            {
                selectedKeys.Add(item.ContentKey);
                selectedKeys.Add(item.ContentKey + ":season:" + (item.Season ?? 0));
            }
            foreach (var item in existing.Values)
                if (!selectedKeys.Contains(item.GetProviderId(ItemProvider) ?? string.Empty)) wanted.Add(item.Id);
            obsoleteRoots = [];
        }
        diagnostics.ReportStage("Publishing", "Items", 0, selected.Count);
        var layout = catalogs.Prepare(retained, cancellationToken);
        await catalogs.RegisterAsync(layout, catalogNames, cancellationToken, scopedOwners).ConfigureAwait(false);
        progress?.Report(10);
        if (selected.Count > 0)
        {
            var alternateVersions = new Dictionary<Guid, List<Video>>();
            foreach (var version in existing.Values.OfType<Video>())
            {
                if (!Guid.TryParse(version.GetProviderId(NativeVersionService.VersionProvider), out var owner)) continue;
                if (!alternateVersions.TryGetValue(owner, out var group)) alternateVersions[owner] = group = [];
                group.Add(version);
            }
            var byKey = new Dictionary<string, List<BaseItem>>(StringComparer.Ordinal);
            foreach (var item in existing.Values)
            {
                if (!string.IsNullOrEmpty(item.GetProviderId(NativeVersionService.VersionProvider))) continue;
                var key = item.GetProviderId(ItemProvider);
                if (key is null) continue;
                if (!byKey.TryGetValue(key, out var group)) byKey[key] = group = [];
                group.Add(item);
            }
            var series = new Dictionary<string, Series>(StringComparer.Ordinal);
            var seasons = new Dictionary<(string Content, int Number), Season>();
            var movedSeries = new HashSet<Guid>();
            var added = new List<BaseItem>();
            var updated = new List<BaseItem>();
            var people = new List<(BaseItem Item, ManagedItem Managed, bool Initialize)>();
            for (var index = 0; index < selected.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index % 64 == 0)
                {
                    progress?.Report(10 + 35d * index / selected.Count);
                    diagnostics.ReportStage("Publishing", "Items", index, selected.Count);
                }
                var managed = selected[index];
                var root = layout.ContentRoots[managed.ContentKey];
                Season? season = null;
                Series? show = null;
                if (managed.Type == "series")
                {
                    if (!series.TryGetValue(managed.ContentKey, out show))
                    {
                        show = FindExisting<Series>(byKey, managed.ContentKey, root.Id)
                            ?? new Series { Id = library.GetNewItemId("siphon:series:" + managed.ContentKey, typeof(Series)) };
                        // A previous run may have saved a parent but not all descendant batches.
                        if (show.ParentId != root.Id || topParents.GetValueOrDefault(show.Id) != root.Id) movedSeries.Add(show.Id);
                        if (changedContent is null || changedContent.Contains(managed.ContentKey)
                            || movedSeries.Contains(show.Id) || !existing.ContainsKey(show.Id))
                        {
                            metadata.Apply(show, managed, managed.ContentKey, managed.ProviderIds);
                            show.PresentationUniqueKey = show.Id.ToString("N");
                            show.Path = "siphon://series/" + show.PresentationUniqueKey;
                            show.SetParent(root);
                            Save(show, !existing.ContainsKey(show.Id), cancellationToken);
                            people.Add((show, managed, !existing.ContainsKey(show.Id)));
                        }
                        series.Add(managed.ContentKey, show);
                    }
                    wanted.Add(show.Id);
                    var seasonKey = (managed.ContentKey, managed.Season ?? 0);
                    if (!seasons.TryGetValue(seasonKey, out season))
                    {
                        var key = managed.ContentKey + ":season:" + seasonKey.Item2;
                        season = FindExisting<Season>(byKey, key, show.Id)
                            ?? new Season { Id = library.GetNewItemId("siphon:season:" + key, typeof(Season)) };
                        if (changedContent is null || changedContent.Contains(managed.ContentKey)
                            || season.ParentId != show.Id || movedSeries.Contains(show.Id)
                            || topParents.GetValueOrDefault(season.Id) != root.Id || obsoleteRoots.Length > 0 || !existing.ContainsKey(season.Id))
                        {
                            metadata.Apply(season, managed, key, []);
                            if (Metadata.MetadataPolicy.CanUpdate(season, configuration.Current, "Name", string.IsNullOrWhiteSpace(season.Name)))
                                season.Name = seasonKey.Item2 == 0 ? "Specials" : "Season " + seasonKey.Item2;
                            season.Path = show.Path + "/season/" + seasonKey.Item2;
                            season.IndexNumber = seasonKey.Item2;
                            season.SeriesId = show.Id;
                            season.SeriesName = show.Name;
                            season.SeriesPresentationUniqueKey = show.PresentationUniqueKey;
                            season.SetParent(show);
                            season.PresentationUniqueKey = season.CreatePresentationUniqueKey();
                            Save(season, !existing.ContainsKey(season.Id), cancellationToken);
                        }
                        seasons.Add(seasonKey, season);
                    }
                    wanted.Add(season.Id);
                }

                var parent = (Folder?)season ?? root;
                BaseItem media = managed.Type == "movie"
                    ? FindExisting<Movie>(byKey, managed.Key, parent.Id) ?? new Movie { Id = library.GetNewItemId("siphon:media:" + managed.Key, typeof(Movie)) }
                    : FindExisting<Episode>(byKey, managed.Key, parent.Id) ?? new Episode { Id = library.GetNewItemId("siphon:media:" + managed.Key, typeof(Episode)) };
                wanted.Add(media.Id);
                var relatedVersions = alternateVersions.GetValueOrDefault(media.Id) ?? [];
                foreach (var version in relatedVersions) wanted.Add(version.Id);
                if (changedKeys is not null && !changedKeys.Contains(managed.Key)
                    && obsoleteRoots.Length == 0 && media.ParentId == parent.Id
                    && topParents.GetValueOrDefault(media.Id) == root.Id
                    && (show is null || !movedSeries.Contains(show.Id)) && existing.ContainsKey(media.Id)
                    && relatedVersions.All(version => version.ParentId == parent.Id && version.PrimaryVersionId == media.Id
                        && topParents.GetValueOrDefault(version.Id) == root.Id)) continue;
                metadata.Apply(media, managed, managed.Key, managed.Type == "movie" ? managed.ProviderIds : managed.EpisodeProviderIds);
                var boundSource = media.GetProviderId(NativeVersionService.SourceProvider);
                media.Path = configuration.Current.PublicBaseUrl.TrimEnd('/') + (string.IsNullOrEmpty(boundSource)
                    ? "/Siphon/s/" + tokens.SignItem(managed.Key)
                    : "/Siphon/source/" + tokens.SignSource(managed.Key, boundSource));
                media.SetParent(parent);
                if (media is Episode episode)
                {
                    episode.IndexNumber = managed.Episode;
                    episode.ParentIndexNumber = managed.Season;
                    episode.SeriesId = show!.Id;
                    episode.SeriesName = show.Name;
                    episode.SeriesPresentationUniqueKey = show.PresentationUniqueKey;
                    episode.SeasonId = season!.Id;
                    episode.SeasonName = season.Name;
                }
                media.PresentationUniqueKey = media.CreatePresentationUniqueKey();
                (existing.ContainsKey(media.Id) ? updated : added).Add(media);
                people.Add((media, managed, !existing.ContainsKey(media.Id)));
                foreach (var version in relatedVersions)
                {
                    versions.ApplyMetadata(version, (Video)media, managed);
                    updated.Add(version);
                    people.Add((version, managed, false));
                }
            }
            progress?.Report(45);
            var saved = 0;
            var total = added.Count + updated.Count;
            foreach (var batch in added.Chunk(256))
            {
                cancellationToken.ThrowIfCancellationRequested();
                library.CreateItems(batch, null, cancellationToken);
                saved += batch.Length;
                progress?.Report(45 + 20d * saved / total);
            }
            foreach (var batch in updated.Chunk(256))
            {
                cancellationToken.ThrowIfCancellationRequested();
                persistence.SaveItems(batch, cancellationToken);
                foreach (var item in batch) library.RegisterItem(item);
                saved += batch.Length;
                progress?.Report(45 + 20d * saved / total);
            }
            progress?.Report(65);
            diagnostics.ReportStage("Publishing", "Items", selected.Count, selected.Count);
            await metadata.SavePeopleAsync(people, cancellationToken, new ProgressRange(progress, 65, 95)).ConfigureAwait(false);
            // Drop only the cached child lists. BaseItem.UserData is the cached user-data
            // rows: clearing it makes Jellyfin read "no state" and hides favourites.
            foreach (var folder in series.Values.Cast<Folder>().Concat(seasons.Values).Concat(layout.ContentRoots.Values).DistinctBy(folder => folder.Id))
            {
                folder.Children = null;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(95);
        diagnostics.ReportStage("Finalizing", "Items", 0, selected.Count);

        await catalogs.PublishAsync(layout, cancellationToken, scopedContentKeys is null).ConfigureAwait(false);

        foreach (var stale in existing.Values.Where(item => !wanted.Contains(item.Id))
            .OrderBy(item => item is Series ? 2 : item is Season ? 1 : 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stale is Folder folder) folder.Children = null;
            library.DeleteItem(stale, new DeleteOptions { DeleteFileLocation = false });
        }
        if (obsoleteRoots.Length > 0) persistence.DeleteItem(obsoleteRoots);
        library.RootFolder.Children = null;
        logger.LogInformation("Siphon published {ItemCount} unique playable items into native catalog libraries", retained.Count);
        progress?.Report(100);
        diagnostics.ReportStage("Finalizing", "Items", selected.Count, selected.Count);
    }

    private Dictionary<string, string> PublicationHashes(IReadOnlyList<ManagedItem> items)
    {
        if (!_publicationLoaded)
        {
            _publicationLoaded = true;
            try
            {
                if (File.Exists(_publicationPath))
                {
                    using var stream = File.OpenRead(_publicationPath);
                    if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException();
                    var document = JsonSerializer.Deserialize<PublicationDocument>(stream);
                    if (document is null || document.Version != 1 || document.Items is null || document.Items.Count > 100000
                        || document.Items.Any(entry => entry.Key.Length is 0 or > 1024 || entry.Value is null
                            || entry.Value.Length != 64 || !entry.Value.All(char.IsAsciiHexDigit))) throw new InvalidDataException();
                    _published = document.Items;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                _published = null;
                logger.LogWarning("Siphon native publication cache is unavailable; changed metadata will be republished.");
            }
        }

        var config = configuration.Current;
        var policy = string.Join('\n', "1", config.PublicBaseUrl, config.MetadataUpdateMode,
            string.Join(',', config.MetadataRefreshFields.Order(StringComparer.Ordinal)));
        var policyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(policy)));
        return items.ToDictionary(item => item.Key, item => Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(policyHash + ManagedItemComparison.Fingerprint(item)))), StringComparer.Ordinal);
    }

    private HashSet<string> PublicationChanges(IReadOnlyDictionary<string, string> hashes, bool includeUnknown)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, hash) in hashes)
        {
            if (_published is not null && _published.TryGetValue(key, out var previous))
            {
                if (!string.Equals(previous, hash, StringComparison.Ordinal)) changed.Add(key);
            }
            else if (includeUnknown) changed.Add(key);
        }
        return changed;
    }

    private async Task SavePublicationAsync(Dictionary<string, string> hashes, IReadOnlySet<string>? publishedKeys, CancellationToken cancellationToken)
    {
        var committed = publishedKeys is null ? hashes : (_published ?? new Dictionary<string, string>(StringComparer.Ordinal))
            .Where(entry => hashes.ContainsKey(entry.Key)).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        if (publishedKeys is not null)
            foreach (var key in publishedKeys) committed[key] = hashes[key];
        var temporary = _publicationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_publicationPath)!);
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(stream, new PublicationDocument(1, committed), cancellationToken: cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _publicationPath, overwrite: true);
            _published = committed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _published = null;
            logger.LogWarning("Siphon native publication cache could not be saved; the next synchronization will safely republish metadata.");
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record PublicationDocument(int Version, Dictionary<string, string> Items);

    private static T? FindExisting<T>(IReadOnlyDictionary<string, List<BaseItem>> items, string key, Guid preferredParent) where T : BaseItem
    {
        if (!items.TryGetValue(key, out var candidates)) return null;
        T? first = null;
        foreach (var item in candidates)
        {
            if (item is not T candidate) continue;
            if (candidate.ParentId == preferredParent) return candidate;
            first ??= candidate;
        }
        return first;
    }


    private void Save(BaseItem item, bool isNew, CancellationToken cancellationToken)
    {
        item.DateLastSaved = DateTime.UtcNow;
        if (isNew) library.CreateItems([item], item.GetParent(), cancellationToken);
        else
        {
            persistence.SaveItems([item], cancellationToken);
            library.RegisterItem(item);
        }
    }
}
