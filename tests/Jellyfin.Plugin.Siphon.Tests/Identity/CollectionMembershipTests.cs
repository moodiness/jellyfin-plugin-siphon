using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Siphon.Collections;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Playback;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Identity;

[Collection("Native publication")]
public sealed class CollectionMembershipTests
{
    private static readonly TypeInfo NativeController = AssemblyBuilder
        .DefineDynamicAssembly(new AssemblyName("Jellyfin.Api"), AssemblyBuilderAccess.Run)
        .DefineDynamicModule("MembershipActions").DefineType("MembershipActions.Controller", TypeAttributes.Public)
        .CreateTypeInfo()!;

    [Fact]
    public void CanonicalDuplicatesDoNotChangePersonalPlaylistDuplicatesOrRelativeOrder()
    {
        using var fixture = new Fixture();
        var primary = fixture.Movie("first");
        var alternate = fixture.Version(primary);
        var second = fixture.Movie("second");
        var personal = fixture.Add(new Movie { Id = Guid.NewGuid(), Name = "Personal" });

        var normalized = fixture.Retention.Normalize([personal.Id, alternate.Id, second.Id, personal.Id, primary.Id], []);
        Assert.Equal([personal.Id, primary.Id, second.Id, personal.Id], normalized);
        Assert.Equal([personal.Id, second.Id, personal.Id], fixture.Retention.Normalize(
            [personal.Id, alternate.Id, second.Id, personal.Id, primary.Id], [alternate.Id]));
    }

    [Fact]
    public void CorruptVersionPointerCannotRedirectMembershipToPersonalMedia()
    {
        using var fixture = new Fixture();
        var primary = fixture.Movie("managed");
        var personal = fixture.Add(new Movie { Id = Guid.NewGuid(), Name = "Private cut" });
        var version = fixture.Version(primary);
        version.SetProviderId(NativeVersionService.VersionProvider, personal.Id.ToString("N"));
        version.PrimaryVersionId = personal.Id;

        Assert.Same(primary, fixture.Retention.Canonicalize(version.Id));
    }

    [Fact]
    public void NativeMembershipsProtectWholeSeriesButAutomaticCatalogLinksDoNotProtectThemselves()
    {
        using var fixture = new Fixture();
        var automatic = fixture.Movie("automatic");
        var manual = fixture.Movie("manual");
        var generated = fixture.Generated(automatic.Id, manual.Id);
        fixture.SeedLedger(generated, [automatic.Id]);
        var episode = fixture.Episode("show");
        var show = fixture.Add(new Series { Id = Guid.NewGuid(), Name = "Show" });
        show.SetProviderId("Siphon", "show");
        var season = fixture.Add(new Season { Id = Guid.NewGuid(), Name = "Season", SeriesId = show.Id });
        season.SetProviderId("Siphon", "show:season:1");
        fixture.Add(new Playlist { Id = Guid.NewGuid(), LinkedChildren = [LinkedChild.Create(season)] });
        fixture.Add(new BoxSet { Id = Guid.NewGuid(), LinkedChildren = [LinkedChild.Create(fixture.Version(episode))] });

        Assert.Equal(new[] { "manual", "show" }, fixture.Retention.GetProtectedContentKeys().Order(StringComparer.Ordinal));
        // The generated collection was visited first. Nesting it deliberately must still traverse its automatic members.
        fixture.Add(new BoxSet { Id = Guid.NewGuid(), LinkedChildren = [LinkedChild.Create(generated)] });
        Assert.Contains("automatic", fixture.Retention.GetProtectedContentKeys());
    }

    [Fact]
    public async Task DuplicateUserAddOverridesAutomaticOwnershipDurablyAndRemovalReleasesThatOverride()
    {
        using var fixture = new Fixture();
        var movie = fixture.Movie("catalog-title");
        var generated = fixture.Generated(movie.Id);
        fixture.SeedLedger(generated, [movie.Id]);
        Assert.Empty(fixture.Retention.GetProtectedContentKeys());

        await fixture.Catalogs.RecordDeliberateAsync(generated, [movie.Id], CancellationToken.None);
        fixture.ReloadServices();
        Assert.Contains("catalog-title", fixture.Retention.GetProtectedContentKeys());

        await fixture.Catalogs.ForgetDeliberateAsync(generated, [movie.Id], CancellationToken.None);
        fixture.ReloadServices();
        Assert.Empty(fixture.Retention.GetProtectedContentKeys());
    }

    [Fact]
    public async Task DisablingCollectionPresentationRemovesOnlyAutomaticLinks()
    {
        using var fixture = new Fixture();
        var stale = fixture.Movie("stale");
        var deliberate = fixture.Movie("deliberate");
        var personal = fixture.Add(new Movie { Id = Guid.NewGuid(), Name = "Personal" });
        var generated = fixture.Generated(stale.Id, deliberate.Id, personal.Id);
        fixture.SeedLedger(generated, [stale.Id, deliberate.Id], [deliberate.Id]);
        var other = fixture.Add(new BoxSet { Id = Guid.NewGuid(), LinkedChildren = [LinkedChild.Create(stale)] });

        await fixture.Catalogs.ReconcileAsync([], CancellationToken.None);

        Assert.Equal([deliberate.Id, personal.Id], generated.LinkedChildren.Select(link => link.ItemId!.Value));
        Assert.Equal(stale.Id, Assert.Single(other.LinkedChildren).ItemId);
        Assert.Empty(fixture.Deleted);
        fixture.ReloadServices();
        Assert.True(fixture.Catalogs.IsDeliberateMember(generated, deliberate.Id));
    }

    [Fact]
    public async Task InitialCatalogMembersRemainAutomaticAfterRestart()
    {
        using var fixture = new Fixture();
        fixture.Config.Addons = [new() { Id = "installation", Catalogs = [new() { Key = "catalog", Type = "movie", Id = "all", Presentation = "Collection" }] }];
        var automatic = fixture.Movie("automatic");
        var personal = fixture.Add(new Movie { Id = Guid.NewGuid(), Name = "Personal" });
        await fixture.Catalogs.ReconcileAsync(fixture.State.Items, CancellationToken.None);
        var generated = Assert.Single(fixture.Items.Values.OfType<BoxSet>());
        generated.LinkedChildren = [.. generated.LinkedChildren, LinkedChild.Create(personal)];
        await fixture.Catalogs.RecordDeliberateAsync(generated, [personal.Id], CancellationToken.None);

        fixture.ReloadServices();
        fixture.Config.Addons = [];
        await fixture.Catalogs.ReconcileAsync(fixture.State.Items, CancellationToken.None);

        var remaining = Assert.IsType<BoxSet>(fixture.Items[generated.Id]);
        Assert.Equal(personal.Id, Assert.Single(remaining.LinkedChildren).ItemId);
        Assert.Contains(automatic.Id, fixture.Items.Keys);
    }

    [Fact]
    public async Task MissingCatalogLinksDisappearWithoutDeletingRetainedTitlesOrDeliberateMembership()
    {
        using var fixture = new Fixture();
        fixture.Config.Addons = [new() { Id = "installation", Catalogs = [new() { Key = "catalog", Type = "movie", Id = "all", Presentation = "Collection" }] }];
        var automatic = fixture.Movie("automatic");
        var deliberate = fixture.Movie("deliberate");
        var generated = fixture.Generated(automatic.Id, deliberate.Id);
        fixture.SeedLedger(generated, [automatic.Id, deliberate.Id], [deliberate.Id]);
        var retained = fixture.State.Items.Select(item => item with { MissingOwners = ["installation:catalog"], MissingSinceUtc = DateTimeOffset.UtcNow }).ToArray();

        await fixture.Catalogs.ReconcileAsync(retained, CancellationToken.None);

        Assert.Equal(deliberate.Id, Assert.Single(generated.LinkedChildren).ItemId);
        Assert.Contains(automatic.Id, fixture.Items.Keys);
        Assert.Contains(deliberate.Id, fixture.Items.Keys);
        fixture.ReloadServices();
        Assert.True(fixture.Catalogs.IsDeliberateMember(generated, deliberate.Id));
    }

    [Fact]
    public async Task PersonalCollectionAdditionSurvivesConcurrentAutomaticCollectionRemoval()
    {
        using var fixture = new Fixture();
        var automatic = fixture.Movie("automatic");
        var personal = fixture.Add(new Movie { Id = Guid.NewGuid(), Name = "Personal" });
        var generated = fixture.Generated(automatic.Id);
        fixture.SeedLedger(generated, [automatic.Id]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconciliationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adding = fixture.AddPersonalToCollectionAsync(generated, personal, entered, release.Task);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var reconciling = fixture.ReconcileWithGateAsync(reconciliationEntered);
        try
        {
            Assert.False(reconciliationEntered.Task.IsCompleted);
            release.TrySetResult();
            await Task.WhenAll(adding, reconciling).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(generated.Id, fixture.Items.Keys);
            Assert.Equal(personal.Id, Assert.Single(generated.LinkedChildren).ItemId);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(adding, reconciling).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task NativePlaylistAddKeepsInsertionPositionAndCanonicalizesSelectedVersionIdempotently()
    {
        using var fixture = new Fixture();
        var first = fixture.Add(new Movie { Id = Guid.NewGuid(), Name = "First" });
        var last = fixture.Add(new Movie { Id = Guid.NewGuid(), Name = "Last" });
        var primary = fixture.Movie("selected");
        var alternate = fixture.Version(primary);
        var playlist = fixture.Add(new Playlist
        {
            Id = Guid.NewGuid(),
            OwnerUserId = fixture.User.Id,
            LinkedChildren = [LinkedChild.Create(first), LinkedChild.Create(last)]
        });
        await fixture.AddToPlaylistAsync(playlist, alternate.Id, 1);
        await fixture.AddToPlaylistAsync(playlist, primary.Id, 0);

        Assert.Equal([first.Id, primary.Id, last.Id], playlist.LinkedChildren.Select(link => link.ItemId!.Value));
        Assert.Equal(0, fixture.State.Saves);
    }

    [Fact]
    public async Task InaccessibleManagedItemIsRejectedBeforeNativeMutationOrPromotion()
    {
        using var fixture = new Fixture();
        var primary = fixture.Movie("restricted");
        fixture.Hidden.Add(primary.Id);
        var playlist = fixture.Add(new Playlist { Id = Guid.NewGuid(), OwnerUserId = fixture.User.Id, LinkedChildren = [] });

        var result = await fixture.AddToPlaylistAsync(playlist, fixture.Version(primary).Id, 0);

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(playlist.LinkedChildren);
        Assert.Equal(0, fixture.State.Saves);
    }

    [Fact]
    public async Task NativePermissionRejectionDoesNotRewriteExistingAlternateMemberships()
    {
        using var fixture = new Fixture();
        var primary = fixture.Movie("selected");
        var alternate = fixture.Version(primary);
        var playlist = fixture.Add(new Playlist
        {
            Id = Guid.NewGuid(),
            OwnerUserId = fixture.User.Id,
            LinkedChildren = [LinkedChild.Create(alternate)]
        });

        await fixture.AddToPlaylistAsync(playlist, primary.Id, 0, nativeDenial: true);

        Assert.Equal(alternate.Id, Assert.Single(playlist.LinkedChildren).ItemId);
        Assert.Equal(0, fixture.State.Saves);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "siphon-membership-" + Guid.NewGuid().ToString("N"));
        private readonly ILibraryManager _previousLibrary = BaseItem.LibraryManager;
        private readonly ILibraryManager _library;
        private readonly IItemPersistenceService _persistence;
        private readonly ICollectionManager _collections;
        private readonly SiphonPaths _paths;
        private readonly ConfigurationAccessor _configuration;
        public PluginConfiguration Config { get; } = new();
        public Dictionary<Guid, BaseItem> Items { get; } = [];
        public List<Guid> Deleted { get; } = [];
        public HashSet<Guid> Hidden { get; } = [];
        public MemoryState State { get; } = new();
        public User User { get; } = new("viewer", "authentication", "password-reset") { Id = Guid.NewGuid() };
        public CatalogCollectionService Catalogs { get; private set; } = null!;
        public CollectionRetentionService Retention { get; private set; } = null!;

        public Fixture()
        {
            _configuration = new ConfigurationAccessor(() => Config);
            _paths = new SiphonPaths(Proxy<IApplicationPaths>((method, _) => method.Name == "get_DataPath" ? _directory : throw new NotSupportedException(method.Name)));
            _library = Proxy<ILibraryManager>((method, args) => method.Name switch
            {
                "GetItemById" => Items.GetValueOrDefault((Guid)args![0]!),
                "GetNewItemId" => Guid.Empty,
                "GetItemList" => Query((InternalItemsQuery)args![0]!),
                "GetVirtualFolders" => new List<VirtualFolderInfo>(),
                "ConfigureUserAccess" or "RegisterItem" => null,
                "DeleteItem" => Delete((BaseItem)args![0]!),
                _ => throw new NotSupportedException(method.Name)
            });
            BaseItem.LibraryManager = _library;
            _persistence = Proxy<IItemPersistenceService>((method, _) => method.Name == "SaveItems" ? null : throw new NotSupportedException(method.Name));
            _collections = Proxy<ICollectionManager>((method, args) =>
            {
                if (method.Name == "GetCollectionsFolder")
                    return Task.FromResult<Folder?>(new Folder { Path = Path.Combine(_directory, "collections") });
                if (method.Name == "CreateCollectionAsync")
                {
                    var options = (CollectionCreationOptions)args![0]!;
                    var path = Path.Combine(_directory, "collections", options.Name + " [boxset]");
                    Directory.CreateDirectory(path);
                    return Task.FromResult(Add(new BoxSet
                    {
                        Id = Guid.NewGuid(),
                        Name = options.Name,
                        Path = path,
                        ProviderIds = options.ProviderIds,
                        LinkedChildren = options.ItemIdList.Select(id => LinkedChild.Create(Items[Guid.Parse(id)])).ToArray()
                    }));
                }
                if (method.Name is not ("AddToCollectionAsync" or "RemoveFromCollectionAsync"))
                    throw new NotSupportedException(method.Name);
                var box = (BoxSet)Items[(Guid)args![0]!];
                var ids = ((IEnumerable<Guid>)args[1]!).ToHashSet();
                box.LinkedChildren = method.Name == "RemoveFromCollectionAsync"
                    ? box.LinkedChildren.Where(link => !ids.Contains(link.ItemId!.Value)).ToArray()
                    : [.. box.LinkedChildren, .. ids.Where(id => !box.LinkedChildren.Any(link => link.ItemId == id)).Select(id => LinkedChild.Create(Items[id]))];
                return Task.CompletedTask;
            });
            ReloadServices();
        }

        public void ReloadServices()
        {
            Catalogs = new(_configuration, _paths, _library, _collections, _persistence);
            Retention = new(_library, State, Catalogs);
        }

        public T Add<T>(T item) where T : BaseItem { Items.Add(item.Id, item); return item; }
        public Movie Movie(string key)
        {
            State.Items.Add(Managed(key, key, "movie"));
            var item = Add(new Movie { Id = Guid.NewGuid(), Name = key });
            item.SetProviderId("Siphon", key);
            return item;
        }
        public Episode Episode(string key)
        {
            var itemKey = key + ":1:1";
            State.Items.Add(Managed(itemKey, key, "series"));
            var item = Add(new Episode { Id = Guid.NewGuid(), Name = itemKey });
            item.SetProviderId("Siphon", itemKey);
            return item;
        }
        public Video Version(Video primary)
        {
            Video item = primary is Movie ? new Movie() : new Episode();
            item.Id = Guid.NewGuid();
            item.Name = "Version";
            item.PrimaryVersionId = primary.Id;
            item.SetProviderId("Siphon", primary.GetProviderId("Siphon")!);
            item.SetProviderId(NativeVersionService.VersionProvider, primary.Id.ToString("N"));
            return Add(item);
        }
        public BoxSet Generated(params Guid[] ids)
        {
            var box = Add(new BoxSet { Id = Guid.NewGuid(), Name = "Catalog", LinkedChildren = ids.Select(id => LinkedChild.Create(Items[id])).ToArray() });
            box.SetProviderId(CatalogCollectionService.OwnerProvider, "installation:catalog");
            return box;
        }
        public void SeedLedger(BoxSet box, Guid[] automatic, Guid[]? deliberate = null)
        {
            Directory.CreateDirectory(_paths.DataDirectory);
            File.WriteAllBytes(Path.Combine(_paths.DataDirectory, "catalog-collections.json"), JsonSerializer.SerializeToUtf8Bytes(
                new Dictionary<Guid, CatalogCollectionService.Membership>
                {
                    [box.Id] = new() { Automatic = automatic.ToHashSet(), Deliberate = (deliberate ?? []).ToHashSet() }
                }));
        }
        private BaseItem[] Query(InternalItemsQuery query)
        {
            IEnumerable<BaseItem> result = Items.Values;
            if (query.ItemIds.Length > 0) result = result.Where(item => query.ItemIds.Contains(item.Id));
            if (query.User is not null) result = result.Where(item => !Hidden.Contains(item.Id));
            if (query.IncludeItemTypes.Length > 0) result = result.Where(item => query.IncludeItemTypes.Contains(item switch
            {
                BoxSet => BaseItemKind.BoxSet,
                Playlist => BaseItemKind.Playlist,
                Movie _ => BaseItemKind.Movie,
                Episode _ => BaseItemKind.Episode,
                Series => BaseItemKind.Series,
                Season => BaseItemKind.Season,
                _ => BaseItemKind.Folder
            }));
            if (query.HasAnyProviderId is { Count: > 0 } providers) result = result.Where(item => providers.Any(pair => item.GetProviderId(pair.Key) == pair.Value));
            return result.Skip(query.StartIndex ?? 0).Take(query.Limit ?? int.MaxValue).ToArray();
        }
        private object? Delete(BaseItem item) { Deleted.Add(item.Id); Items.Remove(item.Id); return null; }

        public async Task<IActionResult?> AddToPlaylistAsync(Playlist playlist, Guid itemId, int position, bool nativeDenial = false)
        {
            var authorization = Proxy<IAuthorizationContext>((method, _) => method.Name == "GetAuthorizationInfo"
                ? Task.FromResult(new AuthorizationInfo { User = User }) : throw new NotSupportedException(method.Name));
            var playlists = Proxy<IPlaylistManager>((method, _) => method.Name switch
            {
                "GetPlaylistForUser" => playlist,
                "SavePlaylistFile" => null,
                _ => throw new NotSupportedException(method.Name)
            });
            var filter = new NativeMembershipFilter(_configuration, authorization, null!, _library, playlists, _persistence, State, Retention, Catalogs, null!);
            var http = new DefaultHttpContext();
            http.Request.Method = HttpMethods.Post;
            var descriptor = new ControllerActionDescriptor { ControllerName = "Playlists", ActionName = "AddItemToPlaylist", ControllerTypeInfo = NativeController };
            var context = new ActionExecutingContext(new ActionContext(http, new RouteData(), descriptor), [],
                new Dictionary<string, object?> { ["playlistId"] = playlist.Id, ["ids"] = new[] { itemId }, ["position"] = position }, new object());
            await filter.OnActionExecutionAsync(context, () =>
            {
                if (nativeDenial) return Task.FromResult(new ActionExecutedContext(context, [], context.Controller) { Result = new ForbidResult() });
                var additions = ((Guid[])context.ActionArguments["ids"]!).Select(id => LinkedChild.Create(Items[id])).ToArray();
                playlist.LinkedChildren = [.. playlist.LinkedChildren[..position], .. additions, .. playlist.LinkedChildren[position..]];
                return Task.FromResult(new ActionExecutedContext(context, [], context.Controller) { Result = new NoContentResult() });
            });
            return context.Result;
        }
        public async Task AddPersonalToCollectionAsync(BoxSet box, Movie personal, TaskCompletionSource entered, Task release)
        {
            var authorization = Proxy<IAuthorizationContext>((method, _) => method.Name == "GetAuthorizationInfo"
                ? Task.FromResult(new AuthorizationInfo { User = User }) : throw new NotSupportedException(method.Name));
            var filter = new NativeMembershipFilter(_configuration, authorization, null!, _library, null!, _persistence, State, Retention, Catalogs, null!);
            var http = new DefaultHttpContext();
            http.Request.Method = HttpMethods.Post;
            var descriptor = new ControllerActionDescriptor { ControllerName = "Collection", ActionName = "AddToCollection", ControllerTypeInfo = NativeController };
            var context = new ActionExecutingContext(new ActionContext(http, new RouteData(), descriptor), [],
                new Dictionary<string, object?> { ["collectionId"] = box.Id, ["ids"] = new[] { personal.Id } }, new object());
            await filter.OnActionExecutionAsync(context, async () =>
            {
                entered.TrySetResult();
                await release;
                box.LinkedChildren = [.. box.LinkedChildren, LinkedChild.Create(personal)];
                return new ActionExecutedContext(context, [], context.Controller) { Result = new NoContentResult() };
            });
        }

        public async Task ReconcileWithGateAsync(TaskCompletionSource entered)
        {
            await _configuration.SynchronizationGate.WaitAsync();
            try { entered.TrySetResult(); await Catalogs.ReconcileAsync([], CancellationToken.None); }
            finally { _configuration.SynchronizationGate.Release(); }
        }
        public void Dispose()
        {
            BaseItem.LibraryManager = _previousLibrary;
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
        private static ManagedItem Managed(string key, string content, string type) => new()
        {
            Key = key,
            ContentKey = content,
            ContentId = content,
            Type = type,
            Name = key,
            VideoId = key,
            Path = key,
            Owners = ["installation:catalog"]
        };
    }

    public sealed class MemoryState : ISiphonStateStore
    {
        public List<ManagedItem> Items { get; } = [];
        public int Saves { get; private set; }
        public IReadOnlyList<ManagedItem> GetItems() => Items;
        public ManagedItem? FindByKey(string key) => Items.FirstOrDefault(item => item.Key == key);
        public ManagedItem? FindByContentKey(string key) => Items.FirstOrDefault(item => item.ContentKey == key);
        public ManagedItem? FindByPath(string path) => Items.FirstOrDefault(item => item.Path == path);
        public Task SaveAsync(IReadOnlyList<ManagedItem> items, CancellationToken ct) { Saves++; Items.Clear(); Items.AddRange(items); return Task.CompletedTask; }
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
