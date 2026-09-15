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
    public async Task BlockedPrivateManifestExplainsTheExactAllowlistAction()
    {
        const string manifestUrl = "http://10.23.45.67:12345/addons/example/manifest.json";
        var configuration = new PluginConfiguration();
        configuration.Addons.Clear();
        var accessor = new ConfigurationAccessor(() => configuration);
        using var client = new StremioClient(new ThrowingOrigin(), accessor);
        var controller = new SiphonAdminController(client, new EmptyState(), new SsrfPolicy(accessor));

        var result = await controller.ValidateAddon(new SiphonAdminController.ValidateRequest(manifestUrl), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var message = badRequest.Value?.GetType().GetProperty("Message")?.GetValue(badRequest.Value)?.ToString();
        Assert.Contains("10.23.45.67", message);
        Assert.Contains("Allowed private hosts", message);
    }

    [Fact]
    public async Task AllowlistedPrivateManifestIsValidated()
    {
        const string manifestUrl = "http://10.23.45.67:12345/addons/example/manifest.json";
        var configuration = new PluginConfiguration { AllowedPrivateHosts = ["10.23.45.67"] };
        configuration.Addons.Clear();
        var accessor = new ConfigurationAccessor(() => configuration);
        using var client = new StremioClient(new ManifestOrigin(), accessor);
        var controller = new SiphonAdminController(client, new EmptyState(), new SsrfPolicy(accessor));

        var result = await controller.ValidateAddon(new SiphonAdminController.ValidateRequest(manifestUrl), CancellationToken.None);

        var manifest = Assert.IsType<SiphonAdminController.ManifestResponse>(result.Value);
        Assert.Equal("local.addon", manifest.Id);
    }

    private sealed class ThrowingOrigin : ISafeHttpClient
    {
        public Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken) =>
            throw new HttpRequestException("blocked");
    }

    private sealed class ManifestOrigin : ISafeHttpClient
    {
        public Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"local.addon\",\"name\":\"Local addon\",\"catalogs\":[]}")
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
