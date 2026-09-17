using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Siphon.Collections;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Playback;
using Jellyfin.Plugin.Siphon.Recovery;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Identity;

[CollectionDefinition("Native publication", DisableParallelization = true)]
public sealed class NativePublicationCollection { }

[Collection("Native publication")]
public sealed class LibraryMaterializerTests
{
    [Fact]
    public async Task ScopedMovieRemovalRetiresVersionsAndUnlinksFinalMembershipWithoutRemovingPersonalMedia()
    {
        using var fixture = new NativeLibrary("movie");
        var removed = MovieItem("removed", "installation:selected");
        var unrelated = MovieItem("unrelated", "installation:other");
        await fixture.PublishAsync([removed, unrelated]);
        var native = fixture.Media(removed.Key);
        var version = new Movie { Id = Guid.NewGuid(), Name = "Alternate cut", ParentId = native.ParentId, PrimaryVersionId = native.Id };
        version.SetProviderId("Siphon", removed.Key);
        version.SetProviderId(NativeVersionService.VersionProvider, native.Id.ToString("N"));
        fixture.Items.Add(version.Id, version);
        var selectedView = fixture.View("installation:selected");
        var oldLocation = fixture.Items[native.ParentId].Path;
        var personal = fixture.AddPersonalMovie(selectedView);
        var unrelatedNative = fixture.Media(unrelated.Key);
        var unrelatedView = fixture.View("installation:other");
        var unrelatedLocations = fixture.Locations(unrelatedView);
        var history = new UserData { ItemId = unrelatedNative.Id, CustomDataKey = unrelatedNative.Id.ToString("N"), UserId = Guid.NewGuid(), Item = null!, User = null!, IsFavorite = true, PlaybackPositionTicks = 321 };
        unrelatedNative.UserData.Add(history);

        await fixture.PublishAsync([unrelated], new(new HashSet<string> { removed.ContentKey }, [removed, unrelated]));

        Assert.DoesNotContain(native.Id, fixture.Items.Keys);
        Assert.DoesNotContain(version.Id, fixture.Items.Keys);
        Assert.DoesNotContain(oldLocation, fixture.Locations(selectedView));
        Assert.Contains(fixture.Items[personal.ParentId].Path, fixture.Locations(selectedView));
        Assert.Same(personal, fixture.Items[personal.Id]);
        Assert.Same(unrelatedNative, fixture.Media(unrelated.Key));
        Assert.Same(history, Assert.Single(unrelatedNative.UserData));
        Assert.Equal(unrelatedLocations, fixture.Locations(unrelatedView));
        Assert.All(fixture.Deletions, entry => Assert.False(entry.Options.DeleteFileLocation));
    }

    [Fact]
    public async Task ScopedEpisodeRemovalKeepsSharedSeriesThenRetiresItsEmptySeasonAndFinalSeries()
    {
        using var fixture = new NativeLibrary("series");
        var shared = FollowedSeriesSelectorTests.Episode("shared") with { Owners = ["installation:selected", "installation:other"] };
        var removed = shared with { Key = shared.ContentKey + ":2:1", Season = 2, VideoId = "second-season", Owners = ["installation:selected"] };
        var unrelated = FollowedSeriesSelectorTests.Episode("unrelated") with { Owners = ["installation:untouched"] };
        await fixture.PublishAsync([shared, removed, unrelated]);
        var show = fixture.Media(shared.ContentKey);
        var season = fixture.Media(shared.ContentKey + ":season:1");
        var emptySeason = fixture.Media(shared.ContentKey + ":season:2");
        var sharedEpisode = fixture.Media(shared.Key);
        var removedEpisode = fixture.Media(removed.Key);
        var unrelatedEpisode = fixture.Media(unrelated.Key);
        var history = new UserData { ItemId = sharedEpisode.Id, CustomDataKey = sharedEpisode.Id.ToString("N"), UserId = Guid.NewGuid(), Item = null!, User = null!, Played = true, PlayCount = 3 };
        sharedEpisode.UserData.Add(history);
        show.UserData.Add(new UserData { ItemId = show.Id, CustomDataKey = show.Id.ToString("N"), UserId = history.UserId, Item = null!, User = null!, IsFavorite = true });
        var untouchedLocations = fixture.Locations(fixture.View("installation:untouched"));

        await fixture.PublishAsync([shared, unrelated], new(new HashSet<string> { shared.ContentKey }, [shared, removed, unrelated]));

        Assert.DoesNotContain(removedEpisode.Id, fixture.Items.Keys);
        Assert.DoesNotContain(emptySeason.Id, fixture.Items.Keys);
        Assert.Same(show, fixture.Media(shared.ContentKey));
        Assert.Same(season, fixture.Media(shared.ContentKey + ":season:1"));
        Assert.Same(sharedEpisode, fixture.Media(shared.Key));
        Assert.Same(history, Assert.Single(sharedEpisode.UserData));
        Assert.True(Assert.Single(show.UserData).IsFavorite);
        Assert.Contains(show.ParentId, fixture.View("installation:selected").PhysicalFolderIds);
        Assert.Contains(show.ParentId, fixture.View("installation:other").PhysicalFolderIds);

        await fixture.PublishAsync([unrelated], new(new HashSet<string> { shared.ContentKey }, [shared, unrelated]));

        Assert.DoesNotContain(show.Id, fixture.Items.Keys);
        Assert.DoesNotContain(season.Id, fixture.Items.Keys);
        Assert.DoesNotContain(sharedEpisode.Id, fixture.Items.Keys);
        Assert.DoesNotContain(show.ParentId, fixture.View("installation:selected").PhysicalFolderIds);
        Assert.DoesNotContain(show.ParentId, fixture.View("installation:other").PhysicalFolderIds);
        Assert.Same(unrelatedEpisode, fixture.Media(unrelated.Key));
        Assert.Equal(untouchedLocations, fixture.Locations(fixture.View("installation:untouched")));
    }

    [Fact]
    public async Task ScopedSeriesPreviewExpiryRetiresShellAndUnlinksItsLastPreviewLocation()
    {
        using var fixture = new NativeLibrary("series");
        var preview = Preview("expired");
        var unrelated = FollowedSeriesSelectorTests.Episode("unrelated") with { Owners = ["installation:other"] };
        await fixture.PublishAsync([preview, unrelated]);
        var shell = fixture.Media(preview.ContentKey);
        var view = fixture.View("siphon:preview:series");
        var oldRoot = shell.ParentId;
        var unrelatedEpisode = fixture.Media(unrelated.Key);

        await fixture.PublishAsync([unrelated], new(new HashSet<string> { preview.ContentKey }, [preview, unrelated]));

        Assert.DoesNotContain(shell.Id, fixture.Items.Keys);
        Assert.DoesNotContain(oldRoot, view.PhysicalFolderIds);
        Assert.Same(unrelatedEpisode, fixture.Media(unrelated.Key));
    }

    [Fact]
    public async Task ScopedPreviewAdoptionPreservesCanonicalSeriesAndRemovesItsFormerPreviewMembership()
    {
        using var fixture = new NativeLibrary("series");
        var preview = Preview("adopted");
        await fixture.PublishAsync([preview]);
        var shell = fixture.Media(preview.ContentKey);
        var oldRoot = shell.ParentId;
        var history = new UserData { ItemId = shell.Id, CustomDataKey = shell.Id.ToString("N"), UserId = Guid.NewGuid(), Item = null!, User = null!, IsFavorite = true };
        shell.UserData.Add(history);
        var episode = FollowedSeriesSelectorTests.Episode("adopted") with { Owners = ["installation:selected"] };

        await fixture.PublishAsync([episode], new(new HashSet<string> { preview.ContentKey }, [preview]));

        Assert.Same(shell, fixture.Media(episode.ContentKey));
        Assert.Same(history, Assert.Single(shell.UserData));
        Assert.IsType<Episode>(fixture.Media(episode.Key));
        Assert.DoesNotContain(oldRoot, fixture.View("siphon:preview:series").PhysicalFolderIds);
        Assert.Contains(shell.ParentId, fixture.View("installation:selected").PhysicalFolderIds);
    }

    private static ManagedItem MovieItem(string id, string owner) => new()
    {
        Key = "movie:" + id,
        ContentKey = "movie:" + id,
        Type = "movie",
        ContentId = id,
        VideoId = id,
        Name = id,
        Path = "/siphon/" + id,
        Owners = [owner]
    };

    private static ManagedItem Preview(string id) => new()
    {
        Key = "series:" + id,
        ContentKey = "series:" + id,
        Type = "series",
        ContentId = id,
        VideoId = id,
        Name = id,
        SeriesName = id,
        Path = "/siphon/" + id,
        Owners = ["siphon:preview:series"],
        IsSearchPreview = true,
        PreviewExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
    };

    private sealed class NativeLibrary : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "siphon-publication-" + Guid.NewGuid().ToString("N"));
        private readonly AggregateFolder _root = new() { Id = Guid.NewGuid() };
        private readonly UserRootFolder _userRoot = new() { Id = Guid.NewGuid() };
        private readonly ILibraryManager _originalLibrary = BaseItem.LibraryManager;
        private readonly IProviderManager _originalProviders = BaseItem.ProviderManager;
        private readonly IFileSystem _originalFileSystem = BaseItem.FileSystem;
        private readonly IServerApplicationHost _originalHost = CollectionFolder.ApplicationHost;
        private readonly IXmlSerializer _originalSerializer = CollectionFolder.XmlSerializer;
        private readonly LibraryMaterializer _materializer;
        private readonly FollowedSeriesSelectorTests.Database _database = new();
        private readonly SyncDiagnostics _diagnostics;
        private readonly List<CollectionFolder> _views = [];
        public Dictionary<Guid, BaseItem> Items { get; } = [];
        public List<(BaseItem Item, DeleteOptions Options)> Deletions { get; } = [];

        public NativeLibrary(string type)
        {
            var config = new PluginConfiguration
            {
                PublicBaseUrl = "https://jellyfin.example",
                Addons = [new ConfiguredAddon
                {
                    Id = "installation",
                    Catalogs = [new() { Key = "selected", Type = type, Id = "Selected" },
                        new() { Key = "other", Type = type, Id = "Other" }, new() { Key = "untouched", Type = type, Id = "Untouched" }]
                }]
            };
            var configuration = new ConfigurationAccessor(() => config);
            var paths = new SiphonPaths(Proxy<IApplicationPaths>((method, _) => method.Name == "get_DataPath"
                ? _directory : throw new NotSupportedException(method.Name)));
            var library = Proxy<ILibraryManager>(InvokeLibrary);
            var persistence = Proxy<IItemPersistenceService>((method, args) =>
            {
                if (method.Name == "SaveItems") Save((IEnumerable<BaseItem>)args![0]!);
                else if (method.Name == "DeleteItem") foreach (var id in (IEnumerable<Guid>)args![0]!) Items.Remove(id);
                else throw new NotSupportedException(method.Name);
                return null;
            });
            var fileSystem = Proxy<IFileSystem>((method, args) => method.Name == "AreEqual"
                ? string.Equals((string?)args![0], (string?)args[1], StringComparison.Ordinal)
                : throw new NotSupportedException(method.Name));
            BaseItem.LibraryManager = library;
            BaseItem.FileSystem = fileSystem;
            BaseItem.ProviderManager = Proxy<IProviderManager>((method, args) =>
            {
                if (method.Name != "RefreshSingleItem") throw new NotSupportedException(method.Name);
                var view = (CollectionFolder)args![0]!;
                view.PhysicalLocationsList = Locations(view);
                view.PhysicalFolderIds = view.PhysicalLocationsList.Select(path => Items.Values.Single(item => item.Path == path).Id).ToArray();
                return Task.FromResult(ItemUpdateType.None);
            });
            CollectionFolder.ApplicationHost = Proxy<IServerApplicationHost>((method, args) => method.Name is "ReverseVirtualPath" or "ExpandVirtualPath"
                ? args![0] : throw new NotSupportedException(method.Name));
            CollectionFolder.XmlSerializer = Proxy<IXmlSerializer>((method, _) => method.Name switch
            {
                "SerializeToFile" => null,
                "DeserializeFromFile" => throw new FileNotFoundException(),
                _ => throw new NotSupportedException(method.Name)
            });
            Items.Add(_root.Id, _root);
            Items.Add(_userRoot.Id, _userRoot);
            var tokens = new CapabilityTokenService(new SiphonSecretStore(paths));
            _diagnostics = new SyncDiagnostics(paths, NullLogger<SyncDiagnostics>.Instance);
            var metadata = new ItemMetadataMapper(configuration, tokens, library, null!, persistence);
            var catalogs = new CatalogLibraryService(configuration, paths, null!, library, persistence,
                Proxy<IUserManager>((method, _) => method.Name == "GetUsers" ? Array.Empty<User>() : throw new NotSupportedException(method.Name)), fileSystem);
            _materializer = new LibraryMaterializer(configuration, tokens, null!, library, persistence, null!, metadata, catalogs, null!,
                NullLogger<LibraryMaterializer>.Instance, paths, _diagnostics,
                new CatalogCollectionService(configuration, paths, library, null!, persistence), new RecoveryLedger(paths, _database));
        }

        public BaseItem Media(string key) => Items.Values.Single(item => item.GetProviderId("Siphon") == key);
        public CollectionFolder View(string owner) => _views.Single(view => view.GetProviderId(CatalogLibraryService.CatalogProvider) == owner);
        public string[] Locations(CollectionFolder view) => view.GetLibraryOptions().PathInfos.Select(info => info.Path).Order(StringComparer.Ordinal).ToArray();

        public Task PublishAsync(IReadOnlyList<ManagedItem> retained, LibraryMaterializer.PublicationScope? scope = null)
        {
            var existing = Items.Values.Where(item => item.GetProviderId("Siphon") is not null).ToDictionary(item => item.Id);
            var topParents = existing.Values.ToDictionary(item => item.Id, item => (Guid?)TopParent(item));
            // Run the real native publication pipeline, replacing only Jellyfin's IO services.
            // The loader's DB query is outside this regression's ownership/removal contract.
            return (Task)typeof(LibraryMaterializer).GetMethod("MaterializeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_materializer, [retained, existing, topParents, Array.Empty<Guid>(), CancellationToken.None,
                    new Dictionary<string, string>(), null, new HashSet<string>(StringComparer.Ordinal), scope, null])!;
        }

        public Movie AddPersonalMovie(CollectionFolder view)
        {
            var folder = new Folder { Id = Guid.NewGuid(), Path = Path.Combine(_directory, "personal") };
            Items.Add(folder.Id, folder);
            var movie = new Movie { Id = Guid.NewGuid(), Name = "Personal movie", Path = Path.Combine(folder.Path, "movie.mkv"), ParentId = folder.Id };
            Items.Add(movie.Id, movie);
            AddLocation(view, folder.Path);
            return movie;
        }

        private Guid TopParent(BaseItem item)
        {
            while (Items.TryGetValue(item.ParentId, out var parent) && parent.Id != _root.Id) item = parent;
            return item.Id;
        }

        private object? InvokeLibrary(MethodInfo method, object?[]? args)
        {
            switch (method.Name)
            {
                case "get_RootFolder": return _root;
                case "GetUserRootFolder": return _userRoot;
                case "GetNewItemId": return new Guid(MD5.HashData(Encoding.UTF8.GetBytes(args![0] + ":" + args[1])));
                case "GetItemById": return Items.GetValueOrDefault((Guid)args![0]!);
                case "FindByPath": return Items.Values.FirstOrDefault(item => item.Path == (string)args![0]!);
                case "CreateItems":
                    Save((IEnumerable<BaseItem>)args![0]!);
                    // Native Jellyfin invalidates the supplied parent's cached rows as well as its children.
                    if (args[1] is Folder parent) { parent.Children = null; parent.UserData = null; }
                    return null;
                case "RegisterItem": Save([(BaseItem)args![0]!]); return null;
                case "GetVirtualFolders":
                    return _views.Select(view => new VirtualFolderInfo
                    {
                        ItemId = view.Id.ToString("N"),
                        Name = view.Name,
                        Locations = Locations(view),
                        LibraryOptions = view.GetLibraryOptions()
                    }).ToList();
                case "AddVirtualFolder":
                    var view = new CollectionFolder { Id = Guid.NewGuid(), Name = (string)args![0]!, Path = Path.Combine(_directory, Guid.NewGuid().ToString("N")) };
                    view.UpdateLibraryOptions((LibraryOptions)args[2]!);
                    _views.Add(view);
                    Items.Add(view.Id, view);
                    return Task.CompletedTask;
                case "CreateShortcut":
                    AddLocation(_views.Single(view => view.Path == (string)args![0]!), ((MediaPathInfo)args![1]!).Path);
                    return null;
                case "RemoveMediaPath":
                    var target = _views.Single(view => view.Name == (string)args![0]!);
                    var options = target.GetLibraryOptions();
                    options.PathInfos = options.PathInfos.Where(info => info.Path != (string)args![1]!).ToArray();
                    target.UpdateLibraryOptions(options);
                    return null;
                case "GetItemList":
                    var query = (InternalItemsQuery)args![0]!;
                    return Items.Values.Where(item => (query.ParentId == Guid.Empty || item.ParentId == query.ParentId)
                        && (query.IncludeItemTypes.Length == 0 || query.IncludeItemTypes.Any(kind => kind.ToString() == item.GetType().Name))
                        && (query.HasAnyProviderId is not { Count: > 0 } providers || providers.Any(pair => item.GetProviderId(pair.Key) == pair.Value)))
                        .Skip(query.StartIndex ?? 0).Take(query.Limit ?? int.MaxValue).ToArray();
                case "DeleteItem":
                    var item = (BaseItem)args![0]!;
                    Deletions.Add((item, (DeleteOptions)args[1]!));
                    if (Items.GetValueOrDefault(item.ParentId) is Folder removedFrom) { removedFrom.Children = null; removedFrom.UserData = null; }
                    Items.Remove(item.Id);
                    return null;
                default: throw new NotSupportedException(method.Name);
            }
        }

        private static void AddLocation(CollectionFolder view, string location)
        {
            var options = view.GetLibraryOptions();
            options.PathInfos = options.PathInfos.Append(new MediaPathInfo(location)).ToArray();
            view.UpdateLibraryOptions(options);
        }

        private void Save(IEnumerable<BaseItem> items)
        {
            foreach (var item in items) Items[item.Id] = item;
        }

        public void Dispose()
        {
            _diagnostics.Dispose();
            _database.Dispose();
            BaseItem.LibraryManager = _originalLibrary;
            BaseItem.ProviderManager = _originalProviders;
            BaseItem.FileSystem = _originalFileSystem;
            CollectionFolder.ApplicationHost = _originalHost;
            CollectionFolder.XmlSerializer = _originalSerializer;
            CollectionFolder.OnCollectionFolderChange();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
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
