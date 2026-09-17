using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Playback;
using Jellyfin.Plugin.Siphon.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Playback;

public sealed class StreamResolverTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "siphon-streams-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MirrorIdsSurviveReverseCompletionUrlRotationReorderingDisappearanceAndRestart()
    {
        var configuration = new PluginConfiguration
        {
            MaxConcurrentRequests = 2,
            Addons =
            [
                new() { Id = "first", ManifestUrl = "https://first.example/manifest.json", Enabled = true },
                new() { Id = "second", ManifestUrl = "https://second.example/manifest.json", Enabled = true }
            ]
        };
        var accessor = new ConfigurationAccessor(() => configuration);
        var origin = new OrderedOrigin();
        using var client = new StremioClient(origin, accessor);
        var registry = new AddonRegistry(client, accessor, NullLogger<AddonRegistry>.Instance);
        var path = Path.Combine(_directory, "source-bindings.json");
        var resolver = new StreamResolver(client, registry, accessor, NullLogger<StreamResolver>.Instance, new SourceBindingStore(path));
        var item = new ManagedItem
        {
            Key = "movie:tt1234567",
            Type = "movie",
            ContentId = "tt1234567",
            ContentKey = "movie:tt1234567",
            VideoId = "tt1234567",
            Name = "Film",
            Path = "https://jellyfin.example/managed/film",
            StreamIdentities = [new("movie", "tt1234567")]
        };

        var sources = await resolver.GetSourcesAsync(item, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "/first.mkv", "/mirror.mkv", "/last.mkv" }, sources.Select(source => source.Url.AbsolutePath));
        Assert.NotEqual(sources[0].Id, sources[1].Id);

        origin.Token = "renewed";
        resolver.Invalidate();
        var renewed = await resolver.GetSourcesAsync(item, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(sources.Select(source => source.Id), renewed.Select(source => source.Id));
        Assert.All(renewed, source => Assert.Equal("?token=renewed", source.Url.Query));

        origin.Paths = ["mirror", "first"];
        resolver.Invalidate();
        var reordered = await resolver.GetSourcesAsync(item, CancellationToken.None);
        Assert.Equal(new[] { sources[1].Id, sources[0].Id, sources[2].Id }, reordered.Select(source => source.Id));
        origin.Paths = ["mirror"];
        resolver.Invalidate();
        var disappeared = await resolver.GetSourcesAsync(item, CancellationToken.None);
        Assert.Equal(new[] { sources[1].Id, sources[2].Id }, disappeared.Select(source => source.Id));
        Assert.DoesNotContain(disappeared, source => source.Id == sources[0].Id);

        resolver = new StreamResolver(client, registry, accessor, NullLogger<StreamResolver>.Instance, new SourceBindingStore(path));
        var restarted = await resolver.GetSourcesAsync(item, CancellationToken.None);
        Assert.Equal(disappeared.Select(source => source.Id), restarted.Select(source => source.Id));
        origin.Paths = ["mirror", "first"];
        origin.Token = "after-restart";
        resolver.Invalidate();
        var returned = await resolver.GetSourcesAsync(item, CancellationToken.None);
        Assert.Equal(new[] { sources[1].Id, sources[0].Id, sources[2].Id }, returned.Select(source => source.Id));
        var persisted = File.ReadAllText(path);
        Assert.DoesNotContain("example", persisted);
        Assert.DoesNotContain("after-restart", persisted);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class OrderedOrigin : ISafeHttpClient
    {
        private readonly TaskCompletionSource _secondFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Token { get; set; } = "initial";
        public string[] Paths { get; set; } = ["first", "mirror"];

        public async Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            string body;
            if (uri.AbsolutePath == "/manifest.json")
            {
                body = """{"id":"streams","resources":["stream"],"types":["movie"]}""";
            }
            else if (uri.Host == "first.example")
            {
                await _secondFinished.Task.WaitAsync(cancellationToken);
                body = JsonSerializer.Serialize(new
                {
                    streams = Paths.Select(path => new
                    {
                        name = "Z 4K",
                        url = "https://video.example/" + path + ".mkv?token=" + Token,
                        behaviorHints = new { filename = "Film.mkv", videoSize = 100 }
                    })
                });
            }
            else
            {
                body = $$$"""{"streams":[{"name":"A 1080p","url":"https://video.example/last.mkv?token={{{Token}}}","behaviorHints":{"filename":"Other.mkv","videoSize":50}}]}""";
                _secondFinished.TrySetResult();
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(method, uri),
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
