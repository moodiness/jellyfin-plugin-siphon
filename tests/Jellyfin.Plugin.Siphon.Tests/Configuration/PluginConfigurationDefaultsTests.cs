using Jellyfin.Plugin.Siphon.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Configuration;

public sealed class PluginConfigurationDefaultsTests
{
    [Fact]
    public void CinemetaIsEnabledWithoutAnyDefaultCatalogSubscription()
    {
        var configuration = new PluginConfiguration();

        var addon = Assert.Single(configuration.Addons);
        Assert.Equal("Cinemata", addon.DisplayName);
        Assert.Equal("stremio://v3-cinemeta.strem.io/manifest.json", addon.ManifestUrl);
        Assert.True(Guid.TryParseExact(addon.Id, "N", out _));
        Assert.True(addon.Enabled);
        Assert.Empty(addon.Catalogs);
        ConfigurationValidator.Validate(configuration);
    }
}
