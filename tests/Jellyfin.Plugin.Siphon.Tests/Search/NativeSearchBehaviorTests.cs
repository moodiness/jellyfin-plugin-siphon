using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Siphon.Collections;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Metadata;
using Jellyfin.Plugin.Siphon.Protocol;
using Jellyfin.Plugin.Siphon.Search;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Search;

public sealed class NativeSearchBehaviorTests
{
    // Only the descriptor's assembly identity is relevant to the filter. Avoid bringing
    // the server executable and its startup dependencies into these action-filter tests.
    private static readonly TypeInfo NativeController = AssemblyBuilder
        .DefineDynamicAssembly(new AssemblyName("Jellyfin.Api"), AssemblyBuilderAccess.Run)
        .DefineDynamicModule("SearchActions").DefineType("SearchActions.Controller", TypeAttributes.Public)
        .CreateTypeInfo()!;

    [Fact]
    public async Task SearchReturnsWhileSynchronizationStillOwnsTheGate()
    {
        using var fixture = new SearchFixture([]);
        await fixture.Configuration.SynchronizationGate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var pending = fixture.Search.SearchAsync(fixture.Query(), cancellation.Token);
            Assert.True(pending.IsCompleted, "Native search must not join the synchronization queue.");
            Assert.Empty(await pending);
            Assert.Equal(0, fixture.Origin.Requests);
        }
        finally
        {
            cancellation.Cancel();
            fixture.Configuration.SynchronizationGate.Release();
        }
    }

    [Fact]
    public async Task CanonicalSeriesDetailDoesNotWaitForSynchronization()
    {
        var episode = Item("installation", "series") with
        {
            Key = "episode",
            Season = 1,
            Episode = 1,
            IsSearchPreview = false,
            SearchMetadataAddonId = "",
            Owners = ["installation:catalog"]
        };
        using var fixture = new SearchFixture([episode]);
        var native = fixture.AddNative(episode);
        await fixture.Configuration.SynchronizationGate.WaitAsync();
        try
        {
            var request = fixture.Request("UserLibrary", "GetItem", native.Id);
            var pending = fixture.ExecuteAsync(request);
            Assert.True(pending.IsCompleted, "Already-expanded canonical details must remain available during synchronization.");
            Assert.True(await pending);
            Assert.Null(request.Result);
            Assert.Equal(0, fixture.Origin.Requests);
        }
        finally { fixture.Configuration.SynchronizationGate.Release(); }
    }

    [Fact]
    public async Task DurableDiscoveryStillRequiresItsFirstMetadataLoad()
    {
        var promoted = Item("installation", "movie") with
        {
            IsSearchPreview = false,
            PreviewExpiresUtc = null,
            Owners = [CatalogLibraryService.ManualPrefix + "movie"]
        };
        using var fixture = new SearchFixture([promoted]);
        var native = fixture.AddNative(promoted);
        await fixture.Configuration.SynchronizationGate.WaitAsync();
        try
        {
            var request = fixture.Request("UserLibrary", "GetItem", native.Id);
            Assert.False(await fixture.ExecuteAsync(request));
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(request.Result).StatusCode);
            Assert.Equal(0, fixture.State.Saves);
        }
        finally { fixture.Configuration.SynchronizationGate.Release(); }
    }

    [Theory]
    [InlineData("Image", "GetItemImage")]
    [InlineData("Items", "GetItems")]
    [InlineData("UserLibrary", "GetSpecialFeatures")]
    public async Task UnrelatedGetsNeverExpandASeriesShell(string controller, string action)
    {
        var shell = Item("installation", "series");
        using var fixture = new SearchFixture([shell]);
        var native = fixture.AddNative(shell);
        await fixture.Configuration.SynchronizationGate.WaitAsync();
        try
        {
            var request = fixture.Request(controller, action, native.Id);
            Assert.True(await fixture.ExecuteAsync(request));
            Assert.Null(request.Result);
            Assert.Same(shell, fixture.State.FindByContentKey(shell.ContentKey));
            Assert.Equal(0, fixture.Origin.Requests);
        }
        finally { fixture.Configuration.SynchronizationGate.Release(); }
    }

    [Fact]
    public async Task RejectedCrossUserDetailDoesNotExpandBeforeNativeAuthorization()
    {
        var shell = Item("installation", "series");
        using var fixture = new SearchFixture([shell]);
        var native = fixture.AddNative(shell);
        await fixture.Configuration.SynchronizationGate.WaitAsync();
        try
        {
            var request = fixture.Request("UserLibrary", "GetItemLegacy", native.Id, fixture.OtherUser.Id);
            Assert.True(await fixture.ExecuteAsync(request));
            Assert.Null(request.Result);
            Assert.Same(shell, fixture.State.FindByContentKey(shell.ContentKey));
            Assert.Equal(0, fixture.State.Saves);
            Assert.Equal(0, fixture.Origin.Requests);
        }
        finally { fixture.Configuration.SynchronizationGate.Release(); }
    }

    [Fact]
    public async Task AdministratorSelectionUsesRequestedUsersLibraryAccess()
    {
        var shell = Item("installation", "series");
        using var fixture = new SearchFixture([shell]);
        var native = fixture.AddNative(shell, fixture.OtherUser);
        await fixture.Configuration.SynchronizationGate.WaitAsync();
        try
        {
            var request = fixture.Request("TvShows", "GetEpisodes", native.Id, fixture.OtherUser.Id, administrator: true);
            Assert.False(await fixture.ExecuteAsync(request));
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(request.Result).StatusCode);
            // The authenticated user's own library cannot see this title. A busy response
            // means the authorized effective user's visible shell reached real expansion.
            Assert.False(fixture.Search.IsAccessible(native.Id, fixture.User));
            Assert.True(fixture.Search.IsAccessible(native.Id, fixture.OtherUser));
        }
        finally { fixture.Configuration.SynchronizationGate.Release(); }
    }

    [Fact]
    public async Task SameOpaqueIdFromTwoInstallationsReturnsBothExistingTitles()
    {
        var first = Item("first", "movie");
        var second = Item("second", "movie");
        using var fixture = new SearchFixture([first, second], "first", "second");
        var firstNative = fixture.AddNative(first);
        var secondNative = fixture.AddNative(second);

        var results = await fixture.Search.SearchAsync(fixture.Query(), CancellationToken.None);

        Assert.Equal(new[] { firstNative.Id, secondNative.Id }.Order(), results.Select(item => item.Id).Order());
        Assert.NotEqual(firstNative.Id, secondNative.Id);
        Assert.Equal(0, fixture.State.Saves);
    }

    [Fact]
    public async Task ExpansionOverCapacityLeavesTheExistingStateUntouched()
    {
        var shell = Item("installation", "series");
        var retained = Item("installation", "movie");
        var items = Enumerable.Range(1, SiphonStateStore.MaximumItems - 1)
            .Select(index => retained with { Key = "retained:" + index, ContentKey = "retained:" + index })
            .Prepend(shell).ToArray();
        using var fixture = new SearchFixture(items);
        var native = fixture.AddNative(shell);

        var error = await Assert.ThrowsAsync<SearchAdmissionException>(() => fixture.Search.ExpandAsync(native.Id, fixture.User, false, CancellationToken.None));

        Assert.Equal("LibraryLimit", error.Code);
        Assert.Same(items, fixture.State.GetItems());
        Assert.Same(shell, fixture.State.FindByContentKey(shell.ContentKey));
        Assert.Equal(0, fixture.State.Saves);
    }

    private static ManagedItem Item(string installation, string type)
    {
        var key = ContentIdentity.Key(type, "opaque-title", installation, new Dictionary<string, string>());
        return new ManagedItem
        {
            Key = key,
            ContentKey = key,
            ContentId = "opaque-title",
            VideoId = "opaque-title",
            Type = type,
            Name = installation + " title",
            Path = "",
            SearchAddonId = installation,
            SearchResourceType = type,
            IsSearchPreview = true,
            PreviewExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
            Owners = [CatalogLibraryService.PreviewPrefix + type]
        };
    }

    private sealed class SearchFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "siphon-search-fixture-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<Guid, (BaseItem Item, Guid UserId)> _native = [];
        private readonly StremioClient _client;
        private readonly NativeSearchSelectionFilter _filter;
        public User User { get; } = new("viewer", "authentication", "password-reset") { Id = Guid.NewGuid() };
        public User OtherUser { get; } = new("other-viewer", "authentication", "password-reset") { Id = Guid.NewGuid() };
        public ConfigurationAccessor Configuration { get; }
        public MemoryState State { get; }
        public MemoryOrigin Origin { get; } = new();
        public AddonSearchService Search { get; }

        public SearchFixture(IReadOnlyList<ManagedItem> items, params string[] installations)
        {
            var config = new PluginConfiguration
            {
                PublicBaseUrl = "https://jellyfin.example",
                Addons = (installations.Length == 0 ? new[] { "installation" } : installations)
                    .Select(id => new ConfiguredAddon { Id = id, Enabled = true, ManifestUrl = "https://" + id + ".example/manifest.json" }).ToArray()
            };
            Configuration = new ConfigurationAccessor(() => config);
            State = new MemoryState(items);
            _client = new StremioClient(Origin, Configuration);
            var registry = new AddonRegistry(_client, Configuration, NullLogger<AddonRegistry>.Instance);
            var library = Proxy<ILibraryManager>((method, args) => method.Name switch
            {
                "GetNewItemId" => NativeId((string)args![0]!, (Type)args[1]!),
                "GetItemById" => _native.GetValueOrDefault((Guid)args![0]!).Item,
                "ConfigureUserAccess" => null,
                "GetItemList" => Accessible((InternalItemsQuery)args![0]!),
                _ => throw new NotSupportedException(method.Name)
            });
            var users = Proxy<IUserManager>((method, args) => method.Name == "GetUserById"
                ? (Guid)args![0]! == User.Id ? User : (Guid)args[0]! == OtherUser.Id ? OtherUser : null
                : throw new NotSupportedException(method.Name));
            var enrichment = new MetadataEnrichmentService(Configuration, null!, library, null!, State);
            var sync = new CatalogSyncService(Configuration, registry, _client, State, null!, NullLogger<CatalogSyncService>.Instance,
                null!, enrichment, null!);
            var paths = new SiphonPaths(Proxy<IApplicationPaths>((method, _) => method.Name == "get_DataPath"
                ? _directory : throw new NotSupportedException(method.Name)));
            Search = new AddonSearchService(Configuration, registry, _client, State, null!, sync, library, users, null!,
                NullLogger<AddonSearchService>.Instance, enrichment, new UserPreferenceStore(paths, Configuration),
                new CollectionRetentionService(library, State, null!));
            var authorization = Proxy<IAuthorizationContext>((method, _) => method.Name == "GetAuthorizationInfo"
                ? Task.FromResult(new AuthorizationInfo { User = User }) : throw new NotSupportedException(method.Name));
            _filter = new NativeSearchSelectionFilter(Search, authorization, users);
        }

        public BaseItem AddNative(ManagedItem item, User? visibleTo = null)
        {
            BaseItem native = item.Type == "series" ? new Series() : new Movie();
            native.Id = NativeId(item.Type == "series" ? "siphon:series:" + item.ContentKey : "siphon:media:" + item.Key, native.GetType());
            native.Name = item.Name;
            native.SetProviderId("Siphon", item.ContentKey);
            _native.Add(native.Id, (native, (visibleTo ?? User).Id));
            return native;
        }

        private BaseItem[] Accessible(InternalItemsQuery query)
            => _native.Values.Where(entry => entry.UserId == query.User?.Id
                && (query.ItemIds.Length == 0 || query.ItemIds.Contains(entry.Item.Id)))
                .Select(entry => entry.Item).ToArray();

        public SearchProviderQuery Query() => new() { SearchTerm = "title", UserId = User.Id, IncludeItemTypes = [BaseItemKind.Movie] };

        public ActionExecutingContext Request(string controller, string action, Guid id, Guid? requestedUser = null, bool administrator = false)
        {
            var http = new DefaultHttpContext();
            http.Request.Method = HttpMethods.Get;
            if (administrator) http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Administrator")], "test"));
            var descriptor = new ControllerActionDescriptor { ControllerName = controller, ActionName = action, ControllerTypeInfo = NativeController };
            return new ActionExecutingContext(new ActionContext(http, new RouteData(), descriptor), [],
                new Dictionary<string, object?> { ["itemId"] = id, ["seriesId"] = id, ["userId"] = requestedUser }, new object());
        }

        public async Task<bool> ExecuteAsync(ActionExecutingContext context)
        {
            var continued = false;
            await _filter.OnActionExecutionAsync(context, () =>
            {
                continued = true;
                return Task.FromResult(new ActionExecutedContext(context, [], context.Controller));
            });
            return continued;
        }

        public void Dispose()
        {
            Search.Dispose();
            _client.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
        private static Guid NativeId(string value, Type type) => new(MD5.HashData(Encoding.UTF8.GetBytes(value + ":" + type)));
    }

    private sealed class MemoryState(IReadOnlyList<ManagedItem> items) : ISiphonStateStore
    {
        public int Saves { get; private set; }
        public IReadOnlyList<ManagedItem> GetItems() => items;
        public ManagedItem? FindByKey(string key) => items.FirstOrDefault(item => item.Key == key);
        public ManagedItem? FindByPath(string path) => items.FirstOrDefault(item => item.Path == path);
        public ManagedItem? FindByContentKey(string key) => items.FirstOrDefault(item => item.ContentKey == key);
        public Task SaveAsync(IReadOnlyList<ManagedItem> next, CancellationToken ct)
        {
            Saves++;
            throw new InvalidOperationException("These paths must not mutate persisted state.");
        }
    }

    private sealed class MemoryOrigin : ISafeHttpClient
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requests);
            var json = uri.AbsolutePath switch
            {
                "/manifest.json" => """{"id":"fixture","types":["movie","series"],"resources":["catalog","meta"],"catalogs":[{"type":"movie","id":"titles","extra":[{"name":"search"}]}]}""",
                var path when path.StartsWith("/catalog/", StringComparison.Ordinal) => """{"metas":[{"id":"opaque-title","type":"movie","name":"Found title"}]}""",
                "/meta/series/opaque-title.json" => """{"meta":{"id":"opaque-title","type":"series","name":"Found series","videos":[{"id":"one","season":1,"episode":1,"title":"One"},{"id":"two","season":1,"episode":2,"title":"Two"}]}}""",
                _ => throw new InvalidOperationException("Unexpected upstream path: " + uri.AbsolutePath)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class
    {
        var proxy = DispatchProxy.Create<T, CallbackProxy>();
        ((CallbackProxy)(object)proxy).InvokeMethod = invoke;
        return proxy;
    }

    public class CallbackProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> InvokeMethod { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => InvokeMethod(targetMethod!, args);
    }
}
