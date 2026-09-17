using System.Reflection;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Search;
using Jellyfin.Plugin.Siphon.Tests.Infrastructure;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Search;

public sealed class UserSearchPreferenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "siphon-preferences-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SavingAnotherUserDoesNotReplaceOverridesAndInheritanceRemainsLiveAfterRestart()
    {
        var config = new PluginConfiguration();
        var configuration = new ConfigurationAccessor(() => config);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        using (var store = new UserPreferenceStore(Paths(), configuration))
        {
            await store.SaveAsync(first, new UserPreferences { SearchMode = "Local", HideUnreleased = false }, default);
            await store.SaveAsync(second, new UserPreferences { NotificationsEnabled = true }, default);
            Assert.True(store.UsesLocalSearch(first));
            Assert.False(store.UsesLocalSearch(second));
        }
        config.DefaultSearchMode = "Local";
        config.HideUnreleased = true;
        using var restarted = new UserPreferenceStore(Paths(), configuration);
        Assert.True(restarted.UsesLocalSearch(second));
        Assert.False(restarted.HidesUnreleased(first));
        Assert.True(restarted.HidesUnreleased(second));
        Assert.True(restarted.Get(second).NotificationsEnabled);
        await restarted.SaveAsync(second, new UserPreferences { SearchMode = "All" }, default);
        Assert.True(restarted.UsesLocalSearch(first));
        Assert.False(restarted.UsesLocalSearch(second));
    }

    [Fact]
    public async Task UnreleasedFilterAppliesToNativeFallbackBeforeHintPaginationWithoutHidingPersonalMedia()
    {
        var configuration = new ConfigurationAccessor(() => new PluginConfiguration());
        var user = Guid.NewGuid();
        using var preferences = new UserPreferenceStore(Paths(), configuration);
        await preferences.SaveAsync(user, new UserPreferences { HideUnreleased = true, SearchMode = "Local" }, default);
        var future = Movie("Future Siphon", true, DateTime.UtcNow.AddYears(1));
        var released = Movie("Released Siphon", true, DateTime.UtcNow.AddYears(-1));
        var personal = Movie("Personal future dated media", false, DateTime.UtcNow.AddYears(1));
        BaseItem[] items = [future, released, personal];
        // Native search/cached items may omit provider IDs until explicitly requested.
        var partial = items.Select(item => new Movie
        {
            Id = item.Id,
            Name = item.Name,
            PremiereDate = item.PremiereDate,
            ProductionYear = item.ProductionYear
        }).ToArray();
        var library = DispatchProxy.Create<ILibraryManager, NativeSearchBehaviorTests.CallbackProxy>();
        ((NativeSearchBehaviorTests.CallbackProxy)(object)library).InvokeMethod = (method, args) => method.Name switch
        {
            "GetItemById" => partial.Single(item => item.Id == (Guid)args![0]!),
            "GetItemList" when args![0] is InternalItemsQuery native => (native.DtoOptions.Fields.Contains(ItemFields.ProviderIds)
                ? items : partial).Where(item => native.ItemIds.Contains(item.Id)).ToArray(),
            _ => throw new NotSupportedException(method.Name)
        };
        var manager = new NativeSearchManager(new NativeResults(partial), preferences, configuration, library);
        var results = await manager.GetSearchResultsAsync(new SearchProviderQuery { UserId = user, SearchTerm = "fixture" });
        Assert.Equal(new[] { released.Id, personal.Id }, results.Select(result => result.ItemId));
        var hints = await manager.GetSearchHintsAsync(new SearchQuery { UserId = user, SearchTerm = "fixture", StartIndex = 1, Limit = 1 });
        Assert.Equal(2, hints.TotalRecordCount);
        Assert.Equal(personal.Id, Assert.Single(hints.Items).Item.Id);
        var unfiltered = await manager.GetSearchResultsAsync(new SearchProviderQuery { UserId = Guid.NewGuid(), SearchTerm = "fixture" });
        Assert.Contains(unfiltered, result => result.ItemId == future.Id);
    }

    [Fact]
    public async Task LargeHintPageRetainsRequestedProviderBudgetBeforeReleaseFiltering()
    {
        var configuration = new ConfigurationAccessor(() => new PluginConfiguration());
        var user = Guid.NewGuid();
        using var preferences = new UserPreferenceStore(Paths(), configuration);
        await preferences.SaveAsync(user, new UserPreferences { HideUnreleased = true, SearchMode = "Local" }, default);
        var items = Enumerable.Range(0, 160)
            .Select(index => Movie("Fixture " + index, true, DateTime.UtcNow.AddYears(index < 20 ? 1 : -1))).ToArray();
        var library = DispatchProxy.Create<ILibraryManager, NativeSearchBehaviorTests.CallbackProxy>();
        ((NativeSearchBehaviorTests.CallbackProxy)(object)library).InvokeMethod = (method, args) =>
            method.Name == "GetItemList" && args![0] is InternalItemsQuery query
                ? items.Where(item => query.ItemIds.Contains(item.Id)).ToArray()
                : throw new NotSupportedException(method.Name);
        var manager = new NativeSearchManager(new NativeResults(items), preferences, configuration, library);
        var page = await manager.GetSearchHintsAsync(new SearchQuery
        {
            UserId = user,
            SearchTerm = "fixture",
            StartIndex = 100,
            Limit = 150
        });
        Assert.Equal(130, page.TotalRecordCount);
        Assert.Equal(items.Skip(120).Take(30).Select(item => item.Id), page.Items.Select(hint => hint.Item.Id));
    }

    [Fact]
    public void ReleaseBufferHonorsUtcBoundaryWithoutTreatingUnknownDatesAsUnavailable()
    {
        var now = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);
        Assert.True(AddonSearchPolicy.IsReleased(new DateTime(2026, 1, 1), 2026, now, 2));
        Assert.False(AddonSearchPolicy.IsReleased(new DateTime(2026, 1, 1, 0, 0, 1), 2026, now, 2));
        Assert.True(AddonSearchPolicy.IsReleased(null, null, now, 2));
        Assert.False(AddonSearchPolicy.IsReleased(null, 2027, now, 2));
    }

    private SiphonPaths Paths()
    {
        var paths = DispatchProxy.Create<IApplicationPaths, MetadataQuotaStoreTests.PathsProxy>();
        ((MetadataQuotaStoreTests.PathsProxy)(object)paths).Directory = _directory;
        return new SiphonPaths(paths);
    }

    private static Movie Movie(string name, bool managed, DateTime released)
    {
        var item = new Movie { Id = Guid.NewGuid(), Name = name, PremiereDate = released };
        if (managed) item.SetProviderId("Siphon", "movie:" + item.Id.ToString("N"));
        return item;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class NativeResults(BaseItem[] items) : ISearchManager
    {
        public Task<QueryResult<SearchHintInfo>> GetSearchHintsAsync(SearchQuery query, CancellationToken cancellationToken = default)
        {
            // The native manager caps provider candidates before loading and paging hints.
            var candidates = items.Take(query.Limit ?? 100).ToArray();
            return Task.FromResult(new QueryResult<SearchHintInfo>(query.StartIndex, candidates.Length,
                candidates.Skip(query.StartIndex ?? 0).Take(query.Limit ?? int.MaxValue)
                    .Select(item => new SearchHintInfo { Item = item }).ToArray()));
        }
        public Task<IReadOnlyList<SearchResult>> GetSearchResultsAsync(SearchProviderQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SearchResult>>(items.Select(item => new SearchResult(item.Id, 1)).ToArray());
        public void AddParts(IEnumerable<ISearchProvider> providers) => throw new NotSupportedException();
        public IReadOnlyList<ISearchProvider> GetProviders() => throw new NotSupportedException();
    }
}
