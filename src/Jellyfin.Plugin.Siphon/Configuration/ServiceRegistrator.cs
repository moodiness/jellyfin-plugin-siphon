using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Siphon.Configuration;

/// <summary>Reads the loaded plugin configuration; missing initialization is an error.</summary>
public sealed class ConfigurationAccessor
{
    private readonly Func<PluginConfiguration> _getConfiguration;
    internal SemaphoreSlim MutationGate { get; } = new(1, 1);

    public ConfigurationAccessor() : this(() => Plugin.Instance?.Configuration
        ?? throw new InvalidOperationException("Siphon has not been initialized."))
    {
    }

    public ConfigurationAccessor(Func<PluginConfiguration> getConfiguration)
    {
        _getConfiguration = getConfiguration;
    }

    public PluginConfiguration Current => _getConfiguration();
}

/// <summary>Registers Siphon services before Jellyfin builds its container.</summary>
public sealed class ServiceRegistrator : MediaBrowser.Controller.Plugins.IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, MediaBrowser.Controller.IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ConfigurationAccessor>();
        serviceCollection.AddSingleton<Infrastructure.SiphonPaths>();
        serviceCollection.AddSingleton<Infrastructure.SiphonSecretStore>();
        serviceCollection.AddSingleton<Infrastructure.SiphonStateStore>();
        serviceCollection.AddSingleton<Infrastructure.ISiphonStateStore>(services => services.GetRequiredService<Infrastructure.SiphonStateStore>());
        serviceCollection.AddSingleton<Infrastructure.CapabilityTokenService>();
        serviceCollection.AddSingleton<Infrastructure.SsrfPolicy>();
        serviceCollection.AddSingleton<Infrastructure.ISafeHttpClient, Infrastructure.SafeHttpClient>();
        serviceCollection.AddSingleton<Protocol.StremioClient>();
        serviceCollection.AddSingleton<Protocol.AddonRegistry>();
        serviceCollection.AddSingleton<Channels.SiphonChannel>();
        serviceCollection.AddSingleton<Infrastructure.SiphonItemLocator>();
        serviceCollection.AddSingleton<MediaBrowser.Controller.Channels.IChannel>(services => services.GetRequiredService<Channels.SiphonChannel>());
        serviceCollection.AddSingleton<Playback.StreamResolver>();
        serviceCollection.AddSingleton<Playback.ProxySessionStore>();
        serviceCollection.AddSingleton<Playback.SiphonMediaSourceProvider>();
        serviceCollection.AddSingleton<MediaBrowser.Controller.Library.IMediaSourceProvider>(services => services.GetRequiredService<Playback.SiphonMediaSourceProvider>());
        serviceCollection.AddSingleton<Tasks.CatalogSyncTask>();
        serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask>(services => services.GetRequiredService<Tasks.CatalogSyncTask>());
        serviceCollection.AddSingleton<Subtitles.StremioSubtitleProvider>();
        serviceCollection.AddSingleton<MediaBrowser.Controller.Subtitles.ISubtitleProvider>(services => services.GetRequiredService<Subtitles.StremioSubtitleProvider>());
        serviceCollection.AddSingleton<Identity.CatalogSyncService>();
    }
}
