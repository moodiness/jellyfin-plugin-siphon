using System.Reflection;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Playback;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Playback;

public sealed class NativeMediaInfoTests
{
    [Fact]
    public async Task OpeningVersionRetainsItsExternalTracksButReplacesStaleEmbeddedTracks()
    {
        var version = new Movie { Id = Guid.NewGuid() };
        version.SetProviderId("Siphon", "movie:tt1234567");
        var library = DispatchProxy.Create<ILibraryManager, Library>();
        ((Library)(object)library).Version = version;
        var repository = DispatchProxy.Create<IMediaStreamRepository, Streams>();
        var stored = (Streams)(object)repository;
        stored.ByItem[version.Id] =
        [
            new() { Index = 0, Type = MediaStreamType.Video, Codec = "h264" },
            new() { Index = 1, Type = MediaStreamType.Audio, Language = "fra" },
            new() { Index = 2, Type = MediaStreamType.Subtitle, IsExternal = true, Path = "/metadata/caption.eng.srt", Codec = "srt", Language = "eng" }
        ];
        var otherVersion = Guid.NewGuid();
        stored.ByItem[otherVersion] =
        [
            new() { Index = 2, Type = MediaStreamType.Subtitle, IsExternal = true, Path = "/metadata/other.fra.srt", Codec = "srt", Language = "fra" }
        ];
        var service = Service(library, repository);
        var opened = Probe();

        await service.SaveMediaInfoAsync(version.Id, opened, CancellationToken.None);

        Assert.Equal("eng", Assert.Single(opened.MediaStreams, stream => stream.Type == MediaStreamType.Audio).Language);
        var caption = Assert.Single(opened.MediaStreams, stream => stream.Type == MediaStreamType.Subtitle);
        Assert.Equal("/metadata/caption.eng.srt", caption.Path);
        Assert.Equal(2, caption.Index);
        Assert.True(caption.SupportsExternalStream);
        Assert.True(version.HasSubtitles);
        Assert.Equal(opened.RunTimeTicks, version.RunTimeTicks);
        Assert.Equal("/metadata/caption.eng.srt", Assert.Single(stored.ByItem[version.Id], stream => stream.IsExternal).Path);
        Assert.Equal("/metadata/other.fra.srt", Assert.Single(stored.ByItem[otherVersion]).Path);

        var reopened = Probe();
        await service.SaveMediaInfoAsync(version.Id, reopened, CancellationToken.None);
        Assert.Equal(2, Assert.Single(reopened.MediaStreams, stream => stream.IsExternal).Index);
    }

    [Fact]
    public async Task ChangedEmbeddedLayoutReindexesOnlyCollidingExternalTracksAndKeepsNewIndicesOnReopen()
    {
        var version = new Movie { Id = Guid.NewGuid() };
        version.SetProviderId("Siphon", "movie:tt1234567");
        var library = DispatchProxy.Create<ILibraryManager, Library>();
        ((Library)(object)library).Version = version;
        var repository = DispatchProxy.Create<IMediaStreamRepository, Streams>();
        var stored = (Streams)(object)repository;
        stored.ByItem[version.Id] =
        [
            new() { Index = 2, Type = MediaStreamType.Subtitle, IsExternal = true, Path = "/metadata/caption.eng.srt", Codec = "srt" },
            new() { Index = 4, Type = MediaStreamType.Subtitle, IsExternal = true, Path = "/metadata/caption.fra.srt", Codec = "srt" }
        ];
        var service = Service(library, repository);
        var opened = Probe();
        opened.MediaStreams = [.. opened.MediaStreams, new() { Index = 2, Type = MediaStreamType.Audio, Language = "jpn" }];

        await service.SaveMediaInfoAsync(version.Id, opened, CancellationToken.None);

        Assert.Equal(2, Assert.Single(opened.MediaStreams, stream => stream.Language == "jpn").Index);
        Assert.Equal(4, Assert.Single(opened.MediaStreams, stream => stream.Path == "/metadata/caption.fra.srt").Index);
        Assert.Equal(5, Assert.Single(opened.MediaStreams, stream => stream.Path == "/metadata/caption.eng.srt").Index);
        var reopened = Probe();
        reopened.MediaStreams = [.. reopened.MediaStreams, new() { Index = 2, Type = MediaStreamType.Audio, Language = "jpn" }];
        await service.SaveMediaInfoAsync(version.Id, reopened, CancellationToken.None);
        Assert.Equal(5, Assert.Single(reopened.MediaStreams, stream => stream.Path == "/metadata/caption.eng.srt").Index);
    }

    private static MediaSourceInfo Probe() => new()
    {
        Container = "matroska",
        RunTimeTicks = TimeSpan.FromMinutes(95).Ticks,
        MediaStreams =
        [
            new() { Index = 0, Type = MediaStreamType.Video, Codec = "hevc" },
            new() { Index = 1, Type = MediaStreamType.Audio, Language = "eng" }
        ]
    };

    private static NativeVersionService Service(ILibraryManager library, IMediaStreamRepository streams)
        => new(new ConfigurationAccessor(() => new PluginConfiguration()), null!, null!, library,
            DispatchProxy.Create<IItemPersistenceService, Persistence>(), streams, null!, null!, null!);

    public class Library : DispatchProxy
    {
        public Movie Version { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "GetItemById" => args![0] is Guid id && id == Version.Id ? Version : null,
            "RegisterItem" => null,
            _ => throw new NotSupportedException(method?.Name)
        };
    }

    public class Streams : DispatchProxy
    {
        public Dictionary<Guid, IReadOnlyList<MediaStream>> ByItem { get; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "GetMediaStreams") return ByItem.GetValueOrDefault(((MediaStreamQuery)args![0]!).ItemId) ?? [];
            if (method?.Name == "SaveMediaStreams")
            {
                ByItem[(Guid)args![0]!] = ((IReadOnlyList<MediaStream>)args[1]!).ToArray();
                return null;
            }
            throw new NotSupportedException(method?.Name);
        }
    }

    public class Persistence : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.Name == "SaveItems" ? null : throw new NotSupportedException(method?.Name);
    }
}
