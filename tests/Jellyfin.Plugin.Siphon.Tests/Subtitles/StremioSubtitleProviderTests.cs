using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Playback;
using Jellyfin.Plugin.Siphon.Protocol;
using Jellyfin.Plugin.Siphon.Subtitles;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Subtitles;

public sealed class StremioSubtitleProviderTests
{
    private const string Srt = "1\n00:00:01,000 --> 00:00:02,000\nA caption\n";

    [Fact]
    public async Task SubtitleOnlyAddonSearchAndDownloadKeepOriginalMovieIdentityAndHideSecrets()
    {
        using var fixture = new Fixture();
        fixture.Item = fixture.Item with { StreamIdentities = [new("movie", "custom:original/id?part=1")] };
        fixture.Addon.DisplayName = "https://origin.example/installation-secret";
        fixture.Http.Subtitles = """{"subtitles":[{"id":"origin-secret-id","lang":"eng","url":"https://captions.example/subtitle.srt?token=download-secret"}]}""";

        var result = Assert.Single(await fixture.Search());
        var subtitleRequest = Assert.Single(fixture.Http.Requests, uri => uri.AbsolutePath.Contains("/subtitles/", StringComparison.Ordinal));
        Assert.Contains("/subtitles/movie/custom%3Aoriginal%2Fid%3Fpart%3D1.json", subtitleRequest.OriginalString);
        Assert.DoesNotContain(fixture.Http.Requests, uri => uri.Host == "captions.example");
        Assert.Matches("^[A-F0-9]{64}$", result.Id);
        Assert.Null(result.IsHashMatch);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("secret", serialized);
        Assert.DoesNotContain("https://", serialized);

        var downloaded = await fixture.Provider.GetSubtitles(result.Id, CancellationToken.None);
        Assert.Equal("srt", downloaded.Format);
        Assert.Equal("eng", downloaded.Language);
        using var reader = new StreamReader(downloaded.Stream);
        Assert.Equal(Srt, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task EpisodeUsesPersistedAnimeResourceTypeAndVideoIdRatherThanJellyfinNumbers()
    {
        using var fixture = new Fixture();
        fixture.Item = fixture.Item with
        {
            Type = "series",
            VideoId = "different-library-id",
            Season = 3,
            Episode = 27,
            StreamIdentities = [new("anime", "mal:123:episode:4")]
        };
        fixture.Http.Manifest = """{"id":"captions","resources":[{"name":"subtitles","types":["anime"],"idPrefixes":["mal:"]}],"types":["movie"]}""";
        fixture.Http.Subtitles = """{"subtitles":[{"id":"caption","lang":"en","url":"https://captions.example/file"}]}""";
        fixture.Http.DownloadType = "text/vtt";
        fixture.Http.Download = Encoding.UTF8.GetBytes("WEBVTT\n\n00:01.000 --> 00:02.000\nA caption\n");

        var result = Assert.Single(await fixture.Search());
        Assert.Contains(fixture.Http.Requests, uri => uri.OriginalString.Contains("/subtitles/anime/mal%3A123%3Aepisode%3A4.json", StringComparison.Ordinal));
        var response = await fixture.Provider.GetSubtitles(result.Id, CancellationToken.None);
        Assert.Equal("vtt", response.Format);
        using var reader = new StreamReader(response.Stream);
        Assert.StartsWith("WEBVTT", await reader.ReadToEndAsync());
    }

    [Theory]
    [InlineData("en", "eng")]
    [InlineData("fre", "fra")]
    [InlineData("FR", "fra")]
    public async Task LanguagesRespectJellyfinIsoAliasesWithoutReturningOtherLanguages(string requested, string expected)
    {
        using var fixture = new Fixture();
        fixture.Http.Subtitles = """{"subtitles":[{"id":"english","lang":"eng","url":"https://captions.example/en.srt"},{"id":"french","lang":"fre","url":"https://captions.example/fr.srt"},{"id":"unsafe","lang":"../../eng","url":"https://captions.example/bad.srt"}]}""";
        var result = Assert.Single(await fixture.Search(requested));
        Assert.Equal(expected, result.ThreeLetterISOLanguageName);
        var response = await fixture.Provider.GetSubtitles(result.Id, CancellationToken.None);
        Assert.Equal(expected, response.Language);
        response.Stream.Dispose();
    }

    [Fact]
    public async Task UnmanagedPathsNeverFallbackToMatchingProviderIdsOrTitles()
    {
        using var fixture = new Fixture();
        var request = fixture.Request();
        request.MediaPath = "/unrelated/movie.strm";
        request.ProviderIds["Imdb"] = "tt1234567";
        Assert.Empty(await fixture.Provider.Search(request, CancellationToken.None));
        Assert.Empty(fixture.Http.Requests);
    }

    [Fact]
    public async Task AutomatedPerfectMatchAndUnknownLanguageRequestsDoNotFetch()
    {
        using var fixture = new Fixture();
        var request = fixture.Request();
        request.IsAutomated = true;
        Assert.Empty(await fixture.Provider.Search(request, CancellationToken.None));
        request.IsAutomated = false;
        request.IsPerfectMatch = true;
        Assert.Empty(await fixture.Provider.Search(request, CancellationToken.None));
        Assert.Empty(await fixture.Search("../../eng"));
        Assert.Empty(fixture.Http.Requests);
    }

    [Fact]
    public async Task MissingResourceIdentityOrManifestSupportNeverMakesGenericSubtitleRequests()
    {
        using var fixture = new Fixture();
        var original = fixture.Item;
        fixture.Item = original with { StreamIdentities = [] };
        Assert.Empty(await fixture.Search());
        Assert.Empty(fixture.Http.Requests);
        fixture.Item = original;
        fixture.Http.Manifest = """{"id":"captions","resources":[{"name":"subtitles","types":["movie"],"idPrefixes":["other:"]}],"types":["movie"]}""";
        Assert.Empty(await fixture.Search());
        Assert.DoesNotContain(fixture.Http.Requests, uri => uri.AbsolutePath.Contains("/subtitles/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StreamMetadataAddsCanonicalExtrasWithoutFetchingVideoBytes()
    {
        using var fixture = new Fixture();
        fixture.Http.Manifest = """{"id":"captions","resources":["subtitles","stream"],"types":["movie"]}""";
        fixture.Http.Streams = """{"streams":[{"url":"https://video.example/movie.mkv","behaviorHints":{"filename":"Film name.mkv","videoSize":123456789}}]}""";
        Assert.Single(await fixture.Search());
        var request = Assert.Single(fixture.Http.Requests, uri => uri.AbsolutePath.Contains("/subtitles/", StringComparison.Ordinal));
        Assert.Contains("/filename=Film%20name.mkv&videoSize=123456789.json", request.OriginalString);
        Assert.DoesNotContain("videoHash", request.OriginalString);
        Assert.DoesNotContain(fixture.Http.Requests, uri => uri.Host == "video.example");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"subtitles\":{\"url\":\"https://captions.example/file.srt\"}}")]
    [InlineData("{\"subtitles\":[null,1,{\"id\":3,\"url\":\"https://captions.example/file.srt\",\"lang\":\"eng\"},{\"id\":\"x\",\"url\":\"file:///tmp/file.srt\",\"lang\":\"eng\"}]}")]
    public async Task MalformedAndUnsafeSubtitleResponsesDoNotProduceCandidates(string body)
    {
        using var fixture = new Fixture();
        fixture.Http.Subtitles = body;
        Assert.Empty(await fixture.Search());
    }

    [Fact]
    public async Task OpaqueTicketsExpireAndEvictOldestDownloadsAtCapacity()
    {
        using var fixture = new Fixture(capacity: 2);
        var first = Assert.Single(await fixture.Search());
        var second = Assert.Single(await fixture.Search());
        var third = Assert.Single(await fixture.Search());
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(second.Id, third.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Provider.GetSubtitles(first.Id, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Provider.GetSubtitles("https://captions.example/file.srt", CancellationToken.None));
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Provider.GetSubtitles(third.Id, CancellationToken.None));
        Assert.DoesNotContain(fixture.Http.Requests, uri => uri.Host == "captions.example");
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("uninstalled")]
    [InlineData("reconfigured")]
    [InlineData("removed-item")]
    public async Task DownloadRevocationIsCheckedBeforeNetworkAccess(string change)
    {
        using var fixture = new Fixture();
        var result = Assert.Single(await fixture.Search());
        switch (change)
        {
            case "disabled": fixture.Addon.Enabled = false; break;
            case "uninstalled": fixture.Configuration.Addons = []; break;
            case "reconfigured": fixture.Addon.ManifestUrl = "https://addon.example/new-token/manifest.json"; break;
            case "removed-item": fixture.State.Item = null; break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Provider.GetSubtitles(result.Id, CancellationToken.None));
        Assert.DoesNotContain(fixture.Http.Requests, uri => uri.Host == "captions.example");
    }

    [Fact]
    public async Task DownloadRechecksRevocationAfterFetchingBytes()
    {
        using var fixture = new Fixture();
        var result = Assert.Single(await fixture.Search());
        fixture.Http.BeforeDownload = () => fixture.Addon.Enabled = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Provider.GetSubtitles(result.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedJsonAndSubtitlesAreRejectedWithOrWithoutContentLength(bool unknownLength)
    {
        using var fixture = new Fixture();
        fixture.Http.UnknownLength = unknownLength;
        fixture.Http.Subtitles = new string(' ', 2 * 1024 * 1024 + 1);
        Assert.Empty(await fixture.Search());
        fixture.Http.Subtitles = Fixture.DefaultSubtitles;
        var result = Assert.Single(await fixture.Search());
        fixture.Http.Download = Encoding.UTF8.GetBytes(Srt + new string('x', 8 * 1024 * 1024));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Provider.GetSubtitles(result.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData("ass", "[Script Info]\nScriptType: v4.00+\n[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Hello\n")]
    [InlineData("ssa", "[Script Info]\nScriptType: v4.00\n[Events]\nDialogue: Marked=0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Hello\n")]
    public async Task AssAndSsaAreReturnedAsRealText(string format, string text)
    {
        using var fixture = new Fixture();
        fixture.Http.Subtitles = JsonSerializer.Serialize(new { subtitles = new[] { new { id = "caption", lang = "eng", url = "https://captions.example/file." + format } } });
        fixture.Http.Download = Encoding.UTF8.GetBytes(text);
        var result = Assert.Single(await fixture.Search());
        var response = await fixture.Provider.GetSubtitles(result.Id, CancellationToken.None);
        Assert.Equal(format, response.Format);
        using var reader = new StreamReader(response.Stream);
        Assert.Equal(text, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task DownloadRejectsHtmlAndDoesNotLeakUpstreamExceptionUris()
    {
        using var fixture = new Fixture();
        var result = Assert.Single(await fixture.Search());
        fixture.Http.Download = Encoding.UTF8.GetBytes("<html>Not a subtitle</html>");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Provider.GetSubtitles(result.Id, CancellationToken.None));
        fixture.Http.BeforeDownload = () => throw new InvalidOperationException("https://captions.example/file?token=private-secret");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Provider.GetSubtitles(result.Id, CancellationToken.None));
        Assert.DoesNotContain("private-secret", error.ToString());
        Assert.Null(error.InnerException);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string DefaultSubtitles = """{"subtitles":[{"id":"caption","lang":"eng","url":"https://captions.example/file.srt"}]}""";
        internal ConfiguredAddon Addon { get; } = new() { Id = "installation", ManifestUrl = "https://addon.example/installation-secret/manifest.json" };
        internal PluginConfiguration Configuration { get; } = new();
        internal MemoryState State { get; } = new();
        internal Origin Http { get; } = new();
        internal Clock Clock { get; } = new();
        internal StremioSubtitleProvider Provider { get; }
        private readonly StremioClient _client;
        internal ManagedItem Item { get => State.Item!; set => State.Item = value; }

        internal Fixture(int capacity = 4096)
        {
            Item = new ManagedItem
            {
                Key = "movie:tt1234567",
                Type = "movie",
                ContentId = "tt1234567",
                ContentKey = "movie:tt1234567",
                VideoId = "tt1234567",
                Name = "Film",
                Path = "/managed/Film.strm",
                StreamIdentities = [new("movie", "tt1234567")]
            };
            Configuration.Addons = [Addon];
            var accessor = new ConfigurationAccessor(() => Configuration);
            _client = new StremioClient(Http, accessor);
            var registry = new AddonRegistry(_client, accessor, NullLogger<AddonRegistry>.Instance);
            var resolver = new StreamResolver(_client, registry, accessor, NullLogger<StreamResolver>.Instance);
            var localization = DispatchProxy.Create<ILocalizationManager, LocalizationProxy>();
            Provider = new StremioSubtitleProvider(State, registry, resolver, Http, accessor, localization, Clock, capacity, TimeSpan.FromMinutes(5));
        }

        internal SubtitleSearchRequest Request(string language = "eng") => new()
        {
            Language = language,
            MediaPath = Item.Path,
            Name = Item.Name,
            ContentType = Item.Type == "movie" ? VideoContentType.Movie : VideoContentType.Episode
        };
        internal Task<IEnumerable<RemoteSubtitleInfo>> Search(string language = "eng") => Provider.Search(Request(language), CancellationToken.None);
        public void Dispose() { Provider.Dispose(); _client.Dispose(); }
    }

    public class LocalizationProxy : DispatchProxy
    {
        private static readonly CultureDto[] Cultures =
        [
            new("English", "English", "en", ["eng"]),
            new("French", "French", "fr", ["fra", "fre"])
        ];
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == "GetCultures" ? Cultures : throw new NotSupportedException();
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan by) => _now += by;
    }

    private sealed class MemoryState : ISiphonStateStore
    {
        internal ManagedItem? Item { get; set; }
        public ManagedItem? FindByPath(string path) => Item?.Path == path ? Item : null;
        public IReadOnlyList<ManagedItem> GetItems() => throw new NotSupportedException();
        public ManagedItem? FindByKey(string key) => throw new NotSupportedException();
        public ManagedItem? FindByContentKey(string key) => throw new NotSupportedException();
        public Task SaveAsync(IReadOnlyList<ManagedItem> items, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Origin : ISafeHttpClient
    {
        internal string Manifest { get; set; } = """{"id":"captions","name":"Captions","resources":["subtitles"],"types":["movie","series"]}""";
        internal string Streams { get; set; } = """{"streams":[]}""";
        internal string Subtitles { get; set; } = Fixture.DefaultSubtitles;
        internal byte[] Download { get; set; } = Encoding.UTF8.GetBytes(Srt);
        internal string DownloadType { get; set; } = "text/plain";
        internal bool UnknownLength { get; set; }
        internal Action? BeforeDownload { get; set; }
        internal ConcurrentQueue<Uri> Requests { get; } = new();

        public Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(uri);
            if (method != HttpMethod.Get) throw new InvalidOperationException("Unexpected probe.");
            byte[] bytes;
            var mediaType = "application/json";
            if (uri.Host == "captions.example")
            {
                BeforeDownload?.Invoke();
                bytes = Download;
                mediaType = DownloadType;
            }
            else if (uri.AbsolutePath.EndsWith("/manifest.json", StringComparison.Ordinal)) bytes = Encoding.UTF8.GetBytes(Manifest);
            else if (uri.AbsolutePath.Contains("/subtitles/", StringComparison.Ordinal)) bytes = Encoding.UTF8.GetBytes(Subtitles);
            else if (uri.AbsolutePath.Contains("/stream/", StringComparison.Ordinal)) bytes = Encoding.UTF8.GetBytes(Streams);
            else throw new InvalidOperationException("Unexpected request.");
            HttpContent content = UnknownLength ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
}
