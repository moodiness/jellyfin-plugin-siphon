using System.Net;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Protocol;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Configuration;

public sealed class SiphonAdminControllerTests
{
    [Fact]
    public async Task BlockedPrivateManifestIdentifiesTheExactAllowlistTarget()
    {
        const string manifestUrl = "http://10.23.45.67:12345/addons/example/manifest.json";
        var configuration = new PluginConfiguration();
        configuration.Addons = [];
        var accessor = new ConfigurationAccessor(() => configuration);
        using var client = new StremioClient(new ThrowingOrigin(), accessor);
        var controller = new SiphonAdminController(client, new EmptyState(), new SsrfPolicy(accessor));

        var result = await controller.ValidateAddon(new SiphonAdminController.ValidateRequest(manifestUrl), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var error = Assert.IsType<SiphonAdminController.ValidationError>(badRequest.Value);
        Assert.Equal("PrivateHostBlocked", error.Code);
        Assert.Equal("10.23.45.67", error.Host);
    }

    [Fact]
    public async Task CapabilitiesPreserveGlobalAndObjectRestrictionsIncludingTv()
    {
        const string json = """
            {"id":"capabilities","name":"Capabilities","types":["movie","tv"],"idPrefixes":["tt"],
             "resources":["stream",{"name":"meta","types":["series"],"idPrefixes":["tmdb:"]},
                          {"name":"subtitles"},{"name":"stream","types":["anime"],"idPrefixes":[]}],
             "catalogs":[]}
            """;
        var accessor = new ConfigurationAccessor(() => new PluginConfiguration());
        using var client = new StremioClient(new ManifestOrigin(json), accessor);
        var controller = new SiphonAdminController(client, new EmptyState(), new SsrfPolicy(accessor));
        var result = await controller.ValidateAddon(new("https://example.org/manifest.json"), CancellationToken.None);
        var response = Assert.IsType<SiphonAdminController.ManifestResponse>(result.Value);
        Assert.Equal(["movie", "tv"], response.Types);
        Assert.False(response.Resources[0].IsObject);
        Assert.True(response.Resources[1].IsObject);
        Assert.Equal(["series"], response.Resources[1].Types);
        Assert.Equal(["tmdb:"], response.Resources[1].IdPrefixes);
        Assert.Null(response.Resources[2].Types);
        Assert.Null(response.Resources[2].IdPrefixes);
        Assert.Empty(response.Resources[3].IdPrefixes!);

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var manifest = StremioJson.Manifest(document.RootElement);
        foreach (var resource in new[] { "stream", "meta", "subtitles" })
            foreach (var type in new[] { "movie", "series", "tv", "anime" })
                foreach (var id in new[] { "tt123", "tmdb:42", "other" })
                {
                    var advertised = response.Resources.Any(value => value.Name == resource
                        && value.Types is not null && value.Types.Contains(type)
                        && (value.IdPrefixes is null || value.IdPrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.Ordinal))));
                    Assert.Equal(AddonRegistry.Supports(manifest, resource, type, id), advertised);
                }
    }

    private sealed class ThrowingOrigin : ISafeHttpClient
    {
        public Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken) =>
            throw new HttpRequestException("blocked");
    }

    private sealed class ManifestOrigin(string manifest) : ISafeHttpClient
    {
        public Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(manifest)
            });
    }

    private sealed class EmptyState : ISiphonStateStore
    {
        public IReadOnlyList<ManagedItem> GetItems() => [];
        public ManagedItem? FindByKey(string key) => null;
        public ManagedItem? FindByPath(string path) => null;
        public ManagedItem? FindByContentKey(string contentKey) => null;
        public Task SaveAsync(IReadOnlyList<ManagedItem> items, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
