using System.Reflection;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Identity;

public sealed class ImageMappingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "siphon-artwork-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CachedParentPosterCopiesUpgradeWithoutReplacingDistinctManualArtwork()
    {
        Directory.CreateDirectory(_directory);
        var parentPath = Path.Combine(_directory, "parent.png");
        var fallbackPath = Path.Combine(_directory, "fallback.png");
        var manualPath = Path.Combine(_directory, "manual.png");
        File.WriteAllBytes(parentPath, [1, 2, 3, 4]);
        File.WriteAllBytes(fallbackPath, [1, 2, 3, 4]);
        File.WriteAllBytes(manualPath, [4, 3, 2, 1]);
        var series = new Series { Id = Guid.NewGuid() };
        series.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = parentPath, DateModified = DateTime.UnixEpoch }, 0);
        var (mapper, _) = CreateMapper(series);
        var item = MetadataPolicyTests.EpisodeItem() with
        {
            PosterUrl = "https://art.example/series.png",
            SeasonPosterUrl = "https://art.example/season-one.png",
            ThumbnailUrl = "https://art.example/episode-two.png"
        };
        var season = Cached<Season>(fallbackPath);
        var episode = Cached<Episode>(fallbackPath);
        var manual = Cached<Episode>(manualPath);
        var locked = Cached<Season>(fallbackPath);
        locked.IsLocked = true;

        mapper.Apply(season, item, "season", []);
        mapper.Apply(episode, item, item.Key, []);
        mapper.Apply(manual, item, item.Key, []);
        mapper.Apply(locked, item, "locked-season", []);

        Assert.EndsWith("?type=season", season.GetImageInfo(ImageType.Primary, 0).Path);
        Assert.EndsWith("?type=thumbnail", episode.GetImageInfo(ImageType.Primary, 0).Path);
        Assert.Equal(manualPath, manual.GetImageInfo(ImageType.Primary, 0).Path);
        Assert.Equal(fallbackPath, locked.GetImageInfo(ImageType.Primary, 0).Path);
        Assert.True(locked.IsLocked);
    }

    [Fact]
    public void SeriesArtworkCapabilityDoesNotDependOnOneRetainedEpisode()
    {
        var series = new Series();
        var (mapper, tokens) = CreateMapper(series);
        var episode = MetadataPolicyTests.EpisodeItem() with { PosterUrl = "https://art.example/series.png", BackdropUrl = "https://art.example/backdrop.png" };
        mapper.Apply(series, episode, episode.ContentKey, []);
        foreach (var type in new[] { ImageType.Primary, ImageType.Backdrop })
        {
            var url = new Uri(series.GetImageInfo(type, 0).Path);
            Assert.True(tokens.TryReadItem(url.Segments[^1], out var key));
            Assert.Equal(episode.ContentKey, key);
        }
    }

    [Fact]
    public void ParentArtworkDoesNotPopulateChildrenOrReplaceManualSeasonArt()
    {
        var settings = new PluginConfiguration { MetadataUpdateMode = "RefreshSelected", MetadataRefreshFields = ["Images"] };
        var (mapper, _) = CreateMapper(new Series(), settings);
        var parentOnly = MetadataPolicyTests.EpisodeItem() with { PosterUrl = "https://art.example/parent.png" };
        var season = new Season();
        var episode = new Episode();
        var manual = Cached<Season>(Path.Combine(_directory, "manual.png"));

        mapper.Apply(season, parentOnly, "season", []);
        mapper.Apply(episode, parentOnly, parentOnly.Key, []);
        mapper.Apply(manual, parentOnly, "manual-season", []);

        Assert.False(season.HasImage(ImageType.Primary));
        Assert.False(episode.HasImage(ImageType.Primary));
        Assert.Equal(Path.Combine(_directory, "manual.png"), manual.GetImageInfo(ImageType.Primary, 0).Path);
    }

    private (ItemMetadataMapper Mapper, CapabilityTokenService Tokens) CreateMapper(Series series, PluginConfiguration? settings = null)
    {
        var applicationPaths = DispatchProxy.Create<IApplicationPaths, CatalogSyncTests.PathsProxy>();
        ((CatalogSyncTests.PathsProxy)(object)applicationPaths).DataPath = _directory;
        var tokens = new CapabilityTokenService(new SiphonSecretStore(new SiphonPaths(applicationPaths)));
        var library = DispatchProxy.Create<ILibraryManager, LibraryProxy>();
        ((LibraryProxy)(object)library).Series = series;
        settings ??= new PluginConfiguration();
        settings.PublicBaseUrl = "https://jellyfin.example/base";
        var config = new ConfigurationAccessor(() => settings);
        return (new ItemMetadataMapper(config, tokens, library, null!, null!), tokens);
    }

    private static T Cached<T>(string path) where T : BaseItem, new()
    {
        var item = new T { DateLastSaved = DateTime.UtcNow };
        item.SetProviderId("SiphonImagePrimary", "legacy-managed-image");
        item.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = path, DateModified = DateTime.UnixEpoch }, 0);
        return item;
    }

    public class LibraryProxy : DispatchProxy
    {
        public Series Series { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "GetNewItemId" => Series.Id,
            "GetItemById" => Series,
            _ => throw new NotSupportedException(targetMethod?.Name)
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
