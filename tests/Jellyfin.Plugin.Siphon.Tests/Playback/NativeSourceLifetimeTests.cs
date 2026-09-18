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
        var retired = await manager.GetMediaSource(video, video.Id.ToString("N"), null, false, CancellationToken.None);
        Assert.Null(retired.Path);
        Assert.False(retired.SupportsDirectPlay || retired.SupportsDirectStream || retired.SupportsTranscoding || retired.SupportsProbing);
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

    [Fact]
    public async Task EquivalentNativeIdentifiersResolveTheSameProbedVersion()
    {
        var fixture = new ReportingFixture();
        var compact = await fixture.Manager.GetMediaSource(fixture.Primary, fixture.Version.Id.ToString("N"), null, false, CancellationToken.None);
        var hyphenated = await fixture.Manager.GetMediaSource(fixture.Primary, fixture.Version.Id.ToString("D"), null, false, CancellationToken.None);
        Assert.Equal(compact.Id, hyphenated.Id);
        Assert.Equal(compact.RunTimeTicks, hyphenated.RunTimeTicks);
        Assert.Equal("fra", Assert.Single(hyphenated.MediaStreams).Language);
    }

    [Fact]
    public async Task CanonicalAndRetiredVersionsRemainReportableWithoutOfferingPlayback()
    {
        var fixture = new ReportingFixture();
        var canonical = await fixture.Manager.GetMediaSource(fixture.Primary, fixture.Primary.Id.ToString("N"), null, false, CancellationToken.None);
        Assert.Equal(fixture.Primary.Id.ToString("N"), canonical.Id);
        Assert.Equal(fixture.Primary.RunTimeTicks, canonical.RunTimeTicks);
        AssertMetadataOnly(canonical);
        Assert.Single(await fixture.Manager.GetPlaybackMediaSources(fixture.Primary, fixture.User, true, true, CancellationToken.None));

        fixture.Version.SetProviderId(NativeVersionService.SourceOrderProvider, "-1");
        var retired = await fixture.Manager.GetMediaSource(fixture.Primary, fixture.Version.Id.ToString("D"), null, false, CancellationToken.None);
        Assert.Equal(fixture.Version.Id.ToString("N"), retired.Id);
        Assert.Equal(fixture.Version.RunTimeTicks, retired.RunTimeTicks);
        AssertMetadataOnly(retired);
        Assert.Empty(await fixture.Manager.GetPlaybackMediaSources(fixture.Primary, fixture.User, true, true, CancellationToken.None));

        fixture.Version.SetProviderId(NativeVersionService.SourceOrderProvider, "0");
        var restored = await fixture.Manager.GetMediaSource(fixture.Primary, fixture.Version.Id.ToString("N"), null, false, CancellationToken.None);
        Assert.Equal("fra", Assert.Single(restored.MediaStreams).Language);
        Assert.Single(await fixture.Manager.GetPlaybackMediaSources(fixture.Primary, fixture.User, true, true, CancellationToken.None));
    }

    [Fact]
    public async Task ReportableMetadataCannotCrossUserContentOrVersionBoundaries()
    {
        var fixture = new ReportingFixture();
        fixture.Version.SetProviderId(NativeVersionService.SourceOrderProvider, "-1");
        fixture.Version.SetProviderId(NativeVersionService.OwnerProvider, Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => fixture.Manager.GetMediaSource(fixture.Primary,
            fixture.Version.Id.ToString("N"), null, false, CancellationToken.None));
        fixture.Version.SetProviderId(NativeVersionService.OwnerProvider, fixture.User.Id.ToString("N"));
        fixture.Version.SetProviderId("Siphon", "movie:unrelated");
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => fixture.Manager.GetMediaSource(fixture.Primary,
            fixture.Version.Id.ToString("N"), null, false, CancellationToken.None));
        fixture.Version.SetProviderId("Siphon", "movie:fixture");
        fixture.Version.SetProviderId(NativeVersionService.VersionProvider, Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => fixture.Manager.GetMediaSource(fixture.Primary,
            fixture.Version.Id.ToString("N"), null, false, CancellationToken.None));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => fixture.Manager.GetMediaSource(fixture.Primary,
            "not-a-source", null, false, CancellationToken.None));
    }

    private static void AssertMetadataOnly(MediaSourceInfo source)
    {
        Assert.Null(source.Path);
        Assert.Null(source.OpenToken);
        Assert.Null(source.LiveStreamId);
        Assert.False(source.RequiresOpening || source.RequiresClosing);
        Assert.False(source.SupportsDirectPlay || source.SupportsDirectStream || source.SupportsTranscoding || source.SupportsProbing);
    }

    private sealed class ReportingFixture
    {
        public User User { get; } = new("viewer", "authentication", "password-reset") { Id = Guid.NewGuid() };
        public Movie Primary { get; } = new() { Id = Guid.NewGuid(), RunTimeTicks = TimeSpan.FromMinutes(10).Ticks };
        public Movie Version { get; } = new() { Id = Guid.NewGuid(), RunTimeTicks = TimeSpan.FromMinutes(14).Ticks };
        public NativeMediaSourceManager Manager { get; }

        public ReportingFixture()
        {
            Primary.SetProviderId("Siphon", "movie:fixture");
            Version.SetProviderId("Siphon", "movie:fixture");
            Version.PrimaryVersionId = Primary.Id;
            Version.SetProviderId(NativeVersionService.SourceProvider, "selected-source");
            Version.SetProviderId(NativeVersionService.SourceOrderProvider, "0");
            Version.SetProviderId(NativeVersionService.VersionProvider, Primary.Id.ToString("N"));
            Version.SetProviderId(NativeVersionService.OwnerProvider, User.Id.ToString("N"));
            var native = DispatchProxy.Create<IMediaSourceManager, Sources>();
            ((Sources)(object)native).Source = new MediaSourceInfo
            {
                Id = Version.Id.ToString("N"),
                Path = "https://server.example/selected-source",
                RequiresOpening = true,
                OpenToken = typeof(SiphonMediaSourceProvider).FullName!.GetMD5().ToString("N", CultureInfo.InvariantCulture) + "_fixture",
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                RunTimeTicks = Version.RunTimeTicks,
                MediaStreams = [new() { Type = MediaStreamType.Audio, Index = 2, Language = "fra", Codec = "aac" }]
            };
            var library = DispatchProxy.Create<ILibraryManager, Library>();
            ((Library)(object)library).Video = Version;
            ((Library)(object)library).Primary = Primary;
            Manager = new NativeMediaSourceManager(native, library, CreateAccess(library, User));
        }
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
