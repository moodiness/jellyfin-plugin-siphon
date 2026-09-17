using System.Globalization;
using System.Reflection;
using Jellyfin.Plugin.Siphon.Playback;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Playback;

public sealed class NativeSourceLifetimeTests
{
    [Fact]
    public async Task FiniteSourceKeepsProbedTracksUsableAfterNativeOpeningLeaseIsClosed()
    {
        var video = new Movie { Id = Guid.NewGuid() };
        video.SetProviderId("Siphon", "movie:fixture");
        video.SetProviderId(NativeVersionService.SourceProvider, "selected-source");
        video.SetProviderId(NativeVersionService.SourceOrderProvider, "0");
        var native = DispatchProxy.Create<IMediaSourceManager, Sources>();
        var backend = (Sources)(object)native;
        backend.Source = new MediaSourceInfo
        {
            Id = video.Id.ToString("N"),
            Path = "https://server.example/selected-source",
            RunTimeTicks = TimeSpan.FromMinutes(14).Ticks,
            MediaStreams = [new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" }, new() { Type = MediaStreamType.Audio, Index = 2, Language = "fra", Codec = "aac" }]
        };
        var library = DispatchProxy.Create<ILibraryManager, Library>();
        ((Library)(object)library).Video = video;
        var manager = new NativeMediaSourceManager(native, library);
        var opened = await manager.OpenLiveStream(new LiveStreamRequest
        {
            OpenToken = typeof(SiphonMediaSourceProvider).FullName!.GetMD5().ToString("N", CultureInfo.InvariantCulture) + "_fixture"
        }, CancellationToken.None);

        Assert.Null(opened.MediaSource.LiveStreamId);
        Assert.False(opened.MediaSource.RequiresClosing);
        Assert.False(backend.LeaseOpen);
        var resumed = await manager.GetMediaSource(video, video.Id.ToString("N"), null, false, CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(14).Ticks, resumed.RunTimeTicks);
        Assert.Equal("fra", Assert.Single(resumed.MediaStreams, stream => stream.Type == MediaStreamType.Audio).Language);
        Assert.Equal(2, Assert.Single(resumed.MediaStreams, stream => stream.Type == MediaStreamType.Audio).Index);
        video.SetProviderId(NativeVersionService.SourceOrderProvider, "-1");
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => manager.GetMediaSource(video, video.Id.ToString("N"), null, false, CancellationToken.None));
    }

    [Fact]
    public async Task OtherProvidersRetainTheirNativeTunerLease()
    {
        var native = DispatchProxy.Create<IMediaSourceManager, Sources>();
        var backend = (Sources)(object)native;
        backend.Source = new MediaSourceInfo { Id = "tuner" };
        var manager = new NativeMediaSourceManager(native, null!);
        var opened = await manager.OpenLiveStream(new LiveStreamRequest { OpenToken = "another-provider_tuner" }, CancellationToken.None);
        Assert.Equal("native-lease", opened.MediaSource.LiveStreamId);
        Assert.True(opened.MediaSource.RequiresClosing);
        Assert.True(backend.LeaseOpen);
    }

    public class Sources : DispatchProxy
    {
        public MediaSourceInfo Source { get; set; } = null!;
        public bool LeaseOpen { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "OpenLiveStream")
            {
                LeaseOpen = true;
                return Task.FromResult(new LiveStreamResponse(new MediaSourceInfo
                {
                    Id = Source.Id,
                    MediaStreams = Source.MediaStreams,
                    RunTimeTicks = Source.RunTimeTicks,
                    LiveStreamId = "native-lease",
                    RequiresClosing = true
                }));
            }
            if (method?.Name == "CloseLiveStream")
            {
                LeaseOpen = false;
                return Task.CompletedTask;
            }
            if (method?.Name == "GetStaticMediaSources") return new[] { Source };
            throw new NotSupportedException(method?.Name);
        }
    }

    public class Library : DispatchProxy
    {
        public Movie Video { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "GetItemById" => args![0] is Guid id && id == Video.Id ? Video : null,
            "GetLinkedAlternateVersions" => Array.Empty<Video>(),
            _ => throw new NotSupportedException(method?.Name)
        };
    }
}
