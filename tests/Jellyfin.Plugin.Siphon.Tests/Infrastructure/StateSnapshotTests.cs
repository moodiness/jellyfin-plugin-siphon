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
    public void OversizedMalformedDocumentIsRejectedBeforeParsingAndNeverTruncated()
    {
        var paths = Paths();
        Directory.CreateDirectory(paths.DataDirectory);
        var path = Path.Combine(paths.DataDirectory, "state.json");
        using (var file = File.Create(path))
        {
            file.Write(Encoding.UTF8.GetBytes("not-json"));
            file.SetLength(SiphonStateStore.MaximumDocumentBytes + 1);
        }
        var error = Assert.Throws<InvalidDataException>(() => new SiphonStateStore(paths));
        Assert.Null(error.InnerException); // Malformed JSON was not handed to the parser.
        Assert.Equal(SiphonStateStore.MaximumDocumentBytes + 1, new FileInfo(path).Length);
        using var retained = File.OpenRead(path);
        var prefix = new byte[8];
        retained.ReadExactly(prefix);
        Assert.Equal("not-json", Encoding.UTF8.GetString(prefix));
    }

    [Fact]
    public async Task WriterAcceptsExactReadBoundaryAndRejectsOneExtraByteWithoutCommitting()
    {
        var paths = Paths();
        var item = Item();
        using (var initial = new SiphonStateStore(paths))
            await initial.SaveAsync([item], CancellationToken.None);
        var path = Path.Combine(paths.DataDirectory, "state.json");
        var durable = await File.ReadAllBytesAsync(path);
        using var bounded = new SiphonStateStore(paths, durable.LongLength);
        await bounded.SaveAsync([item], CancellationToken.None);
        var revision = bounded.GetReadSnapshot().Revision;
        await Assert.ThrowsAsync<InvalidDataException>(() => bounded.SaveAsync([item with { Name = item.Name + "a" }], CancellationToken.None));
        Assert.Equal(revision, bounded.GetReadSnapshot().Revision);
        Assert.Equal(item.Name, bounded.ReadByKey(item.Key)?.Name);
        Assert.Equal(durable, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(paths.DataDirectory, "state.json.*.tmp"));
        using var restarted = new SiphonStateStore(paths, durable.LongLength);
        Assert.Equal(item.Key, restarted.ReadByPath(item.Path)?.Key);
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
