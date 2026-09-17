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

    /// <summary>The embedded, offline settings icon resource.</summary>
    internal const string IconResourceName = "Jellyfin.Plugin.Siphon.Assets.siphon.png";
    private readonly Configuration.ConfigurationAccessor _configurationAccessor;

    /// <summary>Initializes the plugin.</summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer,
        Protocol.AddonRegistry registry, Playback.StreamResolver resolver, Playback.ProxySessionStore sessions,
        Configuration.ConfigurationAccessor configurationAccessor)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _configurationAccessor = configurationAccessor;
        var previousPlayback = CapturePlayback(Configuration);
        ConfigurationChanged += (_, _) =>
        {
            registry.Invalidate();
            var currentPlayback = CapturePlayback(Configuration);
            if (currentPlayback.Transport != previousPlayback.Transport)
            {
                resolver.Invalidate();
                sessions.Invalidate();
            }
            else
            {
                bool Changed(Guid userId) => currentPlayback.Profiles.GetValueOrDefault(userId, currentPlayback.Shared)
                    != previousPlayback.Profiles.GetValueOrDefault(userId, previousPlayback.Shared);
                resolver.Invalidate(Changed);
                sessions.Invalidate(Changed);
            }
            previousPlayback = currentPlayback;
        };
    }

    private static PlaybackConfiguration CapturePlayback(Configuration.PluginConfiguration config)
        => new(Protocol.AddonRegistry.PlaybackKey(config, Guid.Empty),
            config.UserProfiles.Where(profile => profile.OverrideAddons)
                .ToDictionary(profile => profile.UserId, profile => Protocol.AddonRegistry.PlaybackKey(config, profile.UserId)),
            config.PublicBaseUrl + "\n" + string.Join('\n', config.AllowedPrivateHosts.Order(StringComparer.OrdinalIgnoreCase)));

    private sealed record PlaybackConfiguration(string Shared, Dictionary<Guid, string> Profiles, string Transport);

    /// <summary>Gets the loaded plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Siphon";

    /// <inheritdoc />
    public override string Description => "Brings Stremio catalogs, metadata, subtitles and stream versions to native Jellyfin libraries.";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is not Configuration.PluginConfiguration config)
        {
            throw new ArgumentException("Invalid Siphon configuration type.", nameof(configuration));
        }

        if (!_configurationAccessor.SynchronizationGate.Wait(0))
        {
            throw new ArgumentException("Stop the Siphon synchronization before changing its configuration.");
        }

        try
        {
            if (!_configurationAccessor.MutationGate.Wait(0))
            {
                throw new ArgumentException("Wait for the current Siphon library update before changing its configuration.");
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
        finally
        {
            _configurationAccessor.SynchronizationGate.Release();
        }
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "siphon",
            DisplayName = "Siphon",
            EnableInMainMenu = true,
            MenuIcon = "settings",
            EmbeddedResourcePath = "Jellyfin.Plugin.Siphon.Web.siphon.html"
        };
    }
}
