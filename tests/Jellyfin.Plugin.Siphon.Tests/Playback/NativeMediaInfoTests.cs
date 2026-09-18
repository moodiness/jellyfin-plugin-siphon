using System.Net;
using System.Reflection;
using System.Text;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Playback;
using Jellyfin.Plugin.Siphon.Protocol;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Playback;

public sealed class NativeMediaInfoTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task OpeningProbesNativeHttpWhileReturningPublicPlaybackAndPreservingSourceOwnership(string boundHost)
    {
        var directory = Path.Combine(Path.GetTempPath(), "siphon-native-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var applicationPaths = DispatchProxy.Create<IApplicationPaths, ProxyBehaviorTests.PathsProxy>();
            ((ProxyBehaviorTests.PathsProxy)(object)applicationPaths).DataPath = directory;
            var paths = new SiphonPaths(applicationPaths);
            var configuration = new ConfigurationAccessor(() => new PluginConfiguration
            {
                PublicBaseUrl = "https://unreachable.invalid:9443/public/jellyfin/",
                Addons = [new() { Id = "probe", ManifestUrl = "https://addon.invalid/manifest.json", Enabled = true }]
            });
            using var state = new SiphonStateStore(paths);
            var item = new ManagedItem
            {
                Key = "movie:tt1234567",
                Type = "movie",
                ContentId = "tt1234567",
                ContentKey = "movie:tt1234567",
                VideoId = "tt1234567",
                Name = "Film",
                Path = Path.Combine(directory, "Film.strm"),
                StreamIdentities = [new("movie", "tt1234567")]
            };
            await state.SaveAsync([item], CancellationToken.None);
            using var client = new StremioClient(new ProbeOrigin(), configuration);
            var registry = new AddonRegistry(client, configuration, NullLogger<AddonRegistry>.Instance);
            var resolver = new StreamResolver(client, registry, configuration, NullLogger<StreamResolver>.Instance, new SourceBindingStore(paths));
            var user = new User("viewer", "authentication", "password-reset") { Id = Guid.NewGuid() };
            user.SetPermission(PermissionKind.EnableMediaPlayback, true);
            var selected = Assert.Single(await resolver.GetSourcesAsync(item, user.Id, CancellationToken.None));
            var version = new Movie { Id = Guid.NewGuid(), PrimaryVersionId = Guid.NewGuid() };
            version.SetProviderId("Siphon", item.Key);
            version.SetProviderId(NativeVersionService.SourceProvider, selected.Id);
            version.SetProviderId(NativeVersionService.OwnerProvider, user.Id.ToString("N"));
            var library = DispatchProxy.Create<ILibraryManager, Library>();
            ((Library)(object)library).Version = version;
            var versions = Service(library, DispatchProxy.Create<IMediaStreamRepository, Streams>());
            var encoder = DispatchProxy.Create<IMediaEncoder, ProbeEncoder>();
            var observed = (ProbeEncoder)(object)encoder;
            var network = DispatchProxy.Create<INetworkManager, NativeNetwork>();
            ((NativeNetwork)(object)network).BoundHost = boundHost;
            var applicationHost = DispatchProxy.Create<IServerApplicationHost, NativeHost>();
            var tokens = new CapabilityTokenService(new SiphonSecretStore(paths));
            var provider = new SiphonMediaSourceProvider(state, versions, resolver, new ProxySessionStore(configuration),
                tokens, configuration, encoder, NativeSourceLifetimeTests.CreateAccess(library, user),
                DispatchProxy.Create<IMediaSegmentManager, Segments>(), applicationHost, network);
            var openToken = tokens.SignSource(item.Key, selected.Id);

            var opened = await provider.OpenMediaSource(openToken, [], CancellationToken.None);

            var probe = new Uri(Assert.Single(observed.Paths));
            var playable = new Uri(opened.MediaSource.Path);
            Assert.Equal("http", probe.Scheme);
            Assert.Equal(IPAddress.Parse(boundHost), IPAddress.Parse(probe.DnsSafeHost));
            Assert.Equal(18096, probe.Port);
            Assert.StartsWith("/native/jellyfin/Siphon/media/", probe.AbsolutePath);
            Assert.Equal("https", playable.Scheme);
            Assert.Equal("unreachable.invalid", playable.Host);
            Assert.Equal(9443, playable.Port);
            Assert.StartsWith("/public/jellyfin/Siphon/media/", playable.AbsolutePath);
            Assert.Equal(probe.AbsolutePath["/native/jellyfin".Length..], playable.AbsolutePath["/public/jellyfin".Length..]);
            Assert.Equal(probe.AbsoluteUri, opened.MediaSource.EncoderPath);
            Assert.Equal(MediaProtocol.Http, opened.MediaSource.EncoderProtocol);

            // A valid source capability must not bypass a changed native owner or playback policy.
            version.SetProviderId(NativeVersionService.OwnerProvider, Guid.NewGuid().ToString("N"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.OpenMediaSource(openToken, [], CancellationToken.None));
            version.SetProviderId(NativeVersionService.OwnerProvider, user.Id.ToString("N"));
            user.SetPermission(PermissionKind.EnableMediaPlayback, false);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.OpenMediaSource(openToken, [], CancellationToken.None));
            Assert.Single(observed.Paths);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

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
            "GetItemList" => new List<BaseItem> { Version },
            "ConfigureUserAccess" => null,
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

    public class ProbeEncoder : DispatchProxy
    {
        public List<string> Paths { get; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != "GetMediaInfo") throw new NotSupportedException(method?.Name);
            // Snapshot Path now: OpenMediaSource mutates the same source back to public after probing.
            Paths.Add(((MediaInfoRequest)args![0]!).MediaSource.Path);
            return Task.FromResult(new MediaInfo { Container = "matroska", MediaStreams = Probe().MediaStreams });
        }
    }

    public class NativeNetwork : DispatchProxy
    {
        public string BoundHost { get; set; } = "127.0.0.1";
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != "GetBindAddress") throw new NotSupportedException(method?.Name);
            var skipOverrides = args!.Length == 3 && args[2] is true;
            args[1] = skipOverrides ? null : 9443;
            return skipOverrides ? BoundHost : "published.invalid";
        }
    }

    public class NativeHost : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_HttpPort" => 18096,
            "GetLocalApiUrl" => new UriBuilder((string?)args![1] ?? "https", (string)args[0]!,
                (int?)args[2] ?? 9443, "/native/jellyfin/").Uri.AbsoluteUri,
            _ => throw new NotSupportedException(method?.Name)
        };
    }

    public class Segments : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.Name == "HasSegments" ? false : throw new NotSupportedException(method?.Name);
    }

    private sealed class ProbeOrigin : ISafeHttpClient
    {
        public Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            var body = uri.AbsolutePath switch
            {
                "/manifest.json" => """{"id":"probe","resources":["stream"],"types":["movie"]}""",
                "/stream/movie/tt1234567.json" => """{"streams":[{"name":"Film","url":"https://media.invalid/film.mkv"}]}""",
                _ => throw new InvalidOperationException("Unexpected fixture request.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(method, uri),
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
