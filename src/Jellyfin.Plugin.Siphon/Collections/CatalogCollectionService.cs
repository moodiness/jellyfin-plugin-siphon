using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Siphon.Collections;

/// <summary>Owns only the automatic links in explicitly marked native catalog collections.</summary>
public sealed class CatalogCollectionService(
    ConfigurationAccessor configuration,
    SiphonPaths paths,
    ILibraryManager library,
    ICollectionManager collections,
    IItemPersistenceService persistence)
{
    public const string OwnerProvider = "SiphonCatalogCollection";
    private readonly string _ledgerPath = Path.Combine(paths.DataDirectory, "catalog-collections.json");
    private readonly object _ledgerLock = new();
    private Dictionary<Guid, Membership>? _ledger;

    public bool IsDeliberateMember(Folder folder, Guid itemId)
    {
        if (string.IsNullOrEmpty(folder.GetProviderId(OwnerProvider))) return true;
        lock (_ledgerLock)
        {
            var ledger = LoadLedger();
            // Unknown provenance is never permission to delete a user's member.
            return !ledger.TryGetValue(folder.Id, out var entry) || entry.Deliberate.Contains(itemId) || !entry.Automatic.Contains(itemId);
        }
    }

    /// <summary>Called under the mutation gate only after the native request succeeds, including duplicate adds.</summary>
    public async Task RecordDeliberateAsync(Folder folder, IEnumerable<Guid> itemIds, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(folder.GetProviderId(OwnerProvider))) return;
        byte[]? document = null;
        lock (_ledgerLock)
        {
            var ledger = LoadLedger();
            if (!ledger.TryGetValue(folder.Id, out var entry)) ledger[folder.Id] = entry = new();
            var changed = false;
            foreach (var id in itemIds) changed |= entry.Deliberate.Add(id);
            if (changed) document = JsonSerializer.SerializeToUtf8Bytes(ledger);
        }
        if (document is not null) await SaveLedgerAsync(document, ct).ConfigureAwait(false);
    }

    public async Task ForgetDeliberateAsync(Folder folder, IEnumerable<Guid> itemIds, CancellationToken ct)
    {
        byte[]? document = null;
        lock (_ledgerLock)
        {
            if (!LoadLedger().TryGetValue(folder.Id, out var entry)) return;
            var changed = false;
            foreach (var id in itemIds) changed |= entry.Deliberate.Remove(id);
            if (changed) document = JsonSerializer.SerializeToUtf8Bytes(LoadLedger());
        }
        if (document is not null) await SaveLedgerAsync(document, ct).ConfigureAwait(false);
    }

    /// <summary>Call after native publication, while holding Siphon's synchronization and mutation gates.</summary>
    public async Task ReconcileAsync(IReadOnlyList<ManagedItem> retained, CancellationToken ct, IReadOnlySet<string>? scopedOwners = null)
    {
        var configured = configuration.Current.Addons.SelectMany(addon => addon.Catalogs.Select(catalog =>
            (Owner: addon.Id + ":" + catalog.Key, Catalog: catalog, Enabled: addon.Enabled && catalog.Enabled)))
            .ToDictionary(entry => entry.Owner, StringComparer.Ordinal);
        var owned = new Dictionary<string, BoxSet>(StringComparer.Ordinal);
        for (var start = 0; ; start += 256)
        {
            var page = library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.BoxSet],
                Recursive = true,
                GroupByPresentationUniqueKey = false,
                StartIndex = start,
                Limit = 256
            });
            foreach (var box in page.OfType<BoxSet>())
                if (box.GetProviderId(OwnerProvider) is { Length: > 0 } owner) owned.TryAdd(owner, box);
            if (page.Count < 256) break;
        }
        var wanted = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        foreach (var content in retained.GroupBy(item => item.ContentKey, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var first = content.First();
            var nativeId = library.GetNewItemId(first.Type == "series" ? "siphon:series:" + first.ContentKey : "siphon:media:" + first.Key,
                first.Type == "series" ? typeof(Series) : typeof(Movie));
            var native = library.GetItemById(nativeId);
            if (native is null || native.GetProviderId("Siphon") != (first.Type == "series" ? first.ContentKey : first.Key))
            {
                native = library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = [first.Type == "series" ? BaseItemKind.Series : BaseItemKind.Movie],
                    HasAnyProviderId = new Dictionary<string, string> { ["Siphon"] = first.Type == "series" ? first.ContentKey : first.Key },
                    Recursive = true,
                    GroupByPresentationUniqueKey = false
                }).FirstOrDefault(item => string.IsNullOrEmpty(item.GetProviderId(Playback.NativeVersionService.VersionProvider)));
            }
            if (native is null) continue;
            foreach (var owner in content.SelectMany(item => item.Owners.Where(owner => !item.MissingOwners.Contains(owner, StringComparer.Ordinal))).Distinct(StringComparer.Ordinal))
            {
                if (scopedOwners is not null && !scopedOwners.Contains(owner)) continue;
                if (!configured.TryGetValue(owner, out var entry) || !entry.Enabled || entry.Catalog.Presentation == "Library") continue;
                if (!wanted.TryGetValue(owner, out var ids)) wanted[owner] = ids = [];
                ids.Add(native.Id);
            }
        }
        foreach (var owner in configured.Keys.Concat(owned.Keys).Distinct(StringComparer.Ordinal))
        {
            if (scopedOwners is not null && !scopedOwners.Contains(owner)) continue;
            ct.ThrowIfCancellationRequested();
            var generate = configured.TryGetValue(owner, out var entry) && entry.Enabled && entry.Catalog.Presentation is "Collection" or "Both";
            wanted.TryGetValue(owner, out var desired);
            desired ??= [];
            owned.TryGetValue(owner, out var box);
            if (box is null)
            {
                // Do not expose an empty generated collection to users lacking its backing-library access.
                if (!generate || desired.Count == 0) continue;
                var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(owner)))[..16];
                var parent = await collections.GetCollectionsFolder(true).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Jellyfin could not create its collections folder.");
                if (Directory.Exists(Path.Combine(parent.Path, "Siphon " + hash + " [boxset]")))
                    throw new InvalidOperationException("A collection already occupies Siphon's generated storage path; it was not adopted.");
                // Creating an empty collection queues a native refresh before links
                // are added. Publish its initial members in the same native operation.
                box = await collections.CreateCollectionAsync(new CollectionCreationOptions
                {
                    Name = "Siphon " + hash,
                    IsLocked = true,
                    ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [OwnerProvider] = owner },
                    ItemIdList = [.. desired.Select(static id => id.ToString("N"))]
                }).ConfigureAwait(false);
                // The storage name is private/stable; the display name follows the native catalog's custom name.
                var view = library.GetVirtualFolders().Select(info => Guid.TryParse(info.ItemId, out var id) ? library.GetItemById(id) : null)
                    .FirstOrDefault(item => item?.GetProviderId(CatalogLibraryService.CatalogProvider) == owner);
                box.Name = view?.Name ?? entry.Catalog.Id;
                lock (_ledgerLock) LoadLedger().TryAdd(box.Id, new() { Automatic = new(desired) });
            }
            if (!box.LinkedChildrenLoaded)
                box = library.GetItemById<BoxSet>(box.Id) ?? throw new InvalidOperationException("The catalog collection disappeared during reconciliation.");
            if (!box.LinkedChildrenLoaded) throw new InvalidOperationException("Native collection membership was not loaded.");
            Membership previous;
            lock (_ledgerLock)
            {
                previous = LoadLedger().TryGetValue(box.Id, out var stored)
                    ? new Membership { Automatic = new(stored.Automatic), Deliberate = new(stored.Deliberate) } : new();
            }
            var current = box.LinkedChildren;
            var currentIds = current.Where(link => link.ItemId.HasValue).Select(link => link.ItemId!.Value).ToHashSet();
            var removals = currentIds.Where(id => previous.Automatic.Contains(id)
                && !previous.Deliberate.Contains(id) && !desired.Contains(id)).ToArray();
            var additions = desired.Where(id => !currentIds.Contains(id)).ToArray();
            var automatic = desired.Where(id => !currentIds.Contains(id) || previous.Automatic.Contains(id)).ToHashSet();
            var present = currentIds.Except(removals).Concat(additions).ToHashSet();
            previous.Deliberate.IntersectWith(present);
            byte[] document;
            lock (_ledgerLock)
            {
                // Keep old automatic ownership until removal commits. Interrupted reconciliation
                // must not reclassify a stale generated link as a deliberate user membership.
                LoadLedger()[box.Id] = new Membership
                {
                    Automatic = previous.Automatic.Concat(automatic).ToHashSet(),
                    Deliberate = previous.Deliberate
                };
                document = JsonSerializer.SerializeToUtf8Bytes(LoadLedger());
            }
            await SaveLedgerAsync(document, ct).ConfigureAwait(false);
            var displayName = box.Name;
            if (removals.Length > 0) await collections.RemoveFromCollectionAsync(box.Id, removals).ConfigureAwait(false);
            if (additions.Length > 0) await collections.AddToCollectionAsync(box.Id, additions).ConfigureAwait(false);
            box = library.GetItemById<BoxSet>(box.Id) ?? throw new InvalidOperationException("The catalog collection disappeared during reconciliation.");
            box.Name = displayName;
            box.LibraryFolderIds = null;
            box.DateLastSaved = DateTime.UtcNow;
            persistence.SaveItems([box], ct);
            library.RegisterItem(box);
            box.Children = null;
            if (box.LinkedChildrenLoaded && box.LinkedChildren.Length == 0)
            {
                // Only an explicitly owned, empty generated object is removed; no manual
                // member or unrelated native collection is touched.
                library.DeleteItem(box, new DeleteOptions { DeleteFileLocation = true });
            }
            lock (_ledgerLock)
            {
                if (box.LinkedChildrenLoaded && box.LinkedChildren.Length == 0) LoadLedger().Remove(box.Id);
                else LoadLedger()[box.Id] = new Membership { Automatic = automatic, Deliberate = previous.Deliberate };
                document = JsonSerializer.SerializeToUtf8Bytes(LoadLedger());
            }
            await SaveLedgerAsync(document, ct).ConfigureAwait(false);
        }
    }

    private Dictionary<Guid, Membership> LoadLedger()
    {
        if (_ledger is not null) return _ledger;
        _ledger = File.Exists(_ledgerPath)
            ? JsonSerializer.Deserialize<Dictionary<Guid, Membership>>(File.ReadAllBytes(_ledgerPath))
                ?? throw new InvalidDataException("The catalog collection ownership ledger is invalid.")
            : [];
        return _ledger;
    }

    private async Task SaveLedgerAsync(byte[] document, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_ledgerPath)!);
        var temporary = _ledgerPath + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, document, ct).ConfigureAwait(false);
            File.Move(temporary, _ledgerPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public sealed class Membership
    {
        public HashSet<Guid> Automatic { get; init; } = [];
        public HashSet<Guid> Deliberate { get; init; } = [];
    }
}
