using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;

namespace Jellyfin.Plugin.Siphon.Search;

/// <summary>Filters native fallback results too, without changing unrelated local media or access rules.</summary>
public sealed class NativeSearchManager(ISearchManager inner, UserPreferenceStore preferences,
    ConfigurationAccessor configuration, ILibraryManager library) : ISearchManager
{
    public void AddParts(IEnumerable<ISearchProvider> providers) => inner.AddParts(providers);
    public IReadOnlyList<ISearchProvider> GetProviders() => inner.GetProviders();

    public async Task<IReadOnlyList<SearchResult>> GetSearchResultsAsync(SearchProviderQuery query, CancellationToken cancellationToken = default)
    {
        var results = await inner.GetSearchResultsAsync(query, cancellationToken).ConfigureAwait(false);
        if (query.UserId is not { } userId || !preferences.HidesUnreleased(userId)) return results;
        var hidden = UnreleasedIds(results.Select(result => result.ItemId));
        return hidden.Count == 0 ? results : results.Where(result => !hidden.Contains(result.ItemId)).ToArray();
    }

    public async Task<QueryResult<SearchHintInfo>> GetSearchHintsAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        if (query.UserId == Guid.Empty || !preferences.HidesUnreleased(query.UserId))
            return await inner.GetSearchHintsAsync(query, cancellationToken).ConfigureAwait(false);
        // Native hint DTOs omit provider IDs. Read ownership separately and paginate only
        // after filtering; otherwise a future first result can consume an entire page.
        var unpaged = new SearchQuery
        {
            UserId = query.UserId,
            SearchTerm = query.SearchTerm,
            // Null caps native provider candidates at 100; retain larger requested budgets.
            Limit = Math.Max(100, query.Limit ?? 100),
            StartIndex = null,
            IncludePeople = query.IncludePeople,
            IncludeMedia = query.IncludeMedia,
            IncludeGenres = query.IncludeGenres,
            IncludeStudios = query.IncludeStudios,
            IncludeArtists = query.IncludeArtists,
            MediaTypes = query.MediaTypes,
            IncludeItemTypes = query.IncludeItemTypes,
            ExcludeItemTypes = query.ExcludeItemTypes,
            ParentId = query.ParentId,
            IsMovie = query.IsMovie,
            IsSeries = query.IsSeries,
            IsNews = query.IsNews,
            IsKids = query.IsKids,
            IsSports = query.IsSports
        };
        var result = await inner.GetSearchHintsAsync(unpaged, cancellationToken).ConfigureAwait(false);
        var hidden = UnreleasedIds(result.Items.Select(hint => hint.Item.Id));
        var visible = result.Items.Where(hint => !hidden.Contains(hint.Item.Id)).ToArray();
        return new QueryResult<SearchHintInfo>(query.StartIndex, visible.Length,
            visible.Skip(query.StartIndex ?? 0).Take(query.Limit ?? int.MaxValue).ToArray());
    }

    private HashSet<Guid> UnreleasedIds(IEnumerable<Guid> candidates)
    {
        var ids = candidates.Distinct().ToArray();
        if (ids.Length == 0) return [];
        var now = DateTimeOffset.UtcNow;
        var bufferDays = configuration.Current.UnreleasedBufferDays;
        // GetItemById can return a cached, partially hydrated search item. One explicit
        // projection keeps ownership checks independent of cache/query field selection.
        return library.GetItemList(new InternalItemsQuery
        {
            ItemIds = ids,
            IncludeAlternateVersions = true,
            GroupByPresentationUniqueKey = false,
            EnableTotalRecordCount = false,
            DtoOptions = new DtoOptions { Fields = [ItemFields.ProviderIds] }
        }).Where(item => !AddonSearchPolicy.IsReleased(item, now, bufferDays)).Select(item => item.Id).ToHashSet();
    }
}
