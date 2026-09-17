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

public sealed class ProfileIsolationTests
{
    [Fact]
    public async Task EmptyOverrideNeverFallsBackAndTitleRefreshDoesNotInvalidateAnotherUser()
    {
        var directory = Path.Combine(Path.GetTempPath(), "siphon-profiles-" + Guid.NewGuid().ToString("N"));
        var firstUser = Guid.NewGuid();
        var secondUser = Guid.NewGuid();
        var emptyUser = Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            Addons = [new() { Id = "provider", ManifestUrl = "https://global.example/manifest.json", Enabled = true }],
            UserProfiles =
            [
                new() { UserId = firstUser, OverrideAddons = true, Addons = [new() { Id = "provider", ManifestUrl = "https://first.example/manifest.json", Enabled = true }] },
                new() { UserId = secondUser, OverrideAddons = true, Addons = [new() { Id = "provider", ManifestUrl = "https://second.example/manifest.json", Enabled = true }] },
                new() { UserId = emptyUser, OverrideAddons = true, Addons = [] }
            ]
        };
        var accessor = new ConfigurationAccessor(() => configuration);
        var origin = new Origin();
        using var client = new StremioClient(origin, accessor);
        var registry = new AddonRegistry(client, accessor, NullLogger<AddonRegistry>.Instance);
        var resolver = new StreamResolver(client, registry, accessor, NullLogger<StreamResolver>.Instance, new SourceBindingStore(Path.Combine(directory, "bindings.json")));
        var item = new ManagedItem { Key = "movie:tt1234567", Type = "movie", ContentId = "tt1234567", ContentKey = "movie:tt1234567", VideoId = "tt1234567", Name = "Film", Path = "https://server.example/item" };
        try
        {
            Assert.Empty(await resolver.GetSourcesAsync(item, emptyUser, CancellationToken.None));
            Assert.Equal(0, origin.Requests);
            var first = Assert.Single(await resolver.GetSourcesAsync(item, firstUser, CancellationToken.None));
            var second = Assert.Single(await resolver.GetSourcesAsync(item, secondUser, CancellationToken.None));
            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal("?account=first.example&token=initial", first.Url.Query);
            Assert.Equal("?account=second.example&token=initial", second.Url.Query);
            origin.Token = "renewed";
            resolver.Invalidate(item.Key, firstUser);
            var renewed = Assert.Single(await resolver.GetSourcesAsync(item, firstUser, CancellationToken.None));
            Assert.Equal(first.Id, renewed.Id);
            Assert.Contains("renewed", renewed.Url.Query);
            var other = Assert.Single(await resolver.GetSourcesAsync(item, secondUser, CancellationToken.None));
            Assert.Equal(second.Url, other.Url);
            var persisted = File.ReadAllText(Path.Combine(directory, "bindings.json"));
            Assert.DoesNotContain("example", persisted);
            Assert.DoesNotContain("renewed", persisted);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ProfileRevocationRemovesItsChildCapabilitiesWithoutInterruptingAnotherUser()
    {
        var sessions = new ProxySessionStore(new ConfigurationAccessor(() => new PluginConfiguration()));
        var item = new ManagedItem { Key = "movie:tt1234567", Type = "movie", ContentId = "tt1234567", ContentKey = "movie:tt1234567", VideoId = "tt1234567", Name = "Film", Path = "https://server.example/item" };
        var firstUser = Guid.NewGuid();
        var first = sessions.Create(item, new ResolvedStream("first", "First", new Uri("https://media.example/one"), new Dictionary<string, string>(), null, null) { UserId = firstUser });
        var child = sessions.CreateChild(first, new Uri("https://media.example/chunk"));
        var second = sessions.Create(item, new ResolvedStream("second", "Second", new Uri("https://media.example/two"), new Dictionary<string, string>(), null, null) { UserId = Guid.NewGuid() });
        sessions.Invalidate(userId => userId == firstUser);
        Assert.Null(sessions.Get(first.Token));
        Assert.Null(sessions.Get(child.Token));
        Assert.NotNull(sessions.Get(second.Token));
    }

    private sealed class Origin : ISafeHttpClient
    {
        public int Requests { get; private set; }
        public string Token { get; set; } = "initial";
        public Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            Requests++;
            var body = uri.AbsolutePath == "/manifest.json" ? """{"id":"provider","resources":["stream"],"types":["movie"]}"""
                : JsonSerializer.Serialize(new { streams = new[] { new { name = "HD", url = "https://video.example/film.mkv?account=" + uri.Host + "&token=" + Token, behaviorHints = new { filename = "film.mkv" } } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = new(method, uri), Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
