using System.Globalization;
using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Siphon.Playback;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Playback;

public sealed class NativeSourceLifetimeTests
{
    [Fact]
    public async Task FiniteSourceKeepsProbedTracksUsableAfterNativeOpeningLeaseIsClosed()
    {
        var user = new User("viewer", "authentication", "password-reset") { Id = Guid.NewGuid() };
        var primary = new Movie { Id = Guid.NewGuid() };
        primary.SetProviderId("Siphon", "movie:fixture");
        var video = new Movie { Id = Guid.NewGuid(), PrimaryVersionId = primary.Id };
        video.SetProviderId("Siphon", "movie:fixture");
        video.SetProviderId(NativeVersionService.SourceProvider, "selected-source");
        video.SetProviderId(NativeVersionService.SourceOrderProvider, "0");
        video.SetProviderId(NativeVersionService.VersionProvider, primary.Id.ToString("N"));
        video.SetProviderId(NativeVersionService.OwnerProvider, user.Id.ToString("N"));
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
        ((Library)(object)library).Primary = primary;
        var manager = new NativeMediaSourceManager(native, library, CreateAccess(library, user));
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
        var manager = new NativeMediaSourceManager(native, null!, null!);
        var opened = await manager.OpenLiveStream(new LiveStreamRequest { OpenToken = "another-provider_tuner" }, CancellationToken.None);
        Assert.Equal("native-lease", opened.MediaSource.LiveStreamId);
        Assert.True(opened.MediaSource.RequiresClosing);
        Assert.True(backend.LeaseOpen);
    }

    [Fact]
    public async Task ColdNativeDtoKeepsMetadataWithoutBorrowingAnotherAccountsSource()
    {
        var user = new User("viewer", "authentication", "password-reset") { Id = Guid.NewGuid() };
        var primary = new Movie { Id = Guid.NewGuid() };
        primary.SetProviderId("Siphon", "movie:fixture");
        var foreign = new Movie { Id = Guid.NewGuid(), PrimaryVersionId = primary.Id };
        foreign.SetProviderId("Siphon", "movie:fixture");
        foreign.SetProviderId(NativeVersionService.VersionProvider, primary.Id.ToString("N"));
        foreign.SetProviderId(NativeVersionService.OwnerProvider, Guid.NewGuid().ToString("N"));
        foreign.SetProviderId(NativeVersionService.SourceProvider, "private-source");
        foreign.SetProviderId(NativeVersionService.SourceOrderProvider, "0");
        var library = DispatchProxy.Create<ILibraryManager, Library>();
        ((Library)(object)library).Primary = primary;
        ((Library)(object)library).Video = foreign;
        var native = DispatchProxy.Create<IMediaSourceManager, Sources>();
        ((Sources)(object)native).Source = new MediaSourceInfo
        {
            Id = foreign.Id.ToString("N"),
            Name = "Private account label",
            Path = "https://private.example/secret",
            OpenToken = "private-capability",
            MediaStreams = [new() { Type = MediaStreamType.Audio, Language = "fra" }]
        };
        var manager = new NativeMediaSourceManager(native, library, CreateAccess(library, user));
        // Jellyfin's DTO builder indexes element zero before asynchronous source discovery.
        var metadata = manager.GetStaticMediaSources(primary, true)[0];
        Assert.Equal(primary.Id.ToString("N"), metadata.Id);
        Assert.Null(metadata.Path);
        Assert.Null(metadata.OpenToken);
        Assert.NotEqual("Private account label", metadata.Name);
        Assert.Empty(metadata.MediaStreams);
        Assert.False(metadata.SupportsDirectPlay || metadata.SupportsDirectStream || metadata.SupportsTranscoding || metadata.SupportsProbing);
        Assert.Empty(await manager.GetPlaybackMediaSources(primary, user, true, true, CancellationToken.None));
    }

    internal static PlaybackAccess CreateAccess(ILibraryManager library, User? user, bool authenticated = true)
    {
        var authorization = DispatchProxy.Create<IAuthorizationContext, UserServices>();
        ((UserServices)(object)authorization).User = authenticated ? user : null;
        var users = DispatchProxy.Create<IUserManager, UserServices>();
        ((UserServices)(object)users).User = user;
        return new PlaybackAccess(new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, authorization, users, library);
    }

    public class UserServices : DispatchProxy
    {
        public User? User { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "GetAuthorizationInfo" => Task.FromResult(new AuthorizationInfo { User = User }),
            "GetUserById" => args![0] is Guid id && id == User?.Id ? User : null,
            _ => throw new NotSupportedException(method?.Name)
        };
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
            if (method?.Name == "GetPlaybackMediaSources") return Task.FromResult<IReadOnlyList<MediaSourceInfo>>([Source]);
            throw new NotSupportedException(method?.Name);
        }
    }

    public class Library : DispatchProxy
    {
        public Movie Video { get; set; } = null!;
        public Movie? Primary { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "GetItemById" => args![0] is Guid id ? id == Video.Id ? Video : id == Primary?.Id ? Primary : null : null,
            "GetItemList" => Primary is null ? new BaseItem[] { Video } : new BaseItem[] { Primary, Video },
            "ConfigureUserAccess" => null,
            "GetLinkedAlternateVersions" => Primary is null ? Array.Empty<Video>() : new Video[] { Video },
            _ => throw new NotSupportedException(method?.Name)
        };
    }
}
