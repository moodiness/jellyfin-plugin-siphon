using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Protocol;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.Siphon.Identity;

/// <summary>Registers one native library per catalog, sharing media without duplicating their identities.</summary>
public sealed class CatalogLibraryService(
    ConfigurationAccessor configuration,
    SiphonPaths paths,
    AddonRegistry registry,
    ILibraryManager library,
    IItemPersistenceService persistence,
    IUserManager users,
    IFileSystem fileSystem)
{
    internal const string CatalogProvider = "SiphonCatalog";
    public const string PresentationProvider = "SiphonCatalogPresentation";
    private const string StorageProvider = "SiphonStorage";
    internal const string ManualPrefix = "siphon:manual:";
    internal const string PreviewPrefix = "siphon:preview:";
    private readonly string _catalogPath = Path.Combine(paths.DataDirectory, "catalogs");
    private Dictionary<Guid, Dictionary<Guid, HashSet<PreferenceKind>>>? _presentationExclusions;

    public Layout Prepare(IReadOnlyList<ManagedItem> retained, CancellationToken cancellationToken)
    {
        var ownersByContent = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var item in retained)
        {
            if (!ownersByContent.TryGetValue(item.ContentKey, out var owners))
                ownersByContent[item.ContentKey] = owners = new(StringComparer.Ordinal);
            owners.UnionWith(item.Owners);
        }

        var roots = new Dictionary<string, Folder>(StringComparer.Ordinal);
        var contentRoots = new Dictionary<string, Folder>(StringComparer.Ordinal);
        var locations = new Dictionary<string, HashSet<Folder>>(StringComparer.Ordinal);
        foreach (var (content, owners) in ownersByContent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A physical root represents exactly one set of catalog memberships. Native
            // latest, search and library permissions then use the real media TopParentId.
            var membership = string.Join('\n', owners);
            if (!roots.TryGetValue(membership, out var root))
                roots[membership] = root = EnsureFolder(Path.Combine(_catalogPath, "media", Hash(membership)), cancellationToken);
            contentRoots.Add(content, root);
            foreach (var owner in owners)
            {
                if (!locations.TryGetValue(owner, out var members)) locations[owner] = members = [];
                members.Add(root);
            }
        }
        return new(contentRoots, locations);
    }

    public async Task RegisterAsync(Layout layout, IReadOnlyDictionary<string, string>? catalogNames, CancellationToken cancellationToken,
        IReadOnlySet<string>? scopedOwners = null)
    {
        var virtualFolders = library.GetVirtualFolders();
        if (virtualFolders.Any(info => !Guid.TryParse(info.ItemId, out _)))
        {
            // Native rename/path edits can leave a disk-backed view without its item
            // until Jellyfin validates the root. Recover it before reconciling catalogs.
            await library.ValidateTopLibraryFolders(cancellationToken, true).ConfigureAwait(false);
            virtualFolders = library.GetVirtualFolders();
        }
        var existing = virtualFolders
            .Select(info => Guid.TryParse(info.ItemId, out var id) ? library.GetItemById<CollectionFolder>(id) : null)
            .OfType<CollectionFolder>()
            .Where(folder => !string.IsNullOrEmpty(folder.GetProviderId(CatalogProvider)))
            .ToDictionary(folder => folder.GetProviderId(CatalogProvider)!, StringComparer.Ordinal);
        var configured = configuration.Current.Addons.SelectMany(addon => addon.Catalogs.Select(catalog =>
            (Owner: addon.Id + ":" + catalog.Key, Catalog: catalog, Enabled: addon.Enabled && catalog.Enabled)))
            .ToDictionary(entry => entry.Owner, StringComparer.Ordinal);
        foreach (var type in new[] { "movie", "series" })
        {
            configured[ManualPrefix + type] = (ManualPrefix + type, new CatalogSubscription { Type = type, Id = "Siphon saved" }, true);
            configured[PreviewPrefix + type] = (PreviewPrefix + type, new CatalogSubscription { Type = type, Id = "Siphon search" }, true);
        }
        if (catalogNames is null && layout.Locations.Count > 0
            && configured.Values.Any(entry => entry.Enabled && !entry.Owner.StartsWith("siphon:", StringComparison.Ordinal) && !existing.ContainsKey(entry.Owner)))
        {
            // Resolve labels once when upgrading the old single library. Later starts
            // reuse persisted native names without requiring the addons to be online.
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var addon in await registry.GetEnabledAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var subscription in addon.Configuration.Catalogs)
                {
                    var definition = addon.Manifest.Catalogs.FirstOrDefault(catalog => catalog.Type == subscription.Type && catalog.Id == subscription.Id);
                    if (definition is not null) names[addon.Configuration.Id + ":" + subscription.Key] = definition.Name;
                }
            }
            catalogNames = names;
        }
        var owners = configured.Keys.Concat(layout.Locations.Keys).Concat(existing.Keys).Distinct(StringComparer.Ordinal);
        foreach (var owner in owners)
        {
            if (scopedOwners is not null && !scopedOwners.Contains(owner)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var hasConfiguration = configured.TryGetValue(owner, out var subscription);
            var enabled = hasConfiguration && subscription.Enabled;
            existing.TryGetValue(owner, out var view);
            layout.Locations.TryGetValue(owner, out var members);
            if (view is null && !enabled && members is null) continue;
            if (owner.StartsWith("siphon:", StringComparison.Ordinal) && view is null && members is null) continue;

            // A permanent empty location identifies an empty library and lets an
            // interrupted registration recover before its provider marker was saved.
            var anchor = EnsureFolder(Path.Combine(_catalogPath, "views", Hash(owner)), cancellationToken);
            var folders = (members ?? []).Append(anchor).ToArray();
            var locations = folders.Select(folder => folder.Path).Order(StringComparer.Ordinal).ToArray();
            if (view is null)
            {
                var recovered = virtualFolders.FirstOrDefault(info => info.Locations.Any(location => fileSystem.AreEqual(location, anchor.Path)));
                if (recovered is not null)
                {
                    view = Guid.TryParse(recovered.ItemId, out var recoveredId)
                        ? library.GetItemById<CollectionFolder>(recoveredId)
                        : null;
                    if (view is null) throw new InvalidOperationException("Jellyfin could not recover the existing catalog library.");
                }
            }

            var type = hasConfiguration ? subscription.Catalog.Type : null;
            var typeName = type switch { "movie" => "Movies", "series" => "Series", "anime" => "Anime", _ => type };
            var name = view?.Name ?? (catalogNames is not null && catalogNames.TryGetValue(owner, out var title)
                ? title + " — " + typeName
                : hasConfiguration ? subscription.Catalog.Id + " — " + typeName : "Retained catalog");
            if (view is null)
            {
                var before = virtualFolders.Select(info => info.ItemId).ToHashSet(StringComparer.Ordinal);
                await library.AddVirtualFolder(name, type switch
                {
                    "movie" => CollectionTypeOptions.movies,
                    "series" => CollectionTypeOptions.tvshows,
                    _ => (CollectionTypeOptions?)null
                }, new LibraryOptions
                {
                    Enabled = false,
                    PathInfos = locations.Select(location => new MediaPathInfo(location)).ToArray(),
                    EnableRealtimeMonitor = false,
                    SaveLocalMetadata = false
                }, false).ConfigureAwait(false);
                virtualFolders = library.GetVirtualFolders();
                var created = virtualFolders.First(info => !before.Contains(info.ItemId)
                    && info.Locations.Any(location => fileSystem.AreEqual(location, anchor.Path)));
                view = library.GetItemById<CollectionFolder>(Guid.Parse(created.ItemId))
                    ?? throw new InvalidOperationException("Jellyfin could not register the catalog library.");
            }
            else
            {
                var info = virtualFolders.First(folder => Guid.TryParse(folder.ItemId, out var id) && id == view.Id);
                var oldPaths = info.Locations.ToHashSet(StringComparer.Ordinal);
                foreach (var location in locations)
                {
                    if (oldPaths.Add(location)) library.CreateShortcut(view.Path, new MediaPathInfo(location));
                }
                // AddVirtualFolder validates the whole physical root. Keep every old
                // location registered until all media have moved and all views exist.
                var options = view.GetLibraryOptions();
                options.PathInfos = oldPaths.Select(location => new MediaPathInfo(location)).ToArray();
                view.UpdateLibraryOptions(options);
            }

            view.Name = name;
            view.IsLocked = true;
            view.SetProviderId(CatalogProvider, owner);
            view.SetProviderId(PresentationProvider, hasConfiguration ? subscription.Catalog.Presentation : "Library");
            Save(view, false, cancellationToken);
            layout.Views.Add(new(view, folders, enabled));
            await ApplyPresentationAsync(view, hasConfiguration && subscription.Catalog.Presentation == "Collection", cancellationToken).ConfigureAwait(false);
            if (owner.StartsWith(PreviewPrefix, StringComparison.Ordinal))
            {
                foreach (var user in users.GetUsers())
                {
                    var changed = false;
                    foreach (var preference in new[] { PreferenceKind.MyMediaExcludes, PreferenceKind.LatestItemExcludes })
                    {
                        var values = user.GetPreference(preference);
                        if (values.Any(value => Guid.TryParse(value, out var id) && id == view.Id)) continue;
                        user.SetPreference(preference, values.Append(view.Id.ToString("N")).ToArray());
                        changed = true;
                    }
                    if (changed) await users.UpdateUserAsync(user).ConfigureAwait(false);
                }
            }
        }

        // Install restrictions while replacement views are still empty/disabled.
        // Retain access to the old view until all of its media have moved.
        var replacementIds = layout.Views.Select(entry => entry.View.Id).ToArray();
        foreach (var old in virtualFolders.Where(info => scopedOwners is null && info.Locations.Any(location => fileSystem.AreEqual(location, paths.LibraryDirectory))))
        {
            if (Guid.TryParse(old.ItemId, out var oldId))
                await MigratePreferencesAsync(oldId, replacementIds, true, cancellationToken).ConfigureAwait(false);
        }

        // New roots were deliberately unattached while views were registered: a native
        // root validation must never delete a future catalog's not-yet-linked directory.
        foreach (var root in layout.Views.SelectMany(view => view.Folders).DistinctBy(folder => folder.Id))
        {
            if (root.ParentId == library.RootFolder.Id) continue;
            root.SetParent(library.RootFolder);
            Save(root, false, cancellationToken);
        }
        library.RootFolder.Children = null;
    }

    public async Task PublishAsync(Layout layout, CancellationToken cancellationToken, bool completeLayout = true)
    {
        var virtualFolders = library.GetVirtualFolders();
        foreach (var entry in layout.Views)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var view = entry.View;
            var info = virtualFolders.First(folder => Guid.TryParse(folder.ItemId, out var id) && id == view.Id);
            var locations = entry.Folders.Select(folder => folder.Path).ToHashSet(StringComparer.Ordinal);
            // A user may add personal locations to this native library. Reconcile only
            // Siphon's own roots, never remove those unrelated paths during a resync.
            foreach (var location in info.Locations)
            {
                if (library.FindByPath(location, true)?.GetProviderId(StorageProvider) != "1")
                    locations.Add(location);
            }
            foreach (var obsolete in info.Locations.Where(location => !locations.Contains(location)))
                library.RemoveMediaPath(info.Name, obsolete);
            var options = view.GetLibraryOptions();
            options.Enabled = entry.Enabled;
            options.PathInfos = locations.Order(StringComparer.Ordinal).Select(location => new MediaPathInfo(location)).ToArray();
            view.UpdateLibraryOptions(options);
            await view.RefreshMetadata(cancellationToken).ConfigureAwait(false);
            if (entry.Folders.Any(folder => !view.PhysicalFolderIds.Contains(folder.Id)))
                throw new InvalidOperationException("Jellyfin could not link the catalog's managed locations.");
            Save(view, false, cancellationToken);
            view.Children = null;
        }

        if (!completeLayout)
        {
            library.RootFolder.Children = null;
            library.GetUserRootFolder().Children = null;
            return;
        }

        // Finalize preferences after moving media, retaining policies for unrelated locations.
        var legacy = virtualFolders.Where(info => info.Locations.Any(location => fileSystem.AreEqual(location, paths.LibraryDirectory))).ToArray();
        var replacementIds = layout.Views.Select(entry => entry.View.Id).ToArray();
        foreach (var old in legacy)
        {
            var oldId = Guid.TryParse(old.ItemId, out var parsed) ? parsed : Guid.Empty;
            var retainLegacy = old.Locations.Length > 1;
            if (oldId != Guid.Empty) await MigratePreferencesAsync(oldId, replacementIds, retainLegacy, cancellationToken).ConfigureAwait(false);
            if (retainLegacy)
            {
                library.RemoveMediaPath(old.Name, paths.LibraryDirectory);
                continue;
            }
            // Keep the identifying shortcut until the directory is removed so cleanup
            // can discover and retry this view if deletion fails or is interrupted.
            await library.RemoveVirtualFolder(old.Name, false).ConfigureAwait(false);
            // Removing the folder only deletes it on disk. Its record must go too, or the
            // library keeps appearing in navigation and My Media with no content behind it.
            if (oldId != Guid.Empty) persistence.DeleteItem([oldId]);
        }
        if (legacy.Length > 0) await library.ValidateTopLibraryFolders(cancellationToken, true).ConfigureAwait(false);

        // Disabled libraries keep retained items reachable by the native scan machinery.
        // Remove only empty, plugin-owned physical roots, never their media here.
        var wantedRoots = layout.Views.SelectMany(entry => entry.Folders).Select(folder => folder.Id).ToHashSet();
        foreach (var folder in library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Folder],
            ParentId = library.RootFolder.Id,
            GroupByPresentationUniqueKey = false
        }).OfType<Folder>())
        {
            if (wantedRoots.Contains(folder.Id) || folder.GetProviderId(StorageProvider) != "1") continue;
            if (library.GetItemList(new InternalItemsQuery { ParentId = folder.Id, Limit = 1 }).Count != 0) continue;
            persistence.DeleteItem([folder.Id]);
            if (Directory.Exists(folder.Path) && !Directory.EnumerateFileSystemEntries(folder.Path).Any()) Directory.Delete(folder.Path);
        }
        library.RootFolder.Children = null;
        library.GetUserRootFolder().Children = null;
    }

    private async Task MigratePreferencesAsync(Guid oldId, Guid[] replacements, bool retainLegacy, CancellationToken cancellationToken)
    {
        var migratedIds = (retainLegacy ? replacements.Prepend(oldId) : replacements)
            .Select(id => id.ToString("N")).ToArray();
        foreach (var user in users.GetUsers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var changed = false;
            foreach (var kind in new[] { PreferenceKind.EnabledFolders, PreferenceKind.EnableContentDeletionFromFolders,
                PreferenceKind.BlockedMediaFolders, PreferenceKind.MyMediaExcludes, PreferenceKind.LatestItemExcludes, PreferenceKind.OrderedViews })
            {
                var values = user.GetPreference(kind);
                if (!values.Any(value => Guid.TryParse(value, out var id) && id == oldId)) continue;
                user.SetPreference(kind, values.SelectMany(value => Guid.TryParse(value, out var id) && id == oldId
                    ? migratedIds : [value]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
                changed = true;
            }
            var grouped = user.GetPreference(PreferenceKind.GroupedFolders);
            if (!retainLegacy && grouped.Any(value => Guid.TryParse(value, out var id) && id == oldId))
            {
                user.SetPreference(PreferenceKind.GroupedFolders, grouped.Where(value => !Guid.TryParse(value, out var id) || id != oldId).ToArray());
                changed = true;
            }
            if (changed) await users.UpdateUserAsync(user).ConfigureAwait(false);
        }
    }

    private Folder EnsureFolder(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(path);
        if (library.FindByPath(path, true) is Folder existing) return existing;
        var folder = new Folder
        {
            Id = library.GetNewItemId(path, typeof(Folder)),
            Name = Path.GetFileName(path),
            Path = path,
            DateCreated = DateTime.UtcNow
        };
        folder.SetProviderId(StorageProvider, "1");
        Save(folder, true, cancellationToken);
        return folder;
    }

    private void Save(BaseItem item, bool isNew, CancellationToken cancellationToken)
    {
        item.DateLastSaved = DateTime.UtcNow;
        if (isNew) library.CreateItems([item], null, cancellationToken);
        else
        {
            persistence.SaveItems([item], cancellationToken);
            library.RegisterItem(item);
        }
    }

    private async Task ApplyPresentationAsync(CollectionFolder view, bool collectionOnly, CancellationToken ct)
    {
        // Retain the original permission-bearing view and shared media locations. Combining
        // catalogs into one visible library would silently broaden independently configured ACLs.
        var path = Path.Combine(_catalogPath, "presentation-exclusions.json");
        _presentationExclusions ??= File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<Guid, Dictionary<Guid, HashSet<PreferenceKind>>>>(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false))
                ?? throw new InvalidDataException("The catalog presentation ownership record is invalid.")
            : [];
        if (!_presentationExclusions.TryGetValue(view.Id, out var owned))
            _presentationExclusions[view.Id] = owned = [];
        var pending = owned.ToDictionary(pair => pair.Key, pair => new HashSet<PreferenceKind>(pair.Value));
        var changedUsers = new List<Jellyfin.Database.Implementations.Entities.User>();
        var changed = false;
        foreach (var user in users.GetUsers())
        {
            if (!owned.TryGetValue(user.Id, out var preferences)) owned[user.Id] = preferences = [];
            var changedUser = false;
            foreach (var kind in new[] { PreferenceKind.MyMediaExcludes, PreferenceKind.LatestItemExcludes })
            {
                var values = user.GetPreference(kind);
                var contains = values.Any(value => Guid.TryParse(value, out var id) && id == view.Id);
                if (collectionOnly && !contains)
                {
                    preferences.Add(kind);
                    user.SetPreference(kind, values.Append(view.Id.ToString("N")).ToArray());
                    changedUser = true;
                    changed = true;
                }
                else if (!collectionOnly && preferences.Remove(kind))
                {
                    // Exclusions that existed before collection-only mode remain untouched.
                    if (contains)
                    {
                        user.SetPreference(kind, values.Where(value => !Guid.TryParse(value, out var id) || id != view.Id).ToArray());
                        changedUser = true;
                    }
                    changed = true;
                }
            }
            if (changedUser) changedUsers.Add(user);
        }
        if (!changed) return;
        Directory.CreateDirectory(_catalogPath);
        foreach (var (userId, preferences) in owned)
        {
            if (!pending.TryGetValue(userId, out var kinds)) pending[userId] = kinds = [];
            kinds.UnionWith(preferences);
        }
        // Keep removal intent in the journal until the native preference updates commit.
        // If interrupted, switching back retries rather than leaving owned exclusions forever.
        _presentationExclusions[view.Id] = pending;
        await SaveExclusionsAsync().ConfigureAwait(false);
        foreach (var user in changedUsers) await users.UpdateUserAsync(user).ConfigureAwait(false);
        _presentationExclusions[view.Id] = owned;
        await SaveExclusionsAsync().ConfigureAwait(false);

        async Task SaveExclusionsAsync()
        {
            var temporary = path + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(_presentationExclusions), ct).ConfigureAwait(false);
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public sealed record Layout(Dictionary<string, Folder> ContentRoots, Dictionary<string, HashSet<Folder>> Locations)
    {
        public List<CatalogView> Views { get; } = [];
    }

    public sealed record CatalogView(CollectionFolder View, Folder[] Folders, bool Enabled);
}
