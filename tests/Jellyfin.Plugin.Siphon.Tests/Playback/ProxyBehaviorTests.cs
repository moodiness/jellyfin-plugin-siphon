using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Playback;
using Jellyfin.Plugin.Siphon.Protocol;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Playback;

public sealed class ProxyBehaviorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "siphon-proxy-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProgressiveRangeAndHeadPreserveRepresentation()
    {
        var content = Enumerable.Range(0, 1024).Select(n => (byte)n).ToArray();
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

    private Fixture CreateFixture(byte[] content, string url, string type = "video/mp4", Stream? body = null, int timeoutSeconds = 30, HttpStatusCode? classificationStatus = null)
    {
        var config = new ConfigurationAccessor(() => new PluginConfiguration { PublicBaseUrl = "https://jellyfin.example", AddonTimeoutSeconds = timeoutSeconds });
        var tokens = new CapabilityTokenService(new SiphonSecretStore(Paths()));
        var store = new SiphonStateStore(Paths());
        store.SaveAsync([Item()], CancellationToken.None).GetAwaiter().GetResult();
        var http = new MemoryOrigin(content, type, body, classificationStatus);
        var client = new StremioClient(http, config);
        var registry = new AddonRegistry(client, config, NullLogger<AddonRegistry>.Instance);
        var resolver = new StreamResolver(client, registry, config, NullLogger<StreamResolver>.Instance);
        var sessions = new ProxySessionStore(config);
        var session = sessions.Create(Item(), new ResolvedStream(new string('A', 64), "Film", new Uri(url), new Dictionary<string, string>(), null, null));
        return new Fixture(new SiphonProxyController(tokens, store, resolver, sessions, http, config), session, store, client);
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

    private sealed record Fixture(SiphonProxyController Controller, ProxySession Session, SiphonStateStore Store, StremioClient Client) : IDisposable
    {
        public void Dispose() { Store.Dispose(); Client.Dispose(); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
