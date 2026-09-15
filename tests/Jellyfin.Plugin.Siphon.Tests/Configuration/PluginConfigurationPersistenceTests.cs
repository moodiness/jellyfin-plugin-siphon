using System.Xml.Serialization;
using Jellyfin.Plugin.Siphon.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Configuration;

public sealed class PluginConfigurationPersistenceTests
{
    [Fact]
    public void SavedCatalogSelectionsSurviveRepeatedReloadsWithoutDuplicatingAddons()
    {
        var configuration = new PluginConfiguration();
        var addon = Assert.Single(configuration.Addons);
        var installationId = addon.Id;
        var catalog = new CatalogSubscription { Type = "movie", Id = "top", MaxItems = 25 };
        addon.Catalogs.Add(catalog);
        addon.Enabled = false;

        for (var reload = 0; reload < 3; reload++)
        {
            configuration = Reload(configuration);
            var savedAddon = Assert.Single(configuration.Addons);
            Assert.Equal(installationId, savedAddon.Id);
            Assert.False(savedAddon.Enabled);
            var savedCatalog = Assert.Single(savedAddon.Catalogs);
            Assert.Equal(catalog.Key, savedCatalog.Key);
            Assert.Equal(25, savedCatalog.MaxItems);
            ConfigurationValidator.Validate(configuration);
        }
    }

    [Fact]
    public void RemovingAllAddonsStaysRemovedAfterReload()
    {
        var configuration = Reload(new PluginConfiguration { Addons = [] });

        Assert.Empty(configuration.Addons);
        ConfigurationValidator.Validate(configuration);
    }

    private static PluginConfiguration Reload(PluginConfiguration configuration)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var output = new StringWriter();
        serializer.Serialize(output, configuration);
        using var input = new StringReader(output.ToString());
        return (PluginConfiguration)serializer.Deserialize(input)!;
    }
}
