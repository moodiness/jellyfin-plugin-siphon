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

    [Fact]
    public async Task NonzeroRangeCannotExposeAnExtensionlessPlaylist()
    {
        var content = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nhttps://secret.example/path/segment.ts?token=private\n#EXT-X-ENDLIST\n");
        using var fixture = CreateFixture(content, "https://upstream.example/opaque", "application/octet-stream");
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

    private Fixture CreateFixture(byte[] content, string url, string type = "video/mp4")
    {
        var config = new ConfigurationAccessor(() => new PluginConfiguration { PublicBaseUrl = "https://jellyfin.example" });
        var tokens = new CapabilityTokenService(new SiphonSecretStore(Paths()));
        var store = new SiphonStateStore(Paths());
        store.SaveAsync([Item()], CancellationToken.None).GetAwaiter().GetResult();
        var http = new MemoryOrigin(content, type);
        var client = new StremioClient(http, config);
        var registry = new AddonRegistry(client, config, NullLogger<AddonRegistry>.Instance);
        var resolver = new StreamResolver(client, registry, config, NullLogger<StreamResolver>.Instance);
        var sessions = new ProxySessionStore(config);
        var session = sessions.Create(Item(), new ResolvedStream(new string('A', 64), "Film", new Uri(url), new Dictionary<string, string>(), null, null));
        return new Fixture(new SiphonProxyController(tokens, store, resolver, sessions, http), session, store, client);
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

    private sealed class MemoryOrigin(byte[] data, string contentType) : ISafeHttpClient
    {
        public Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
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
                Content = new ByteArrayContent(data[start..(end + 1)])
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            response.Headers.AcceptRanges.Add("bytes");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "secret=value");
            if (ranged) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, data.Length);
            return Task.FromResult(response);
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
