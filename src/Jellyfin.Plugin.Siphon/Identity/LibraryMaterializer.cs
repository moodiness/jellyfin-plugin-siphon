using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Siphon.Identity;

/// <summary>Publishes addon media into one registered Jellyfin media library.</summary>
public sealed class LibraryMaterializer(
    ConfigurationAccessor configuration,
    CapabilityTokenService tokens,
    ISiphonStateStore state,
    SiphonPaths paths,
    ILibraryManager library,
    IItemPersistenceService persistence,
    IDbContextFactory<JellyfinDbContext> database,
    IFileSystem fileSystem,
    ILogger<LibraryMaterializer> logger) : IHostedService
{
    private const string ItemProvider = "Siphon";
    private const string LegacyCatalogType = "Jellyfin.Plugin.Siphon.Identity.SiphonCatalogFolder";
    private readonly string _libraryPath = Path.Combine(paths.DataDirectory, "library");

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
            await MaterializeAsync(retained, owned.Items, owned.CatalogRoots, false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            configuration.MutationGate.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task ApplyAsync(IReadOnlyList<ManagedItem> retained, CancellationToken cancellationToken)
    {
        if (retained.Count > 0 && string.IsNullOrWhiteSpace(configuration.Current.PublicBaseUrl))
        {
            throw new InvalidOperationException("Set the Public Jellyfin base URL before synchronizing the Siphon library.");
        }
        var owned = await LoadManagedItemsAsync(cancellationToken).ConfigureAwait(false);
        await MaterializeAsync(retained, owned.Items, owned.CatalogRoots, true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(Dictionary<Guid, BaseItem> Items, Guid[] CatalogRoots)> LoadManagedItemsAsync(CancellationToken cancellationToken)
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

        var ids = await context.BaseItems.Where(item => item.Provider!.Any(provider => provider.ProviderId == ItemProvider))
            .Select(item => item.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<Guid, BaseItem>();
        foreach (var batch in ids.Chunk(256))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in library.GetItemList(new InternalItemsQuery
            {
                ItemIds = batch,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode],
                GroupByPresentationUniqueKey = false
            }))
            {
                if (item.ChannelId == Guid.Empty) result.Add(item.Id, item);
            }
        }
        return (result, catalogRoots);
    }

    private async Task<(CollectionFolder View, Folder Root)> EnsureLibraryAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_libraryPath);
        var root = library.FindByPath(_libraryPath, true) as Folder;
        if (root is null)
        {
            // Jellyfin's filesystem resolver skips empty directories. Register the physical
            // parent explicitly because addon media are remote, not files in this directory.
            root = new Folder
            {
                Id = library.GetNewItemId(_libraryPath, typeof(Folder)),
                Name = "Siphon",
                Path = _libraryPath,
                DateCreated = DateTime.UtcNow
            };
            root.SetParent(library.RootFolder);
            Save(root, true, cancellationToken);
        }
        var view = library.GetVirtualFolders().FirstOrDefault(folder => folder.Locations.Any(location => fileSystem.AreEqual(location, _libraryPath)));
        if (view is null)
        {
            await library.AddVirtualFolder("Siphon", null, new LibraryOptions
            {
                PathInfos = [new MediaPathInfo(_libraryPath)],
                EnableRealtimeMonitor = false,
                SaveLocalMetadata = false
            }, false).ConfigureAwait(false);
            view = library.GetVirtualFolders().FirstOrDefault(folder => folder.Locations.Any(location => fileSystem.AreEqual(location, _libraryPath)))
                ?? throw new InvalidOperationException("Jellyfin did not register the Siphon media library.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var collection = library.GetItemById<CollectionFolder>(Guid.Parse(view.ItemId))
            ?? throw new InvalidOperationException("Jellyfin could not load the Siphon media library.");
        if (!collection.PhysicalFolderIds.Contains(root.Id))
        {
            await collection.RefreshMetadata(cancellationToken).ConfigureAwait(false);
        }
        if (!collection.PhysicalFolderIds.Contains(root.Id))
        {
            throw new InvalidOperationException("Jellyfin could not link the Siphon library's managed location.");
        }
        return (collection, root);
    }

    private async Task MaterializeAsync(IReadOnlyList<ManagedItem> retained, Dictionary<Guid, BaseItem> existing,
        Guid[] obsoleteRoots, bool refreshExisting, CancellationToken cancellationToken)
    {
        var wanted = new HashSet<Guid>();
        if (retained.Count > 0)
        {
            var (view, root) = await EnsureLibraryAsync(cancellationToken).ConfigureAwait(false);
            var byKey = new Dictionary<string, List<BaseItem>>(StringComparer.Ordinal);
            foreach (var item in existing.Values)
            {
                var key = item.GetProviderId(ItemProvider);
                if (key is null) continue;
                if (!byKey.TryGetValue(key, out var group)) byKey[key] = group = [];
                group.Add(item);
            }
            var series = new Dictionary<string, Series>(StringComparer.Ordinal);
            var seasons = new Dictionary<(string Content, int Number), Season>();
            var added = new List<BaseItem>();
            var updated = new List<BaseItem>();
            foreach (var managed in retained)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Season? season = null;
                Series? show = null;
                if (managed.Type == "series")
                {
                    if (!series.TryGetValue(managed.ContentKey, out show))
                    {
                        show = FindExisting<Series>(byKey, managed.ContentKey, root.Id)
                            ?? new Series { Id = library.GetNewItemId("siphon:series:" + managed.ContentKey, typeof(Series)) };
                        if (refreshExisting || show.ParentId != root.Id || !existing.ContainsKey(show.Id))
                        {
                            SetMetadata(show, managed, managed.ContentKey, managed.ProviderIds);
                            show.Name = managed.SeriesName ?? managed.Name;
                            show.Overview = managed.SeriesDescription;
                            show.PresentationUniqueKey = show.Id.ToString("N");
                            show.Path = "siphon://series/" + show.PresentationUniqueKey;
                            show.SetParent(root);
                            Save(show, !existing.ContainsKey(show.Id), cancellationToken);
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
                        if (refreshExisting || season.ParentId != show.Id || obsoleteRoots.Length > 0 || !existing.ContainsKey(season.Id))
                        {
                            SetMetadata(season, managed, key, []);
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
                if (!refreshExisting && obsoleteRoots.Length == 0 && media.ParentId == parent.Id && existing.ContainsKey(media.Id)) continue;
                SetMetadata(media, managed, managed.Key, managed.Type == "movie" ? managed.ProviderIds : managed.EpisodeProviderIds);
                media.Path = configuration.Current.PublicBaseUrl.TrimEnd('/') + "/Siphon/s/" + tokens.SignItem(managed.Key);
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
            }
            foreach (var batch in added.Chunk(256)) library.CreateItems(batch, null, cancellationToken);
            foreach (var batch in updated.Chunk(256))
            {
                persistence.SaveItems(batch, cancellationToken);
                foreach (var item in batch) library.RegisterItem(item);
            }
            foreach (var folder in series.Values.Cast<Folder>().Concat(seasons.Values).Append(root).Append(view))
            {
                folder.Children = null;
                folder.UserData = null;
            }
        }

        foreach (var stale in existing.Values.Where(item => !wanted.Contains(item.Id))
            .OrderBy(item => item is Series ? 2 : item is Season ? 1 : 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stale is Folder folder) folder.Children = null;
            library.DeleteItem(stale, new DeleteOptions { DeleteFileLocation = false });
        }
        if (obsoleteRoots.Length > 0) persistence.DeleteItem(obsoleteRoots);
        library.RootFolder.Children = null;
        logger.LogInformation("Siphon published {ItemCount} unique media items into its Jellyfin media library", retained.Count);
    }

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

    private void SetMetadata(BaseItem target, ManagedItem item, string key, IEnumerable<KeyValuePair<string, string>> ids)
    {
        target.Name = item.Name;
        target.IsVirtualItem = false;
        target.IsLocked = true;
        target.ChannelId = Guid.Empty;
        target.Overview = item.Description;
        target.ProductionYear = item.Year;
        target.PremiereDate = DateTime.TryParse(item.Released, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : null;
        target.Genres = item.Genres;
        if (target.DateCreated == DateTime.MinValue) target.DateCreated = DateTime.UtcNow;
        target.ProviderIds = new Dictionary<string, string>(ids, StringComparer.OrdinalIgnoreCase) { [ItemProvider] = key };
        if (!string.IsNullOrWhiteSpace(item.PosterUrl))
        {
            target.SetImagePath(ImageType.Primary, configuration.Current.PublicBaseUrl.TrimEnd('/') + "/Siphon/image/" + tokens.SignItem(item.Key));
        }
        target.DateModified = target.DateLastSaved = DateTime.UtcNow;
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
