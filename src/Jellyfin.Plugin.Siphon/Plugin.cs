using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Siphon;

/// <summary>The Siphon Jellyfin plugin.</summary>
public sealed class Plugin : BasePlugin<Configuration.PluginConfiguration>, IHasWebPages
{
    /// <summary>The stable plugin identifier.</summary>
    public static readonly Guid PluginId = new("b2df1c14-4b7e-4e7b-9a95-8f9ad8d2b0c1");
    private readonly Configuration.ConfigurationAccessor _configurationAccessor;

    /// <summary>Initializes the plugin.</summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer,
        Protocol.AddonRegistry registry, Playback.StreamResolver resolver, Playback.ProxySessionStore sessions,
        Configuration.ConfigurationAccessor configurationAccessor)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _configurationAccessor = configurationAccessor;
        ConfigurationChanged += (_, _) =>
        {
            registry.Invalidate();
            resolver.Invalidate();
            sessions.Invalidate();
        };
    }

    /// <summary>Gets the loaded plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Siphon";

    /// <inheritdoc />
    public override string Description => "Integrates Stremio addons into Jellyfin, bringing their catalogs, metadata and playback sources into your media library.";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is not Configuration.PluginConfiguration config)
        {
            throw new ArgumentException("Invalid Siphon configuration type.", nameof(configuration));
        }

        if (!_configurationAccessor.MutationGate.Wait(0))
        {
            throw new ArgumentException("Stop the Siphon synchronization before changing its configuration.");
        }

        try
        {
            Jellyfin.Plugin.Siphon.Configuration.ConfigurationValidator.Validate(config);
            base.UpdateConfiguration(config);
        }
        finally
        {
            _configurationAccessor.MutationGate.Release();
        }
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "siphon",
            EmbeddedResourcePath = "Jellyfin.Plugin.Siphon.Web.siphon.html"
        };
    }
}
