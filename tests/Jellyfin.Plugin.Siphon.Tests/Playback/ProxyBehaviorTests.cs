using System.Net;
using System.Net.Http.Headers;
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
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Playback;

public sealed class ProxyBehaviorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "siphon-proxy-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidCapabilityBurstsDoNotConsumeValidPlaybackOrArtworkAdmission(bool artwork)
    {
        var content = artwork ? new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 } : BinaryFixture();
        using var fixture = CreateFixture(content, "https://upstream.example/opaque", artwork ? "image/png" : "video/mp4");
        await fixture.Store.SaveAsync([Item() with { PosterUrl = "https://upstream.example/poster.png" }], CancellationToken.None);
        var imageToken = new CapabilityTokenService(new SiphonSecretStore(Paths())).SignItem(Item().Key);
        for (var attempt = 0; attempt < 192; attempt++)
        {
            var invalid = NewContext("GET", "");
            invalid.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.100");
            fixture.Controller.ControllerContext = new ControllerContext { HttpContext = invalid };
            if (artwork) await fixture.Controller.Image("invalid-reference");
            else await fixture.Controller.Media("invalid-reference");
            Assert.Contains(invalid.Response.StatusCode, new[] { 404, 429 });
            Assert.Empty(((MemoryStream)invalid.Response.Body).ToArray());
        }

        var valid = NewContext("GET", "");
        valid.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.100");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = valid };
        if (artwork) await fixture.Controller.Image(imageToken);
        else await fixture.Controller.Media(fixture.Session.Token);
        Assert.Equal(200, valid.Response.StatusCode);
        Assert.Equal(content, ((MemoryStream)valid.Response.Body).ToArray());
    }
    [Theory]
    [InlineData("Disabled")]
    [InlineData("PlaybackDisabled")]
    [InlineData("ScheduleClosed")]
    public async Task AnonymousCapabilityStopsWhenItsOwnersNativePolicyIsRevoked(string policy)
    {
        using var fixture = CreateFixture(BinaryFixture(), "https://upstream.example/movie.mp4");
        var allowed = NewContext("GET", "bytes=0-9");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = allowed };
        await fixture.Controller.Media(fixture.Session.Token);
        Assert.Equal(206, allowed.Response.StatusCode);
        if (policy == "Disabled") fixture.User.SetPermission(PermissionKind.IsDisabled, true);
        else if (policy == "PlaybackDisabled") fixture.User.SetPermission(PermissionKind.EnableMediaPlayback, false);
        else fixture.User.AccessSchedules.Add(new AccessSchedule(DynamicDayOfWeek.Everyday, 0, 0, fixture.User.Id));
        var denied = NewContext("GET", "bytes=0-9");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = denied };
        await fixture.Controller.Media(fixture.Session.Token);
        Assert.Equal(404, denied.Response.StatusCode);
        Assert.Empty(((MemoryStream)denied.Response.Body).ToArray());
    }


    [Fact]
    public async Task ProgressiveRangeAndHeadPreserveRepresentation()
    {
        var content = BinaryFixture();
        using var fixture = CreateFixture(content, "https://upstream.example/private.mp4?secret=token");
        var context = NewContext("GET", "bytes=100-199");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };
        await fixture.Controller.Media(fixture.Session.Token);
        Assert.Equal(206, context.Response.StatusCode);
        Assert.Equal("bytes 100-199/1024", context.Response.Headers.ContentRange);
        Assert.Equal(content[100..200], ((MemoryStream)context.Response.Body).ToArray());
        Assert.False(context.Response.Headers.ContainsKey("Location"));
        Assert.False(context.Response.Headers.ContainsKey("Set-Cookie"));

        context = NewContext("HEAD", "bytes=100-199");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };
        await fixture.Controller.Media(fixture.Session.Token);
        Assert.Equal(206, context.Response.StatusCode);
        Assert.Equal(100, context.Response.ContentLength);
        Assert.Empty(((MemoryStream)context.Response.Body).ToArray());
    }

    [Fact]
    public async Task DocumentResponseIsRejectedBeforeAnyBodyIsRelayed()
    {
        var document = Encoding.UTF8.GetBytes("<!doctype html><html><body>Not a media stream</body></html>");
        using var fixture = CreateFixture(document, "https://upstream.example/stream", "text/html");
        var context = NewContext("GET", "");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };

        await fixture.Controller.Media(fixture.Session.Token);

        Assert.Equal(502, context.Response.StatusCode);
        Assert.Empty(((MemoryStream)context.Response.Body).ToArray());
    }

    [Fact]
    public async Task UnrecognizedMediaTypePreservesBytesWithoutDocumentRenderingAuthority()
    {
        var content = BinaryFixture();
        using var fixture = CreateFixture(content, "https://upstream.example/opaque", "application/x-provider-media");
        var context = NewContext("GET", "bytes=100-199");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };

        await fixture.Controller.Media(fixture.Session.Token);

        Assert.Equal(206, context.Response.StatusCode);
        Assert.Equal(content[100..200], ((MemoryStream)context.Response.Body).ToArray());
        Assert.Equal("application/octet-stream", context.Response.ContentType);
        Assert.Contains("sandbox", context.Response.Headers.ContentSecurityPolicy.ToString().Split(';').Select(value => value.Trim()));
    }

    [Theory]
    [InlineData("opaque")]
    [InlineData("misleading.mp4")]
    [InlineData("misleading.ts")]
    public async Task NonzeroRangeCannotExposeAnUnclassifiedPlaylist(string path)
    {
        var content = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nhttps://secret.example/path/segment.ts?token=private\n#EXT-X-ENDLIST\n");
        using var fixture = CreateFixture(content, "https://upstream.example/" + path, "application/octet-stream");
        var context = NewContext("GET", "bytes=20-90");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };
        await fixture.Controller.Media(fixture.Session.Token);
        var playlist = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/vnd.apple.mpegurl", context.Response.ContentType);
        Assert.StartsWith("#EXTM3U", playlist);
        Assert.DoesNotContain("secret.example", playlist);
        Assert.DoesNotContain("private", playlist);
        Assert.Contains("/Siphon/media/", playlist);
        Assert.Contains("/stream.ts", playlist);
    }

    [Fact]
    public void HeaderRotationCreatesFreshLeaseWithoutBreakingExistingReaders()
    {
        var sessions = new ProxySessionStore(new ConfigurationAccessor(() => new PluginConfiguration { PublicBaseUrl = "https://jellyfin.example" }));
        var first = new ResolvedStream(new string('A', 64), "Film", new Uri("https://upstream.example/media"), new Dictionary<string, string> { ["Authorization"] = "old" }, "film.mp4", null);
        var old = sessions.Create(Item(), first);
        var fresh = sessions.Create(Item(), first with { RequestHeaders = new Dictionary<string, string> { ["Authorization"] = "fresh" } });
        Assert.NotEqual(old.Token, fresh.Token);
        Assert.Equal("old", sessions.Get(old.Token)!.Source.RequestHeaders["Authorization"]);
        Assert.Equal("fresh", sessions.Get(fresh.Token)!.Source.RequestHeaders["Authorization"]);
        sessions.Invalidate();
        Assert.Null(sessions.Get(fresh.Token));
    }

    [Fact]
    public void NestedPlaylistReturnsToOriginalAuthorityWithoutLosingCredentials()
    {
        var sessions = new ProxySessionStore(new ConfigurationAccessor(() => new PluginConfiguration { PublicBaseUrl = "https://jellyfin.example" }));
        var source = new ResolvedStream(new string('A', 64), "Film", new Uri("https://origin.example/master.m3u8"),
            new Dictionary<string, string> { ["Authorization"] = "fixture-credential" }, null, null);
        var root = sessions.Create(Item(), source);
        var playlist = sessions.CreateChild(root, new Uri("https://cdn.example/variant.m3u8"));
        var segment = sessions.CreateChild(playlist, new Uri("https://cdn.example/segment.ts"));
        var key = sessions.CreateChild(playlist, new Uri("https://origin.example/decryption.key"));

        Assert.Empty(playlist.Source.RequestHeaders);
        Assert.Empty(segment.Source.RequestHeaders);
        Assert.Equal("fixture-credential", key.Source.RequestHeaders["Authorization"]);
        Assert.Equal(key.Token, sessions.CreateChild(root, key.Source.Url).Token);
    }

    [Fact]
    public void SourceCapabilitiesCannotBeForgedFromAnItemCapability()
    {
        var capabilities = new CapabilityTokenService(new SiphonSecretStore(Paths()));
        var item = capabilities.SignItem("movie:tt1234567");
        Assert.False(capabilities.TryReadSource(item, out _, out _));
        var source = capabilities.SignSource("movie:tt1234567", new string('A', 64));
        Assert.True(capabilities.TryReadSource(source, out var key, out var id));
        Assert.Equal("movie:tt1234567", key);
        Assert.Equal(new string('A', 64), id);
        Assert.False(capabilities.TryReadSource(source[..^4] + "abcd", out _, out _));
    }

    [Theory]
    [InlineData("s")]
    [InlineData("source")]
    public void SignedPlaybackIdentityResolvesWhenBasePathContainsThePluginName(string resource)
    {
        using var fixture = CreateFixture([], "https://upstream.example/film.mp4");
        var tokens = new CapabilityTokenService(new SiphonSecretStore(Paths()));
        var selected = new string('A', 64);
        var token = resource == "s" ? tokens.SignItem(Item().Key) : tokens.SignSource(Item().Key, selected);
        var locator = new SiphonItemLocator(fixture.Store, tokens);
        var url = "https://jellyfin.example/Siphon/proxy/Siphon/" + resource + "/" + token;

        Assert.Equal(Item().Key, locator.Find(url, out var sourceId)?.Key);
        Assert.Equal(resource == "s" ? null : selected, sourceId);
        Assert.Null(locator.Find(url + "/unrouted-suffix", out _));
    }

    [Fact]
    public async Task StalledImageBodyTimesOutWithoutWaitingForTheClientToDisconnect()
    {
        using var fixture = CreateFixture([], "https://upstream.example/poster.png", "image/png", new StalledImage(), 1);
        await fixture.Store.SaveAsync([Item() with { PosterUrl = "https://upstream.example/poster.png" }], CancellationToken.None);
        var token = new CapabilityTokenService(new SiphonSecretStore(Paths())).SignItem(Item().Key);
        var context = NewContext("GET", "");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };
        await fixture.Controller.Image(token).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(502, context.Response.StatusCode);
        Assert.Empty(((MemoryStream)context.Response.Body).ToArray());
    }

    [Fact]
    public async Task ParentArtworkCapabilitySurvivesSeriesShellExpansion()
    {
        var bytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        using var fixture = CreateFixture(bytes, "https://upstream.example/series.png", "image/png");
        var episode = Item() with
        {
            Key = "series:tt1234567:1:2",
            ContentKey = "series:tt1234567",
            Type = "series",
            Season = 1,
            Episode = 2,
            PosterUrl = "https://upstream.example/series.png"
        };
        await fixture.Store.SaveAsync([episode], CancellationToken.None);
        var token = new CapabilityTokenService(new SiphonSecretStore(Paths())).SignItem(episode.ContentKey);
        var context = NewContext("GET", "");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };

        await fixture.Controller.Image(token);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(bytes, ((MemoryStream)context.Response.Body).ToArray());
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public async Task PrimaryArtworkUsesTheCapabilityIdentityRatherThanTheSeriesStorageRow(
        bool shell, bool parentCapability, bool episodeThumbnail)
    {
        var bytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        using var fixture = CreateFixture(bytes, "https://upstream.example/artwork.png", "image/png");
        var item = Item() with
        {
            Key = shell ? "series:tt1234567" : "series:tt1234567:1:2",
            ContentKey = "series:tt1234567",
            Type = "series",
            Season = shell ? null : 1,
            Episode = shell ? null : 2,
            IsSearchPreview = shell,
            PosterUrl = episodeThumbnail ? null : "https://upstream.example/poster.png",
            ThumbnailUrl = episodeThumbnail ? "https://upstream.example/episode.png" : null
        };
        await fixture.Store.SaveAsync([item], CancellationToken.None);
        var token = new CapabilityTokenService(new SiphonSecretStore(Paths())).SignItem(
            parentCapability ? item.ContentKey : item.Key);
        var context = NewContext("GET", "");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };

        await fixture.Controller.Image(token, "primary");

        if (parentCapability || episodeThumbnail)
        {
            Assert.Equal(200, context.Response.StatusCode);
            Assert.Equal(bytes, ((MemoryStream)context.Response.Body).ToArray());
        }
        else
        {
            // An episode without a still must not inherit its parent's poster.
            Assert.Equal(404, context.Response.StatusCode);
            Assert.Empty(((MemoryStream)context.Response.Body).ToArray());
        }
    }

    [Theory]
    [InlineData("video/mp4", 0)]
    [InlineData("video/mp4", 512)]
    [InlineData("application/vnd.apple.mpegurl", 512)]
    public async Task StalledPlaybackBodyTimesOutWithoutWaitingForTheClientToDisconnect(string type, int prefixLength)
    {
        var prefix = Encoding.UTF8.GetBytes("#EXTM3U\n#" + new string('x', 502) + "\n")[..prefixLength];
        if (type == "video/mp4") Array.Clear(prefix);
        using var fixture = CreateFixture([], "https://upstream.example/opaque", type, new StalledPlayback(prefix), 1);
        var context = NewContext("GET", "");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };

        await fixture.Controller.Media(fixture.Session.Token).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(502, context.Response.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.PartialContent)]
    public async Task FailedRangeClassificationDoesNotForwardPotentialPlaylistSecrets(HttpStatusCode status)
    {
        var content = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nhttps://origin.example/segment.ts?credential=fixture\n");
        using var fixture = CreateFixture(content, "https://upstream.example/opaque", "application/octet-stream",
            classificationStatus: status);
        var context = NewContext("GET", "bytes=20-90");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };

        await fixture.Controller.Media(fixture.Session.Token);

        Assert.Equal(502, context.Response.StatusCode);
        Assert.Empty(((MemoryStream)context.Response.Body).ToArray());
    }

    [Theory]
    [InlineData("GET", "", "<?xml version=\"1.0\"?><MPD><BaseURL>https://owned.example/child</BaseURL></MPD>")]
    [InlineData("GET", "bytes=20-60", "<MPD><BaseURL>https://owned.example/child</BaseURL></MPD>")]
    [InlineData("HEAD", "bytes=0-1", "<MPD><BaseURL>https://owned.example/child</BaseURL></MPD>")]
    [InlineData("GET", "", "ffconcat version 1.0\nfile 'https://owned.example/child'\n")]
    [InlineData("GET", "", "<smil><body><video src=\"https://owned.example/child\"/></body></smil>")]
    public async Task UnsupportedRootManifestsNeverReachNativeReadersDespiteMediaLabels(string method, string range, string document)
    {
        using var fixture = CreateFixture(Encoding.UTF8.GetBytes(document), "https://upstream.example/misleading.mp4", "application/octet-stream");
        var context = NewContext(method, range);
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };
        await fixture.Controller.Media(fixture.Session.Token);
        Assert.Equal(502, context.Response.StatusCode);
        Assert.Empty(((MemoryStream)context.Response.Body).ToArray());
    }

    [Fact]
    public async Task ShortRootRangeUsesByteZeroClassificationWithoutChangingRequestedBytes()
    {
        var content = BinaryFixture();
        using var fixture = CreateFixture(content, "https://upstream.example/opaque");
        var context = NewContext("GET", "bytes=0-1");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };
        await fixture.Controller.Media(fixture.Session.Token);
        Assert.Equal(206, context.Response.StatusCode);
        Assert.Equal(content[..2], ((MemoryStream)context.Response.Body).ToArray());
        Assert.Equal("bytes 0-1/1024", context.Response.Headers.ContentRange);
    }

    [Fact]
    public async Task HlsChildKeysAndEncryptedBytesRemainOpaque()
    {
        var content = new byte[16];
        using var fixture = CreateFixture(content, "https://upstream.example/key", "application/octet-stream", child: true);
        var context = NewContext("GET", "");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };
        await fixture.Controller.Media(fixture.Session.Token);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(content, ((MemoryStream)context.Response.Body).ToArray());
    }

    [Theory]
    [MemberData(nameof(SupportedContainerPrefixes))]
    public async Task SupportedBinaryRootsDoNotDependOnDeclaredMime(byte[] content)
    {
        using var fixture = CreateFixture(content, "https://upstream.example/opaque", "application/octet-stream");
        var context = NewContext("GET", "");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };
        await fixture.Controller.Media(fixture.Session.Token);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(content, ((MemoryStream)context.Response.Body).ToArray());
    }

    public static IEnumerable<object[]> SupportedContainerPrefixes()
    {
        yield return [BinaryFixture()];
        yield return [new byte[] { 0x1a, 0x45, 0xdf, 0xa3, 0x80 }];
        var transport = new byte[512];
        transport[0] = transport[188] = transport[376] = 0x47;
        yield return [transport];
        var ogg = new byte[27];
        "OggS"u8.CopyTo(ogg);
        yield return [ogg];
        yield return ["fLaC"u8.ToArray()];
        yield return [new byte[] { 0xff, 0xfb, 0x90, 0 }];
        yield return [new byte[] { 0xff, 0xf1, 0x50, 0x80, 0, 0, 0 }];
        yield return ["RIFF0000WAVE"u8.ToArray()];
    }

    private static byte[] BinaryFixture()
    {
        var content = Enumerable.Range(0, 1024).Select(n => (byte)n).ToArray();
        new byte[] { 0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6f, 0x6d }.CopyTo(content, 0);
        return content;
    }

    private Fixture CreateFixture(byte[] content, string url, string type = "video/mp4", Stream? body = null, int timeoutSeconds = 30, HttpStatusCode? classificationStatus = null, bool child = false)
    {
        var config = new ConfigurationAccessor(() => new PluginConfiguration { PublicBaseUrl = "https://jellyfin.example", AddonTimeoutSeconds = timeoutSeconds });
        var tokens = new CapabilityTokenService(new SiphonSecretStore(Paths()));
        var store = new SiphonStateStore(Paths());
        store.SaveAsync([Item()], CancellationToken.None).GetAwaiter().GetResult();
        var http = new MemoryOrigin(content, type, body, classificationStatus);
        var client = new StremioClient(http, config);
        var registry = new AddonRegistry(client, config, NullLogger<AddonRegistry>.Instance);
        var resolver = new StreamResolver(client, registry, config, NullLogger<StreamResolver>.Instance, new SourceBindingStore(Paths()));
        var sessions = new ProxySessionStore(config);
        var user = new User("viewer", "authentication", "password-reset") { Id = Guid.NewGuid() };
        user.SetPermission(PermissionKind.EnableMediaPlayback, true);
        var source = new ResolvedStream(new string('A', 64), "Film", new Uri(url), new Dictionary<string, string>(), null, null) { UserId = user.Id };
        var session = sessions.Create(Item(), source);
        if (child) session = sessions.CreateChild(session, new Uri("https://upstream.example/child"));
        var video = new Movie { Id = Guid.NewGuid() };
        video.SetProviderId("Siphon", Item().Key);
        video.SetProviderId(NativeVersionService.SourceProvider, source.Id);
        video.SetProviderId(NativeVersionService.OwnerProvider, user.Id.ToString("N"));
        var library = DispatchProxy.Create<ILibraryManager, NativeSourceLifetimeTests.Library>();
        ((NativeSourceLifetimeTests.Library)(object)library).Video = video;
        var access = NativeSourceLifetimeTests.CreateAccess(library, user, authenticated: false);
        return new Fixture(new SiphonProxyController(tokens, store, resolver, sessions, http, config, access, null!), session, store, client, user);
    }

    private ManagedItem Item() => new()
    {
        Key = "movie:tt1234567",
        Type = "movie",
        ContentId = "tt1234567",
        ContentKey = "movie:tt1234567",
        VideoId = "tt1234567",
        Name = "Film",
        Path = Path.Combine(_directory, "Film.strm")
    };

    private SiphonPaths Paths()
    {
        var paths = DispatchProxy.Create<IApplicationPaths, PathsProxy>();
        ((PathsProxy)(object)paths).DataPath = _directory;
        return new SiphonPaths(paths);
    }

    private static DefaultHttpContext NewContext(string method, string range)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Headers.Range = range;
        context.Response.Body = new MemoryStream();
        return context;
    }

    public class PathsProxy : DispatchProxy
    {
        public string DataPath { get; set; } = "";
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == "get_DataPath" ? DataPath : throw new NotSupportedException();
    }

    private sealed class MemoryOrigin(byte[] data, string contentType, Stream? body, HttpStatusCode? classificationStatus) : ISafeHttpClient
    {
        public Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            if (classificationStatus.HasValue && headers?.GetValueOrDefault("Range") == "bytes=0-511")
                return Task.FromResult(new HttpResponseMessage(classificationStatus.Value) { Content = new ByteArrayContent([]) });
            var start = 0;
            var end = data.Length - 1;
            var ranged = headers?.TryGetValue("Range", out var range) == true;
            if (ranged)
            {
                var parsed = RangeHeaderValue.Parse(headers!["Range"]).Ranges.Single();
                start = (int)(parsed.From ?? 0);
                end = (int)Math.Min(parsed.To ?? end, end);
            }

            var response = new HttpResponseMessage(ranged ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(method, uri),
                Content = body is null ? new ByteArrayContent(data[start..(end + 1)]) : new StreamContent(body)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            response.Headers.AcceptRanges.Add("bytes");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "secret=value");
            if (ranged) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, data.Length);
            return Task.FromResult(response);
        }
    }

    private sealed class StalledImage : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class StalledPlayback(byte[] prefix) : MemoryStream(prefix, writable: false)
    {
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed record Fixture(SiphonProxyController Controller, ProxySession Session, SiphonStateStore Store, StremioClient Client, User User) : IDisposable
    {
        public void Dispose() { Store.Dispose(); Client.Dispose(); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
