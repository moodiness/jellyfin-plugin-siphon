using System.Text.Json;
using Jellyfin.Plugin.Siphon.Playback;
using Jellyfin.Plugin.Siphon.Protocol;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Playback;

public sealed class SourceBindingStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "siphon-bindings-" + Guid.NewGuid().ToString("N"));
    private const string Identity = "installation\nMirror\nFilm.mkv\n100";
    private string StorePath => Path.Combine(_directory, "source-bindings.json");

    [Fact]
    public void ContentQueryParametersSeparateMirrorsWhileSigningParametersMayRotate()
    {
        var store = new SourceBindingStore(StorePath);
        var first = store.Bind("movie", [Source("https://origin.example/get?file=A&token=initial"), Source("https://origin.example/get?file=B&token=initial")]);
        Assert.Equal(AddonRegistry.Digest(Identity), first[0].Id);
        Assert.Equal(AddonRegistry.Digest(Identity + "\n1"), first[1].Id);

        store = new SourceBindingStore(StorePath);
        var second = store.Bind("movie", [Source("https://origin.example/get?file=B&token=rotated")]);
        Assert.Equal(first[1].Id, Assert.Single(second).Id);
        var returned = store.Bind("movie", [Source("https://origin.example/get?file=A&token=returned")]);
        Assert.Equal(first[0].Id, Assert.Single(returned).Id);

        // An opaque URL with no stable path or content query cannot justify dropping its token.
        var opaque = store.Bind("opaque", [Source("https://origin.example/get?token=A")]);
        var changed = store.Bind("opaque", [Source("https://origin.example/get?token=B")]);
        Assert.NotEqual(Assert.Single(opaque).Id, Assert.Single(changed).Id);
    }

    [Fact]
    public void CapacityNeverEvictsOrRecyclesARetiredBinding()
    {
        var store = new SourceBindingStore(StorePath, maximumBindings: 2);
        var initial = store.Bind("movie", [Source("https://origin.example/a.mkv"), Source("https://origin.example/b.mkv")]);
        Assert.Equal(initial[1].Id, Assert.Single(store.Bind("movie", [Source("https://origin.example/b.mkv")])).Id);
        Assert.Throws<InvalidOperationException>(() => store.Bind("movie", [Source("https://origin.example/c.mkv")]));

        store = new SourceBindingStore(StorePath, maximumBindings: 2);
        Assert.Equal(initial[0].Id, Assert.Single(store.Bind("movie", [Source("https://origin.example/a.mkv")])).Id);
        Assert.Throws<InvalidOperationException>(() => store.Bind("movie", [Source("https://origin.example/c.mkv")]));
    }

    [Fact]
    public void FailedPersistenceCannotPublishOrReserveAnUncommittedIdentity()
    {
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(StorePath);
        var store = new SourceBindingStore(StorePath);
        var error = Record.Exception(() => store.Bind("movie", [Source("https://origin.example/a.mkv")]));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Directory.Delete(StorePath);
        var committed = Assert.Single(store.Bind("movie", [Source("https://origin.example/b.mkv")]));
        Assert.Equal(AddonRegistry.Digest(Identity), committed.Id);

        store = new SourceBindingStore(StorePath);
        Assert.Equal(committed.Id, Assert.Single(store.Bind("movie", [Source("https://origin.example/b.mkv")])).Id);
        Assert.NotEqual(committed.Id, Assert.Single(store.Bind("movie", [Source("https://origin.example/a.mkv")])).Id);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Version\":1}")]
    [InlineData("{\"Version\":2,\"Groups\":{}}")]
    public void CorruptOrUnknownStorageNeverBootstrapsReplacementBindings(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StorePath, content);
        var store = new SourceBindingStore(StorePath);
        var error = Record.Exception(() => store.Bind("movie", [Source("https://origin.example/a.mkv")]));
        Assert.True(error is JsonException or InvalidDataException);
        Assert.Equal(content, File.ReadAllText(StorePath));
    }

    private static (string Identity, ResolvedStream Stream) Source(string url)
        => (Identity, new ResolvedStream(AddonRegistry.Digest(Identity), "Mirror", new Uri(url), new Dictionary<string, string>(), "Film.mkv", 100));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
