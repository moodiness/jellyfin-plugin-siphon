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
using Microsoft.Extensions.Logging;
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
        var childReference = playlist.Split('\n').Single(line => line.Length > 0 && !line.StartsWith('#'));
        var localChild = new Uri(new Uri("http://127.0.0.1:8096/base/Siphon/media/parent/stream.ts"), childReference);
        var publicChild = new Uri(new Uri("https://jellyfin.example/external/base/Siphon/media/parent/stream.ts"), childReference);
        Assert.Equal("127.0.0.1", localChild.Host);
        Assert.Equal("jellyfin.example", publicChild.Host);
        Assert.StartsWith("/base/Siphon/media/", localChild.AbsolutePath);
        Assert.StartsWith("/external/base/Siphon/media/", publicChild.AbsolutePath);
        Assert.EndsWith("/stream.ts", publicChild.AbsolutePath);
    }

    [Fact]
    public async Task SignedSourcePlaylistKeepsKeysAndSegmentsOnTheReadersOriginAndBasePath()
    {
        var content = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"https://secret.example/key?token=private\"\n#EXTINF:6,\nhttps://secret.example/segment.ts?token=private\n#EXT-X-ENDLIST\n");
        using var fixture = CreateFixture(content, "https://upstream.example/master.m3u8", "application/vnd.apple.mpegurl");
        fixture.Sessions.SetDefault(fixture.Session, fixture.User.Id.ToString("N") + "|" + Item().Key + "|" + fixture.Session.Source.Id);
        var token = new CapabilityTokenService(new SiphonSecretStore(Paths())).SignSource(Item().Key, fixture.Session.Source.Id);
        var context = NewContext("GET", "");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };

        await fixture.Controller.Source(token);

        Assert.Equal(200, context.Response.StatusCode);
        var playlist = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.DoesNotContain("secret.example", playlist);
        Assert.DoesNotContain("private", playlist);
        var lines = playlist.Split('\n');
        var keyReference = lines.Single(line => line.StartsWith("#EXT-X-KEY", StringComparison.Ordinal)).Split('"')[1];
        var segmentReference = lines.Single(line => line.Length > 0 && !line.StartsWith('#'));
        foreach (var prefix in new[] { "http://127.0.0.1:8096/jellyfin", "https://jellyfin.example/proxy/jellyfin" })
        {
            var parent = new Uri(prefix + "/Siphon/source/" + token);
            foreach (var reference in new[] { keyReference, segmentReference })
            {
                var child = new Uri(parent, reference);
                Assert.Equal(parent.Authority, child.Authority);
                Assert.StartsWith(prefix + "/Siphon/media/", child.AbsoluteUri);
                var opaque = child.Segments[^2].TrimEnd('/');
                Assert.NotNull(fixture.Sessions.Get(opaque));
            }
        }
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

    [Theory]
    [InlineData("bytes=0-", 0)]
    [InlineData("bytes=700-", 700)]
    public async Task OpenEndedPlaybackDoesNotNeedTwoSimultaneousOriginResponses(string range, int offset)
    {
        var content = BinaryFixture();
        var origin = new SingleResponseOrigin(content);
        using var fixture = CreateFixture(content, "https://upstream.example/movie.mp4", transport: origin);
        var context = NewContext("GET", range);
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };

        await fixture.Controller.Media(fixture.Session.Token);

        Assert.Equal(206, context.Response.StatusCode);
        Assert.Equal(content[offset..], ((MemoryStream)context.Response.Body).ToArray());
        Assert.Equal($"bytes {offset}-{content.Length - 1}/{content.Length}", context.Response.Headers.ContentRange);
    }


    [Fact]
    public async Task SuccessfulNativeMediaClassificationIsReusedForLaterLargeSeek()
    {
        var content = BinaryFixture();
        var ranges = new List<string?>();
        var origin = new ResponseOrigin(headers =>
        {
            var range = headers?.GetValueOrDefault("Range");
            ranges.Add(range);
            var start = 0;
            var end = content.Length - 1;
            if (range is not null)
            {
                var parsed = RangeHeaderValue.Parse(range).Ranges.Single();
                start = (int)(parsed.From ?? 0);
                end = (int)Math.Min(parsed.To ?? end, end);
            }

            var response = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(content[start..(end + 1)])
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            response.Content.Headers.ContentLength = end - start + 1;
            response.Headers.AcceptRanges.Add("bytes");
            if (range is not null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, content.Length);
            return response;
        });
        using var fixture = CreateFixture(content, "https://upstream.example/movie.mp4", transport: origin);

        var opening = NewContext("GET", "");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = opening };
        await fixture.Controller.Media(fixture.Session.Token);
        Assert.Equal(200, opening.Response.StatusCode);

        var seek = NewContext("GET", "bytes=700-");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = seek };
        await fixture.Controller.Media(fixture.Session.Token);

        Assert.Equal(206, seek.Response.StatusCode);
        Assert.Equal(content[700..], ((MemoryStream)seek.Response.Body).ToArray());
        Assert.Equal([null, "bytes=700-"], ranges);
    }

    [Fact]
    public async Task ReusedNativeClassificationStillRejectsChangedRepresentation()
    {
        var content = BinaryFixture();
        var changed = false;
        var origin = new ResponseOrigin(headers =>
        {
            var range = headers?.GetValueOrDefault("Range");
            var bytes = changed ? content.Select(value => (byte)(value ^ 0xff)).ToArray() : content;
            var start = 0;
            var end = bytes.Length - 1;
            if (range is not null)
            {
                var parsed = RangeHeaderValue.Parse(range).Ranges.Single();
                start = (int)(parsed.From ?? 0);
                end = (int)Math.Min(parsed.To ?? end, end);
            }

            var response = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(bytes[start..(end + 1)])
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            response.Content.Headers.ContentLength = end - start + 1;
            response.Headers.ETag = new EntityTagHeaderValue(changed ? "\"changed\"" : "\"original\"");
            if (range is not null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, bytes.Length);
            return response;
        });
        using var fixture = CreateFixture(content, "https://upstream.example/movie.mp4", transport: origin);

        var opening = NewContext("GET", "");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = opening };
        await fixture.Controller.Media(fixture.Session.Token);
        Assert.Equal(200, opening.Response.StatusCode);

        changed = true;
        var seek = NewContext("GET", "bytes=700-");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = seek };
        await fixture.Controller.Media(fixture.Session.Token);

        Assert.Equal(502, seek.Response.StatusCode);
        Assert.Empty(((MemoryStream)seek.Response.Body).ToArray());
    }
    [Theory]
    [InlineData("bytes=0-")]
    [InlineData("bytes=20-90")]
    public async Task SingleResponseOriginPlaylistsAreStillRewritten(string range)
    {
        var bytes = Encoding.UTF8.GetBytes("#EXTM3U\n#EXTINF:6,\nhttps://secret.example/segment.ts?credential=private\n#EXT-X-ENDLIST\n");
        using var fixture = CreateFixture(bytes, "https://upstream.example/opaque", transport: new SingleResponseOrigin(bytes));
        var context = NewContext("GET", range);
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };
        await fixture.Controller.Media(fixture.Session.Token);
        Assert.Equal(200, context.Response.StatusCode);
        var playlist = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.StartsWith("#EXTM3U", playlist);
        Assert.DoesNotContain("secret.example", playlist);
        Assert.DoesNotContain("credential", playlist);
        Assert.Contains("../", playlist);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeekRejectsChangedRepresentationButAcceptsEqualStrongValidators(bool changed)
    {
        var bytes = BinaryFixture();
        var origin = new ResponseOrigin(headers =>
        {
            var classify = headers!["Range"] == "bytes=0-511";
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(classify ? bytes[..512] : bytes[700..])
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(classify ? 0 : 700, classify ? 511 : 1023, 1024);
            response.Headers.ETag = new EntityTagHeaderValue(changed && !classify ? "\"new\"" : "\"original\"");
            return response;
        });
        using var fixture = CreateFixture(bytes, "https://upstream.example/opaque", transport: origin);
        var context = NewContext("GET", "bytes=700-");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };
        await fixture.Controller.Media(fixture.Session.Token);
        Assert.Equal(changed ? 502 : 206, context.Response.StatusCode);
        Assert.Equal(changed ? [] : bytes[700..], ((MemoryStream)context.Response.Body).ToArray());
    }

    [Fact]
    public async Task RangeIgnoredAfterClassificationValidatesTheNewWholeRepresentation()
    {
        var bytes = BinaryFixture();
        var origin = new ResponseOrigin(headers =>
        {
            var classify = headers!["Range"] == "bytes=0-511";
            var response = new HttpResponseMessage(classify ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(classify ? bytes[..512] : "<MPD><BaseURL>https://secret.example/</BaseURL></MPD>"u8.ToArray())
            };
            if (classify) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 511, 1024);
            return response;
        });
        using var fixture = CreateFixture(bytes, "https://upstream.example/opaque", transport: origin);
        var context = NewContext("GET", "bytes=700-");
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };
        await fixture.Controller.Media(fixture.Session.Token);
        Assert.Equal(502, context.Response.StatusCode);
        Assert.Empty(((MemoryStream)context.Response.Body).ToArray());
    }

    [Theory]
    [InlineData("bytes=700-", "classification-headers")]
    [InlineData("bytes=0-", "media-headers")]
    public async Task RelayFailureDiagnosticsNeverIncludeExceptionsOrCapabilities(string range, string stage)
    {
        const string secret = "https://secret.example/path?credential=private";
        var warnings = new Warnings();
        var origin = new ResponseOrigin(_ => throw new HttpRequestException(secret));
        using var fixture = CreateFixture([], secret, transport: origin, logger: warnings);
        var context = NewContext("GET", range);
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };
        await fixture.Controller.Media(fixture.Session.Token);
        Assert.Equal(502, context.Response.StatusCode);
        var message = Assert.Single(warnings.Messages);
        Assert.Contains("stage=" + stage, message);
        Assert.Contains("cause=transport", message);
        Assert.DoesNotContain("secret.example", message);
        Assert.DoesNotContain("private", message);
        Assert.DoesNotContain(fixture.Session.Token, message);
        Assert.Empty(warnings.Exceptions);
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

    private Fixture CreateFixture(byte[] content, string url, string type = "video/mp4", Stream? body = null, int timeoutSeconds = 30, HttpStatusCode? classificationStatus = null, bool child = false, ISafeHttpClient? transport = null, ILogger<SiphonProxyController>? logger = null)
    {
        var config = new ConfigurationAccessor(() => new PluginConfiguration { PublicBaseUrl = "https://jellyfin.example", AddonTimeoutSeconds = timeoutSeconds });
        var tokens = new CapabilityTokenService(new SiphonSecretStore(Paths()));
        var store = new SiphonStateStore(Paths());
        store.SaveAsync([Item()], CancellationToken.None).GetAwaiter().GetResult();
        var http = transport ?? new MemoryOrigin(content, type, body, classificationStatus);
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
        return new Fixture(new SiphonProxyController(tokens, store, resolver, sessions, http, config, access, null!, logger ?? NullLogger<SiphonProxyController>.Instance), session, store, client, user, sessions);
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

    private sealed class SingleResponseOrigin(byte[] data) : ISafeHttpClient
    {
        private bool _active;

        public Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            if (_active) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var range = headers?.TryGetValue("Range", out var value) == true ? RangeHeaderValue.Parse(value).Ranges.Single() : null;
            var start = (int)(range?.From ?? 0);
            var end = (int)Math.Min(range?.To ?? data.Length - 1, data.Length - 1);
            _active = true;
            var content = new StreamContent(new ResponseStream(data[start..(end + 1)], () => _active = false));
            content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            content.Headers.ContentLength = end - start + 1;
            if (range is not null) content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, data.Length);
            return Task.FromResult(new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                RequestMessage = new HttpRequestMessage(method, uri),
                Content = content
            });
        }

        private sealed class ResponseStream(byte[] bytes, Action release) : MemoryStream(bytes, writable: false)
        {
            protected override void Dispose(bool disposing)
            {
                if (disposing) release();
                base.Dispose(disposing);
            }
        }
    }

    private sealed class ResponseOrigin(Func<IReadOnlyDictionary<string, string>?, HttpResponseMessage> respond) : ISafeHttpClient
    {
        public Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
            => Task.FromResult(respond(headers));
    }

    private sealed class Warnings : ILogger<SiphonProxyController>
    {
        public List<string> Messages { get; } = [];
        public List<Exception> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Messages.Add(formatter(state, exception));
            if (exception is not null) Exceptions.Add(exception);
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

    private sealed record Fixture(SiphonProxyController Controller, ProxySession Session, SiphonStateStore Store, StremioClient Client, User User, ProxySessionStore Sessions) : IDisposable
    {
        public void Dispose() { Store.Dispose(); Client.Dispose(); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
