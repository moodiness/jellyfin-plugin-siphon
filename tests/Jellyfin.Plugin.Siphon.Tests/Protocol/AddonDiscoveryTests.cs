using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Protocol;

public sealed class AddonDiscoveryTests
{
    private const string Manifest = """{"id":"discovery","types":["movie","series"],"idPrefixes":["tt"],"resources":["addon_catalog"],"addonCatalogs":[{"type":"all","id":"community/雪","name":"Community"}]}""";
    private const string Addons = """{"addons":[{"transportName":"http","transportUrl":"https://chosen.example/a%2Fb/manifest.json?token=x%2By","manifest":{"id":"chosen","name":"Chosen addon","description":"An addon"}}]}""";

    [Fact]
    public async Task DiscoveryCatalogUsesItsOwnIdentityAndPreservesOpaqueInstallationPath()
    {
        var controller = Create(new MemoryOrigin(uri => uri.OriginalString switch
        {
            "https://discovery.example/a%2Fb/manifest.json?token=x%2By" => Json(Manifest),
            "https://discovery.example/a%2Fb/addon_catalog/all/community%2F%E9%9B%AA.json?token=x%2By" => Json(Addons),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        var request = new AddonDiscoveryController.DiscoveryRequest("stremio://discovery.example/a%2Fb/manifest.json?token=x%2By");
        var listing = await controller.Discover(request, CancellationToken.None);
        var catalog = Assert.Single(Assert.IsType<AddonDiscoveryController.DiscoveryResponse>(listing.Value).Catalogs);
        var result = await controller.Discover(request with { Type = catalog.Type, Id = catalog.Id }, CancellationToken.None);
        var addon = Assert.Single(Assert.IsType<AddonDiscoveryController.DiscoveryResponse>(result.Value).Addons);
        Assert.Equal("https://chosen.example/a%2Fb/manifest.json?token=x%2By", addon.TransportUrl);
        Assert.Equal("Chosen addon", addon.Name);
    }

    [Fact]
    public async Task UnadvertisedDiscoveryCatalogCannotBeRequested()
    {
        var controller = Create(new MemoryOrigin(uri => Json(uri.AbsolutePath.EndsWith("manifest.json", StringComparison.Ordinal) ? Manifest : Addons)));
        var result = await controller.Discover(new("https://discovery.example/manifest.json", "movie", "community/雪"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task DiscoverySkipsMalformedTransportsAndCollapsesEquivalentInstallationLinks()
    {
        const string entries = """
            {"addons":[
                null,
                {"transportName":"torrent","transportUrl":"https://torrent.example/manifest.json","manifest":{"id":"torrent","name":"Torrent"}},
                {"transportName":"http","transportUrl":"file:///manifest.json","manifest":{"id":"file","name":"File"}},
                {"transportName":"http","transportUrl":"https://user:pass@bad.example/manifest.json","manifest":{"id":"credentials","name":"Credentials"}},
                {"transportName":"http","transportUrl":"https://bad.example/manifest.json#secret","manifest":{"id":"fragment","name":"Fragment"}},
                {"transportName":"http","transportUrl":"https://invalid.example/manifest.json","manifest":{"name":"Missing ID"}},
                {"transportName":"http","transportUrl":"stremio://chosen.example/manifest.json?token=a%2Fb","manifest":{"id":"chosen","name":"Chosen addon"}},
                {"transportName":"http","transportUrl":"https://chosen.example/manifest.json?token=a%2Fb","manifest":{"id":"chosen","name":"Duplicate"}}
            ]}
            """;
        var controller = Create(new MemoryOrigin(uri => Json(uri.AbsolutePath.EndsWith("manifest.json", StringComparison.Ordinal) ? Manifest : entries)));
        var result = await controller.Discover(new("https://discovery.example/manifest.json", "all", "community/雪"), CancellationToken.None);
        var addon = Assert.Single(Assert.IsType<AddonDiscoveryController.DiscoveryResponse>(result.Value).Addons);
        Assert.Equal("https://chosen.example/manifest.json?token=a%2Fb", addon.TransportUrl);
        Assert.Equal("chosen", addon.Id);
    }

    [Fact]
    public async Task UpstreamFailureDoesNotExposeCredentialBearingExceptionDetails()
    {
        var controller = Create(new MemoryOrigin(_ => throw new HttpRequestException("https://private.example/manifest.json?token=private-token")));
        var result = await controller.Discover(new("https://discovery.example/manifest.json"), CancellationToken.None);
        var failure = Assert.IsType<BadRequestObjectResult>(result.Result);
        var body = JsonSerializer.Serialize(failure.Value);
        Assert.DoesNotContain("private.example", body);
        Assert.DoesNotContain("private-token", body);
    }

    [Fact]
    public async Task ResponseByteLimitRejectsOtherwiseValidJson()
    {
        var oversized = "{\"id\":\"discovery\",\"addonCatalogs\":[],\"padding\":\"" + new string('a', 8 * 1024 * 1024) + "\"}";
        var controller = Create(new MemoryOrigin(_ => Json(oversized)));
        var result = await controller.Discover(new("https://discovery.example/manifest.json"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    private static AddonDiscoveryController Create(ISafeHttpClient http) => new(http, new ConfigurationAccessor(() => new PluginConfiguration()));
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class MemoryOrigin(Func<Uri, HttpResponseMessage> respond) : ISafeHttpClient
    {
        public Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(uri));
        }
    }
}
