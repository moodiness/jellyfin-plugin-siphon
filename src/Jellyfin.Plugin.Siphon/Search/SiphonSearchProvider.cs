using System.Runtime.CompilerServices;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Siphon.Search;

/// <summary>Native Jellyfin search extension; retains local results when external discoveries exist.</summary>
public sealed class SiphonSearchProvider(AddonSearchService search, ISearchManager manager, UserPreferenceStore preferences, ILogger<SiphonSearchProvider> logger) : IExternalSearchProvider
{
    public string Name => "Siphon addons";
    public MetadataPluginType Type => MetadataPluginType.SearchProvider;
    public int Priority => 100;
    public bool CanSearch(SearchProviderQuery query) => query.SearchTerm.Trim().Length is >= 2 and <= 160
        && query.UserId.HasValue && !preferences.UsesLocalSearch(query.UserId.Value)
        && (!query.ParentId.HasValue || query.ParentId == Guid.Empty)
        && (AddonSearchPolicy.Allows(query, "movie") || AddonSearchPolicy.Allows(query, "series"));

    async Task<IReadOnlyList<SearchResult>> ISearchProvider.SearchAsync(SearchProviderQuery query, CancellationToken cancellationToken)
    {
        var results = new List<SearchResult>();
        await foreach (var result in SearchAsync(query, cancellationToken).ConfigureAwait(false)) results.Add(result);
        return results;
    }

    public async IAsyncEnumerable<SearchResult> SearchAsync(SearchProviderQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (query.UserId is not { } userId || preferences.UsesLocalSearch(userId)) yield break;
        IReadOnlyList<SearchItem> discoveries;
        try { discoveries = await search.SearchAsync(query, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            logger.LogDebug("Siphon addon search is unavailable; native search remains available.");
            discoveries = [];
        }
        if (discoveries.Count == 0) yield break; // SearchManager retains its normal internal fallback.
        var seen = new HashSet<Guid>();
        // Jellyfin chooses external results over its internal fallback, rather than merging.
        // Include the installed internal providers' results so an addon cannot hide local media.
        foreach (var provider in manager.GetProviders().OfType<IInternalSearchProvider>().Where(provider => provider.CanSearch(query)))
        {
            IReadOnlyList<SearchResult> local;
            try { local = await provider.SearchAsync(query, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { continue; }
            foreach (var result in local)
                if (seen.Add(result.ItemId)) yield return result with { Score = Math.Max(result.Score, 2f) };
        }
        foreach (var item in discoveries)
            if (seen.Add(item.Id)) yield return new SearchResult(item.Id, 1f);
    }
}
