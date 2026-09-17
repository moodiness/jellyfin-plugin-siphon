using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Playback;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Siphon.Collections;

/// <summary>Resolves deliberate native membership without allowing generated catalogs to pin their own stale titles.</summary>
public sealed class CollectionRetentionService(
    ILibraryManager library,
    ISiphonStateStore state,
    CatalogCollectionService catalogs)
{
    public BaseItem? Canonicalize(Guid id)
    {
        var item = library.GetItemById(id);
        if (item is not Movie and not Episode || item.GetProviderId("Siphon") is not { Length: > 0 } key
            || state.ReadByKey(key) is null) return item;
        var video = (Video)item;
        var primaryId = Guid.TryParse(item.GetProviderId(NativeVersionService.VersionProvider), out var marked)
            ? marked : video.PrimaryVersionId;
        if (primaryId is null || primaryId == item.Id) return item;
        var primary = library.GetItemById(primaryId.Value);
        // A stale/malicious version pointer must never adopt a personal title or another episode.
        if (primary is Video canonical && canonical.GetType() == item.GetType()
            && canonical.GetProviderId("Siphon") == key && canonical.PrimaryVersionId is null
            && string.IsNullOrEmpty(canonical.GetProviderId(NativeVersionService.VersionProvider))) return primary;
        return library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [item is Movie ? BaseItemKind.Movie : BaseItemKind.Episode],
            HasAnyProviderId = new Dictionary<string, string> { ["Siphon"] = key },
            GroupByPresentationUniqueKey = false,
            Recursive = true
        }).OfType<Video>().FirstOrDefault(candidate => candidate.PrimaryVersionId is null
            && string.IsNullOrEmpty(candidate.GetProviderId(NativeVersionService.VersionProvider)));
    }

    public string? ContentKey(BaseItem? item)
    {
        if (item?.GetProviderId("Siphon") is not { Length: > 0 } key) return null;
        var managed = state.ReadByKey(key) ?? state.ReadByContentKey(key);
        if (managed is not null) return managed.ContentKey;
        if (item is Season season)
        {
            var series = library.GetItemById(season.SeriesId);
            if (series?.GetProviderId("Siphon") is { Length: > 0 } seriesKey)
                return state.ReadByContentKey(seriesKey)?.ContentKey;
            var separator = key.LastIndexOf(":season:", StringComparison.Ordinal);
            if (separator > 0) return state.ReadByContentKey(key[..separator])?.ContentKey;
        }
        return null;
    }

    /// <summary>Returns content keys, not episode/version keys; callers must protect the whole managed title.</summary>
    public HashSet<string> GetProtectedContentKeys()
    {
        var protectedKeys = new HashSet<string>(StringComparer.Ordinal);
        var expanded = new HashSet<(Guid Id, bool Intentional)>();
        for (var start = 0; ; start += 256)
        {
            var page = library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.BoxSet, BaseItemKind.Playlist],
                Recursive = true,
                GroupByPresentationUniqueKey = false,
                StartIndex = start,
                Limit = 256
            });
            foreach (var folder in page.OfType<Folder>())
                Visit(folder, false);
            if (page.Count < 256) break;
        }
        return protectedKeys;

        void Visit(BaseItem item, bool intentional)
        {
            if (ContentKey(item) is { } key) protectedKeys.Add(key);
            if (item is not BoxSet and not Playlist || !expanded.Add((item.Id, intentional))) return;
            var folder = LoadMembership((Folder)item);
            foreach (var link in folder.GetLinkedChildrenInfos())
            {
                // A manually nested generated collection is an intentional membership in its titles.
                var deliberate = intentional || item is Playlist || catalogs.IsDeliberateMember(folder, link.Item2.Id);
                if (deliberate) Visit(link.Item2, true);
            }
        }
    }

    internal Folder LoadMembership(Folder folder)
    {
        if (folder.LinkedChildrenLoaded) return folder;
        if (library.GetItemById(folder.Id) is Folder loaded && loaded.LinkedChildrenLoaded) return loaded;
        // Unknown is not empty. Cleanup must abort instead of interpreting an unloaded membership as consent to delete.
        throw new InvalidOperationException("Jellyfin did not load native collection or playlist memberships.");
    }

    internal Guid[] Normalize(IEnumerable<Guid> requested, IEnumerable<Guid> existing)
    {
        var managedSeen = new HashSet<Guid>();
        foreach (var id in existing)
        {
            var canonical = Canonicalize(id);
            if (ContentKey(canonical) is not null) managedSeen.Add(canonical!.Id);
        }
        var result = new List<Guid>();
        foreach (var id in requested)
        {
            var canonical = Canonicalize(id);
            if (ContentKey(canonical) is null) result.Add(id); // Native duplicate/order behavior for personal media is untouched.
            else if (managedSeen.Add(canonical!.Id)) result.Add(canonical.Id);
        }
        return result.ToArray();
    }
}
