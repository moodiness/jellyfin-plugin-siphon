using System.Reflection;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Common.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Infrastructure;

public sealed class StateSnapshotTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "siphon-snapshot-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadViewsCannotMutateNestedStateAndRetainTheirCommittedRevision()
    {
        var paths = Paths();
        using var state = new SiphonStateStore(paths);
        var input = Item();
        await state.SaveAsync([input], CancellationToken.None);
        var original = state.GetReadSnapshot();
        var view = Assert.Single(original.Items);
        Assert.Throws<NotSupportedException>(() => ((IList<ManagedItemSnapshot>)original.Items).Clear());
        foreach (var values in new[] { view.Owners, view.MissingOwners, view.Genres, view.ProductionLocations, view.EpisodeGenres, view.EpisodeProductionLocations })
            Assert.Throws<NotSupportedException>(() => ((IList<string>)values)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((IList<ManagedPerson>)view.People)[0] = new("Changed", "Actor"));
        Assert.Throws<NotSupportedException>(() => ((IList<ManagedPerson>)view.EpisodePeople).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<StreamIdentity>)view.StreamIdentities).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)view.ProviderIds)["Imdb"] = "changed");
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)view.EpisodeProviderIds).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, MetadataFieldSnapshot>)view.MetadataProvenance).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<MetadataObservation>)view.MetadataProvenance["Name"].Sources).Clear());

        input.Owners[0] = "changed-input";
        input.ProviderIds["Imdb"] = "changed-input";
        input.MetadataProvenance["Name"].Sources[0] = new("Changed", null, null);
        var working = view.ToMutable();
        working.Owners[0] = "changed-output";
        working.ProviderIds["Imdb"] = "changed-output";
        working.MetadataProvenance["Name"].Sources[0] = new("Changed", null, null);
        var changed = working with { Name = "Updated", MissingOwners = [] };
        await state.SaveAsync([changed], CancellationToken.None);

        Assert.Equal("Film", view.Name);
        Assert.Equal("catalog", view.Owners[0]);
        Assert.Equal("tt1234567", view.ProviderIds["imdb"]);
        Assert.Equal("Addon", view.MetadataProvenance["Name"].Sources[0].Origin);
        Assert.Equal(original.Revision!.Value + 1, state.GetReadSnapshot().Revision);
        Assert.Equal("Updated", state.ReadByKey(input.Key)?.Name);
        using var restarted = new SiphonStateStore(paths);
        Assert.Equal("Updated", restarted.ReadByPath(input.Path)?.Name);
        Assert.Equal(input.Key, restarted.ReadByContentKey(input.ContentKey)?.Key);
    }

    [Fact]
    public void AlternateStoreDefaultsOwnTheirCollectionsWithoutRequiringSnapshotSupport()
    {
        var input = Item();
        ISiphonStateStore alternate = new MutableState(input);
        var snapshot = alternate.GetReadSnapshot();
        var single = alternate.ReadByKey(input.Key)!;
        input.Genres[0] = "changed";
        input.EpisodeProviderIds.Clear();
        input.MetadataProvenance["Name"].Sources[0] = new("Changed", null, null);
        Assert.Null(snapshot.Revision);
        foreach (var item in new[] { Assert.Single(snapshot.Items), single })
        {
            Assert.Equal("Drama", item.Genres[0]);
            Assert.Equal("episode", item.EpisodeProviderIds["Tvdb"]);
            Assert.Equal("Addon", item.MetadataProvenance["Name"].Sources[0].Origin);
        }
    }

    [Fact]
    public async Task ExistingCatalogAbove256MiBCanLoadCommitAndRestartWithoutLosingMetadata()
    {
        var paths = Paths();
        Directory.CreateDirectory(paths.DataDirectory);
        var path = Path.Combine(paths.DataDirectory, "state.json");
        // Repeated escaped metadata crosses the former aggregate limit without a huge JSON token.
        var template = Item() with { Description = new string('é', 64 * 1024) };
        var items = Enumerable.Range(0, 700).Select(index => template with
        {
            Key = "movie:large-" + index,
            ContentId = "large-" + index,
            ContentKey = "movie:large-" + index,
            VideoId = "large-" + index,
            Path = Path.Combine(_directory, index + ".strm")
        }).ToArray();
        // Write independently of the current store, as an existing v2 catalog from 1.5.
        await using (var stream = File.Create(path))
            await JsonSerializer.SerializeAsync(stream, new { Items = items, Version = 2 });
        var previousLength = new FileInfo(path).Length;
        Assert.True(previousLength > 256L * 1024 * 1024);

        using (var state = new SiphonStateStore(paths))
        {
            AssertCatalog(state);
            Assert.Equal(previousLength, new FileInfo(path).Length);
            items[0] = items[0] with { Name = "Updated after upgrade" };
            await state.SaveAsync(items, CancellationToken.None);
            AssertCatalog(state);
        }
        Assert.True(new FileInfo(path).Length > 256L * 1024 * 1024);
        using var restarted = new SiphonStateStore(paths);
        AssertCatalog(restarted);

        void AssertCatalog(SiphonStateStore state)
        {
            Assert.Equal(items.Length, state.GetReadSnapshot().Items.Count);
            foreach (var expected in items)
            {
                var actual = Assert.IsType<ManagedItemSnapshot>(state.ReadByKey(expected.Key));
                Assert.Equal(expected.Key, state.ReadByPath(expected.Path)?.Key);
                Assert.Equal(expected.ContentKey, actual.ContentKey);
                Assert.Equal(expected.VideoId, actual.VideoId);
                Assert.Equal(expected.Name, actual.Name);
                Assert.Equal(expected.Description, actual.Description);
                Assert.Equal(expected.Owners, actual.Owners);
                Assert.Equal(expected.MissingOwners, actual.MissingOwners);
                Assert.Equal("tt1234567", actual.ProviderIds["Imdb"]);
                Assert.Equal("Addon", actual.MetadataProvenance["Name"].Sources[0].Origin);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentSchemaWithVersionAfterItemsKeepsNativeIdentitiesAndProvenance(bool byteOrderMark)
    {
        var paths = Paths();
        Directory.CreateDirectory(paths.DataDirectory);
        var item = Item() with { Description = new string('é', 20_000) };
        var path = Path.Combine(paths.DataDirectory, "state.json");
        var document = JsonSerializer.Serialize(new { Items = new[] { item }, Version = 2 });
        await File.WriteAllTextAsync(path, document, new UTF8Encoding(byteOrderMark));
        using var state = new SiphonStateStore(paths);
        var restored = Assert.Single(state.GetReadSnapshot().Items);
        Assert.Equal(item.Key, restored.Key);
        Assert.Equal(item.Path, restored.Path);
        Assert.Equal(item.ContentKey, restored.ContentKey);
        Assert.Equal(item.Description, restored.Description);
        Assert.Equal(item.VideoId, Assert.Single(restored.StreamIdentities).VideoId);
        Assert.Equal("Addon", restored.MetadataProvenance["Name"].Sources[0].Origin);
        Assert.Equal(document, await File.ReadAllTextAsync(path));
    }

    private ManagedItem Item() => new()
    {
        Key = "movie:tt1234567",
        Type = "movie",
        ContentId = "tt1234567",
        ContentKey = "movie:tt1234567",
        VideoId = "tt1234567",
        Name = "Film",
        Path = Path.Combine(_directory, "film.strm"),
        Owners = ["catalog"],
        MissingOwners = ["catalog"],
        Genres = ["Drama"],
        ProductionLocations = ["France"],
        People = [new("Person", "Actor")],
        EpisodePeople = [new("Episode person", "Director")],
        EpisodeGenres = ["Drama"],
        EpisodeProductionLocations = ["France"],
        ProviderIds = new(StringComparer.OrdinalIgnoreCase) { ["Imdb"] = "tt1234567" },
        EpisodeProviderIds = new(StringComparer.OrdinalIgnoreCase) { ["Tvdb"] = "episode" },
        StreamIdentities = [new("movie", "tt1234567")],
        MetadataProvenance = new(StringComparer.Ordinal) { ["Name"] = new([new("Addon", null, DateTimeOffset.UnixEpoch)]) }
    };

    private SiphonPaths Paths()
    {
        var proxy = DispatchProxy.Create<IApplicationPaths, SecurityStateTests.PathsProxy>();
        ((SecurityStateTests.PathsProxy)(object)proxy).DataPath = _directory;
        return new(proxy);
    }

    private sealed class MutableState(ManagedItem item) : ISiphonStateStore
    {
        public IReadOnlyList<ManagedItem> GetItems() => [item];
        public ManagedItem? FindByKey(string key) => key == item.Key ? item : null;
        public ManagedItem? FindByPath(string path) => path == item.Path ? item : null;
        public ManagedItem? FindByContentKey(string contentKey) => contentKey == item.ContentKey ? item : null;
        public Task SaveAsync(IReadOnlyList<ManagedItem> items, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
