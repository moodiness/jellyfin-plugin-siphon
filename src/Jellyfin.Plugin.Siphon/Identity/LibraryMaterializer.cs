using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Siphon.Identity;

/// <summary>Publishes each selected catalog as a native home root with real movie/series hierarchies.</summary>
public sealed class LibraryMaterializer(
    ConfigurationAccessor configuration,
    CapabilityTokenService tokens,
    ISiphonStateStore state,
    ILibraryManager library,
    IItemPersistenceService persistence,
    ILogger<LibraryMaterializer> logger) : IHostedService
{
    private const string ItemProvider = "Siphon";
    private const string CatalogProvider = "SiphonCatalog";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await configuration.MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var retained = state.GetItems();
            if (retained.Count > 0 && string.IsNullOrWhiteSpace(configuration.Current.PublicBaseUrl))
            {
                logger.LogWarning("Siphon home catalogs are not published: configure PublicBaseUrl and synchronize catalogs");
                return;
            }
            await ApplyAsync(retained, [], cancellationToken, refreshExisting: false).ConfigureAwait(false);
        }
        finally
        {
            configuration.MutationGate.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ApplyAsync(IReadOnlyList<ManagedItem> retained, IReadOnlyList<ManagedItem> removals,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? catalogNames = null, bool refreshExisting = true)
    {
        var config = configuration.Current;
        var selected = config.Addons.Where(addon => addon.Enabled)
            .SelectMany(addon => addon.Catalogs.Where(catalog => catalog.Enabled).Select(catalog => (Addon: addon, Catalog: catalog)))
            .ToDictionary(entry => entry.Addon.Id + ":" + entry.Catalog.Key, StringComparer.Ordinal);
        var members = selected.Keys.ToDictionary(owner => owner, _ => new List<ManagedItem>(), StringComparer.Ordinal);
        foreach (var item in retained)
        {
            foreach (var owner in item.Owners)
            {
                if (members.TryGetValue(owner, out var items)) items.Add(item);
            }
        }

        if (members.Values.Any(items => items.Count > 0) && string.IsNullOrWhiteSpace(config.PublicBaseUrl))
        {
            throw new InvalidOperationException("Set the Public Jellyfin base URL before publishing catalogs on home.");
        }

        var roots = library.RootFolder.VirtualChildren.OfType<SiphonCatalogFolder>().ToDictionary(root => root.Id);
        var legacy = FindLegacyItems(retained.Concat(removals).Select(item => item.Key), cancellationToken);
        var adopted = new HashSet<Guid>();
        var activeRoots = new HashSet<Guid>();
        var published = 0;
        foreach (var (owner, entry) in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rootId = library.GetNewItemId("siphon:catalog:" + owner, typeof(SiphonCatalogFolder));
            var root = roots.GetValueOrDefault(rootId) ?? library.GetItemById<SiphonCatalogFolder>(rootId);
            if (root is null && members[owner].Count == 0) continue;
            var isNewRoot = root is null;
            root ??= new SiphonCatalogFolder { Id = rootId, DateCreated = DateTime.UtcNow };
            root.AddonId = entry.Addon.Id;
            root.CatalogKey = entry.Catalog.Key;
            root.CatalogType = entry.Catalog.Type;
            root.Published = members[owner].Count > 0;
            root.Name = catalogNames?.GetValueOrDefault(owner) ?? (string.IsNullOrWhiteSpace(root.Name)
                ? entry.Addon.DisplayName + " · " + entry.Catalog.Type + " · " + entry.Catalog.Id : root.Name);
            root.Path = "siphon://catalog/" + entry.Addon.Id + "/" + entry.Catalog.Key;
            root.IsLocked = true;
            root.SetParent(library.RootFolder);
            Save(root, isNewRoot, cancellationToken);
            if (!roots.ContainsKey(rootId))
            {
                library.RootFolder.AddVirtualChild(root);
                roots.Add(rootId, root);
            }

            activeRoots.Add(rootId);
            PublishItems(root, owner, members[owner], legacy, adopted, refreshExisting, cancellationToken);
            if (root.Published) published++;
        }

        foreach (var root in roots.Values.Where(root => !activeRoots.Contains(root.Id) && root.Published))
        {
            root.Published = false;
            Save(root, false, cancellationToken);
        }

        // The old existing-library mode is replaced, not kept as a second copy of the same catalog.
        foreach (var item in legacy.Values.SelectMany(items => items).Where(item => !adopted.Contains(item.Id)))
        {
            library.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
        }

        library.RootFolder.Children = null;
        logger.LogInformation("Siphon published {CatalogCount} catalog rows on Jellyfin home", published);
        return Task.CompletedTask;
    }

    private void PublishItems(SiphonCatalogFolder root, string owner, IReadOnlyList<ManagedItem> items,
        IReadOnlyDictionary<string, List<BaseItem>> legacy, HashSet<Guid> adopted, bool refreshExisting, CancellationToken cancellationToken)
    {
        var existing = library.GetItemList(new InternalItemsQuery
        {
            TopParentIds = [root.Id],
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode],
            GroupByPresentationUniqueKey = false
        }).ToDictionary(item => item.Id);
        var mediaByKey = existing.Values.Where(item => item is Movie or Episode)
            .Where(item => item.GetProviderId(ItemProvider) is not null)
            .ToDictionary(item => item.GetProviderId(ItemProvider)!, StringComparer.Ordinal);
        var wanted = new HashSet<Guid>();
        var series = new Dictionary<string, Series>(StringComparer.Ordinal);
        var seasons = new Dictionary<(string Content, int Number), Season>();
        var added = new List<BaseItem>();
        var updated = new List<BaseItem>();
        foreach (var managed in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Season? season = null;
            Series? show = null;
            if (managed.Type == "series")
            {
                if (!series.TryGetValue(managed.ContentKey, out show))
                {
                    var id = library.GetNewItemId(root.Id + ":series:" + managed.ContentKey, typeof(Series));
                    show = existing.GetValueOrDefault(id) as Series ?? new Series { Id = id };
                    if (refreshExisting || !existing.ContainsKey(id))
                    {
                        SetMetadata(show, managed, owner, managed.ContentKey, managed.ProviderIds);
                        show.Name = managed.SeriesName ?? managed.Name;
                        show.Overview = managed.SeriesDescription;
                        // Jellyfin uses this key for episode queries; catalogs must not merge their copies.
                        show.PresentationUniqueKey = id.ToString("N");
                        show.Path = root.Path + "/series/" + show.PresentationUniqueKey;
                        show.SetParent(root);
                        Save(show, !existing.ContainsKey(id), cancellationToken);
                    }
                    series.Add(managed.ContentKey, show);
                }
                wanted.Add(show.Id);

                var seasonKey = (managed.ContentKey, managed.Season ?? 0);
                if (!seasons.TryGetValue(seasonKey, out season))
                {
                    var id = library.GetNewItemId(show.Id + ":season:" + seasonKey.Item2, typeof(Season));
                    season = existing.GetValueOrDefault(id) as Season ?? new Season { Id = id };
                    if (refreshExisting || !existing.ContainsKey(id))
                    {
                        SetMetadata(season, managed, owner, managed.ContentKey + ":season:" + seasonKey.Item2, []);
                        season.Name = seasonKey.Item2 == 0 ? "Specials" : "Season " + seasonKey.Item2;
                        season.Path = show.Path + "/season/" + seasonKey.Item2;
                        season.IndexNumber = seasonKey.Item2;
                        season.SeriesId = show.Id;
                        season.SeriesName = show.Name;
                        season.SeriesPresentationUniqueKey = show.PresentationUniqueKey;
                        season.SetParent(show);
                        season.PresentationUniqueKey = season.CreatePresentationUniqueKey();
                        Save(season, !existing.ContainsKey(id), cancellationToken);
                    }
                    seasons.Add(seasonKey, season);
                }
                wanted.Add(season.Id);
            }

            var current = mediaByKey.GetValueOrDefault(managed.Key);
            current ??= legacy.GetValueOrDefault(managed.Key)?.FirstOrDefault(item => !adopted.Contains(item.Id));
            var isNew = current is null;
            BaseItem media = current ?? (managed.Type == "movie" ? new Movie() : new Episode());
            if (isNew) media.Id = library.GetNewItemId(root.Id + ":" + managed.Key, media.GetType());
            wanted.Add(media.Id);
            if (!refreshExisting && existing.ContainsKey(media.Id)) continue;
            if (current is not null && !existing.ContainsKey(current.Id)) adopted.Add(current.Id);

            SetMetadata(media, managed, owner, managed.Key, managed.Type == "movie" ? managed.ProviderIds : managed.EpisodeProviderIds);
            media.Path = configuration.Current.PublicBaseUrl.TrimEnd('/') + "/Siphon/s/" + tokens.SignItem(managed.Key);
            media.SetParent(season ?? (Folder)root);
            if (media is Episode episode)
            {
                episode.SeriesId = show!.Id;
                episode.SeriesName = show.Name;
                episode.SeriesPresentationUniqueKey = show.PresentationUniqueKey;
                episode.SeasonId = season!.Id;
                episode.SeasonName = season.Name;
                episode.IndexNumber = managed.Episode;
                episode.ParentIndexNumber = managed.Season;
            }
            media.PresentationUniqueKey = media.CreatePresentationUniqueKey();
            (isNew ? added : updated).Add(media);
        }

        foreach (var batch in added.Chunk(256)) library.CreateItems(batch, null, cancellationToken);
        foreach (var batch in updated.Chunk(256))
        {
            persistence.SaveItems(batch, cancellationToken);
            foreach (var item in batch) library.RegisterItem(item);
        }
        root.Children = null;
        foreach (var show in series.Values) show.Children = null;
        foreach (var season in seasons.Values) season.Children = null;

        foreach (var stale in existing.Values.Where(item => !wanted.Contains(item.Id))
            .OrderBy(item => item is Series ? 2 : item is Season ? 1 : 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stale is Folder folder) folder.Children = null;
            library.DeleteItem(stale, new DeleteOptions { DeleteFileLocation = false });
        }
    }

    private void SetMetadata(BaseItem target, ManagedItem item, string owner, string key, IEnumerable<KeyValuePair<string, string>> ids)
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
        target.ProviderIds = new Dictionary<string, string>(ids, StringComparer.OrdinalIgnoreCase)
        {
            [ItemProvider] = key,
            [CatalogProvider] = owner
        };
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

    private Dictionary<string, List<BaseItem>> FindLegacyItems(IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<BaseItem>>(StringComparer.Ordinal);
        foreach (var batch in keys.Distinct(StringComparer.Ordinal).Chunk(256))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = library.GetItemList(new InternalItemsQuery
            {
                HasAnyProviderIds = new Dictionary<string, string[]> { [ItemProvider] = batch },
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                GroupByPresentationUniqueKey = false
            });
            foreach (var item in items.Where(item => item.ChannelId == Guid.Empty && item.GetProviderId(CatalogProvider) is null))
            {
                var key = item.GetProviderId(ItemProvider);
                if (key is null) continue;
                if (!result.TryGetValue(key, out var group)) result[key] = group = [];
                group.Add(item);
            }
        }
        return result;
    }
}
