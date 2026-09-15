using System.Reflection;
using Jellyfin.Plugin.Siphon.Channels;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Channels;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Channels;

public sealed class SiphonChannelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "siphon-channel-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExposesEnabledCatalogFoldersAndMediaWithoutFilesystemRoots()
    {
        var state = new MemoryState
        {
            Items =
            [
                new ManagedItem
                {
                    Key = "movie:tt1234567",
                    Type = "movie",
                    ContentId = "tt1234567",
                    ContentKey = "movie:tt1234567",
                    VideoId = "tt1234567",
                    Name = "Film",
                    PosterUrl = "https://images.example/film.jpg",
                    ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt1234567" },
                    Path = "/siphon/items/movie/film",
                    Owners = ["installation:catalog"]
                }
            ]
        };
        var configuration = new ConfigurationAccessor(() => new PluginConfiguration
        {
            PublicBaseUrl = "https://jellyfin.example",
            Addons =
            [
                new ConfiguredAddon
                {
                    Id = "installation",
                    DisplayName = "Example addon",
                    Enabled = true,
                    Catalogs = [new CatalogSubscription { Key = "catalog", Type = "movie", Id = "top", Enabled = true }]
                }
            ]
        });
        var tokens = new CapabilityTokenService(new SiphonSecretStore(Paths()));
        var channel = new SiphonChannel(state, tokens, configuration);

        var folders = await channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);
        var folder = Assert.Single(folders.Items);
        Assert.Equal(ChannelItemType.Folder, folder.Type);
        Assert.Equal("installation:catalog", folder.Id);
        Assert.DoesNotContain("/media/", folder.Name, StringComparison.Ordinal);

        var media = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = folder.Id }, CancellationToken.None);
        var item = Assert.Single(media.Items);
        Assert.Equal(ChannelItemType.Media, item.Type);
        Assert.Equal("tt1234567", item.ProviderIds["Imdb"]);
        Assert.StartsWith("https://jellyfin.example/Siphon/s/", item.MediaSources[0].Path, StringComparison.Ordinal);
        Assert.StartsWith("https://jellyfin.example/Siphon/image/", item.ImageUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("images.example", item.ImageUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LatestMediaContainsItemsFromEnabledCatalogsOnly()
    {
        var state = new MemoryState
        {
            Items =
            [
                Item("movie:enabled", ["installation:enabled"]),
                Item("movie:disabled", ["installation:disabled"])
            ]
        };
        var configuration = new ConfigurationAccessor(() => new PluginConfiguration
        {
            PublicBaseUrl = "https://jellyfin.example",
            Addons =
            [
                new ConfiguredAddon
                {
                    Id = "installation",
                    Enabled = true,
                    Catalogs =
                    [
                        new CatalogSubscription { Key = "enabled", Type = "movie", Id = "enabled", Enabled = true },
                        new CatalogSubscription { Key = "disabled", Type = "movie", Id = "disabled", Enabled = false }
                    ]
                }
            ]
        });
        var channel = new SiphonChannel(state, new CapabilityTokenService(new SiphonSecretStore(Paths())), configuration);

        var latest = await channel.GetLatestMedia(new ChannelLatestMediaSearch(), CancellationToken.None);

        var item = Assert.Single(latest);
        Assert.Equal("movie:enabled", item.Id);
    }

    private static ManagedItem Item(string key, string[] owners) => new()
    {
        Key = key,
        Type = "movie",
        ContentId = key,
        ContentKey = key,
        VideoId = key,
        Name = key,
        Path = "/siphon/items/movie/" + key,
        Owners = owners
    };

    private SiphonPaths Paths()
    {
        Directory.CreateDirectory(_directory);
        var paths = DispatchProxy.Create<IApplicationPaths, PathsProxy>();
        ((PathsProxy)(object)paths).DataPath = _directory;
        return new SiphonPaths(paths);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class MemoryState : ISiphonStateStore
    {
        public IReadOnlyList<ManagedItem> Items { get; init; } = [];
        public IReadOnlyList<ManagedItem> GetItems() => Items;
        public ManagedItem? FindByKey(string key) => Items.FirstOrDefault(item => item.Key == key);
        public ManagedItem? FindByPath(string path) => Items.FirstOrDefault(item => item.Path == path);
        public ManagedItem? FindByContentKey(string contentKey) => Items.FirstOrDefault(item => item.ContentKey == contentKey);
        public Task SaveAsync(IReadOnlyList<ManagedItem> items, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private class PathsProxy : DispatchProxy
    {
        public string DataPath { get; set; } = string.Empty;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == "get_DataPath" ? DataPath : throw new NotSupportedException();
    }
}
