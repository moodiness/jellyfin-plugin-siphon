using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Siphon.Configuration;

/// <summary>Reads the loaded plugin configuration; missing initialization is an error.</summary>
public sealed class ConfigurationAccessor
{
    private readonly Func<PluginConfiguration> _getConfiguration;
    // Snapshot/configuration operations take this before MutationGate when both are needed.
    internal SemaphoreSlim SynchronizationGate { get; } = new(1, 1);
    // Native library writes only; upstream I/O must never hold this gate.
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
        serviceCollection.AddHttpContextAccessor();
        serviceCollection.AddSingleton<Infrastructure.SiphonPaths>();
        serviceCollection.AddSingleton<Infrastructure.SiphonSecretStore>();
        serviceCollection.AddSingleton<Infrastructure.SiphonStateStore>();
        serviceCollection.AddSingleton<Infrastructure.ISiphonStateStore>(services => services.GetRequiredService<Infrastructure.SiphonStateStore>());
        serviceCollection.AddSingleton<Infrastructure.CapabilityTokenService>();
        serviceCollection.AddSingleton<Infrastructure.UserPreferenceStore>();
        serviceCollection.AddSingleton<Infrastructure.SsrfPolicy>();
        serviceCollection.AddSingleton<Infrastructure.SafeHttpClient>();
        serviceCollection.AddSingleton<Infrastructure.ISafeHttpClient>(services => services.GetRequiredService<Infrastructure.SafeHttpClient>());
        serviceCollection.AddSingleton<Protocol.StremioClient>();
        serviceCollection.AddSingleton<Protocol.AddonRegistry>();
        serviceCollection.AddSingleton<Infrastructure.SyncDiagnostics>();
        serviceCollection.AddSingleton<Infrastructure.TargetedSyncRequestStore>();
        serviceCollection.AddSingleton<SelfConnectionProbe>();
        serviceCollection.AddSingleton<Infrastructure.MetadataResponseCache>();
        serviceCollection.AddSingleton<Infrastructure.MetadataQuotaStore>();
        serviceCollection.AddSingleton<Metadata.MetadataProviderClient>();
        serviceCollection.AddSingleton<Metadata.MetadataEnrichmentService>();
        serviceCollection.AddSingleton<Identity.ItemMetadataMapper>();
        serviceCollection.AddSingleton<Identity.CatalogLibraryService>();
        serviceCollection.AddSingleton<Collections.CatalogCollectionService>();
        serviceCollection.AddSingleton<Collections.CollectionRetentionService>();
        serviceCollection.AddSingleton<Recovery.RecoveryLedger>();
        serviceCollection.AddSingleton<Recovery.WatchRecoveryService>();
        serviceCollection.AddSingleton<Identity.LibraryMaterializer>();
        serviceCollection.AddSingleton<Identity.FollowedSeriesSelector>();
        serviceCollection.AddSingleton<Identity.CatalogCleanupService>();
        serviceCollection.AddSingleton<Infrastructure.SiphonItemLocator>();
        serviceCollection.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(services => services.GetRequiredService<Identity.LibraryMaterializer>());
        serviceCollection.AddSingleton<Search.AddonSearchService>();
        serviceCollection.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(services => services.GetRequiredService<Search.AddonSearchService>());
        serviceCollection.AddSingleton<Search.SiphonSearchProvider>();
        serviceCollection.AddSingleton<ISearchProvider>(services => services.GetRequiredService<Search.SiphonSearchProvider>());
        serviceCollection.AddSingleton<Playback.PlaybackAccess>();
        serviceCollection.AddSingleton<Playback.PlaybackDownloadService>();
        serviceCollection.AddSingleton<Downloads.IDownloadTransfer, Downloads.DownloadTransferService>();
        serviceCollection.AddSingleton<Downloads.DownloadQueueStore>();
        serviceCollection.AddSingleton<Downloads.DownloadQueueService>();
        serviceCollection.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(services => services.GetRequiredService<Downloads.DownloadQueueService>());
        serviceCollection.AddSingleton<P2p.P2pStreamService>();
        serviceCollection.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(services => services.GetRequiredService<P2p.P2pStreamService>());
        serviceCollection.AddSingleton<MediaSegments.IntroDbClient>();
        serviceCollection.AddSingleton<MediaSegments.IntroDbMediaSegmentProvider>();
        serviceCollection.AddSingleton<MediaBrowser.Controller.MediaSegments.IMediaSegmentProvider>(services => services.GetRequiredService<MediaSegments.IntroDbMediaSegmentProvider>());
        serviceCollection.AddSingleton<Calendar.FollowedCalendarService>();
        serviceCollection.AddSingleton<Calendar.CalendarNotificationStore>();
        serviceCollection.AddSingleton<Calendar.CalendarNotificationService>();
        serviceCollection.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(services => services.GetRequiredService<Calendar.CalendarNotificationService>());
        serviceCollection.AddSingleton<Notifications.NotificationWebhookStore>();
        serviceCollection.AddSingleton<Notifications.NotificationWebhookService>();
        serviceCollection.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(services => services.GetRequiredService<Notifications.NotificationWebhookService>());
        serviceCollection.AddSingleton<Playback.StreamResolver>();
        serviceCollection.AddSingleton<Playback.SourceBindingStore>();
        serviceCollection.AddSingleton<Playback.ProxySessionStore>();
        serviceCollection.AddSingleton<Playback.NativeVersionService>();
        serviceCollection.AddScoped<Playback.NativeVersionResultFilter>();
        serviceCollection.AddScoped<CatalogShortcutResultFilter>();
        serviceCollection.AddScoped<Search.NativeSearchSelectionFilter>();
        serviceCollection.AddScoped<Search.NativeSearchReleaseFilter>();
        serviceCollection.AddScoped<Collections.NativeMembershipFilter>();
        serviceCollection.AddScoped<Playback.NativeDownloadFilter>();
        serviceCollection.AddScoped<MediaSegments.IntroDbChapterResultFilter>();
        serviceCollection.Configure<MvcOptions>(options =>
        {
            options.Filters.AddService<Playback.NativeVersionResultFilter>();
            options.Filters.AddService<MediaSegments.IntroDbChapterResultFilter>();
            options.Filters.AddService<CatalogShortcutResultFilter>();
            options.Filters.AddService<Search.NativeSearchSelectionFilter>();
            options.Filters.AddService<Search.NativeSearchReleaseFilter>();
            options.Filters.AddService<Collections.NativeMembershipFilter>();
            options.Filters.AddService<Playback.NativeDownloadFilter>();
        });
        serviceCollection.AddSingleton<Playback.SiphonMediaSourceProvider>();
        serviceCollection.AddSingleton<MediaBrowser.Controller.Library.IMediaSourceProvider>(services => services.GetRequiredService<Playback.SiphonMediaSourceProvider>());
        serviceCollection.AddSingleton<Tasks.CatalogSyncTask>();
        serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask>(services => services.GetRequiredService<Tasks.CatalogSyncTask>());
        serviceCollection.AddSingleton<Tasks.MetadataRefreshTask>();
        serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask>(services => services.GetRequiredService<Tasks.MetadataRefreshTask>());
        serviceCollection.AddSingleton<Tasks.FollowedSeriesSyncTask>();
        serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask>(services => services.GetRequiredService<Tasks.FollowedSeriesSyncTask>());
        serviceCollection.AddSingleton<Tasks.TargetedSyncTask>();
        serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask>(services => services.GetRequiredService<Tasks.TargetedSyncTask>());
        serviceCollection.AddSingleton<Subtitles.StremioSubtitleProvider>();
        serviceCollection.AddSingleton<Subtitles.ManagedSubtitleStore>();
        serviceCollection.AddSingleton<MediaBrowser.Controller.Subtitles.ISubtitleProvider>(services => services.GetRequiredService<Subtitles.StremioSubtitleProvider>());
        serviceCollection.AddSingleton<Identity.CatalogSyncService>();

        // Keep Jellyfin's source manager (and its lifetime/disposal) behind the Siphon-specific adapter.
        const string nativeManagerKey = "Siphon.OriginalMediaSourceManager";
        var nativeManager = serviceCollection.Last(service => !service.IsKeyedService && service.ServiceType == typeof(IMediaSourceManager));
        serviceCollection.Remove(nativeManager);
        serviceCollection.Add(ServiceDescriptor.DescribeKeyed(typeof(IMediaSourceManager), nativeManagerKey, (services, _) =>
            nativeManager.ImplementationInstance
            ?? nativeManager.ImplementationFactory?.Invoke(services)
            ?? ActivatorUtilities.CreateInstance(services, nativeManager.ImplementationType!), nativeManager.Lifetime));
        serviceCollection.Add(ServiceDescriptor.Describe(typeof(IMediaSourceManager), services =>
            new Playback.NativeMediaSourceManager(
                services.GetRequiredKeyedService<IMediaSourceManager>(nativeManagerKey),
                services.GetRequiredService<ILibraryManager>(),
                services.GetRequiredService<Playback.PlaybackAccess>(),
                services.GetRequiredService<Playback.ProxySessionStore>(),
                services.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>()), nativeManager.Lifetime));

        const string nativeSubtitleKey = "Siphon.OriginalSubtitleManager";
        var subtitleType = typeof(MediaBrowser.Controller.Subtitles.ISubtitleManager);
        var nativeSubtitles = serviceCollection.Last(service => !service.IsKeyedService && service.ServiceType == subtitleType);
        serviceCollection.Remove(nativeSubtitles);
        serviceCollection.Add(ServiceDescriptor.DescribeKeyed(subtitleType, nativeSubtitleKey, (services, _) =>
            nativeSubtitles.ImplementationInstance
            ?? nativeSubtitles.ImplementationFactory?.Invoke(services)
            ?? ActivatorUtilities.CreateInstance(services, nativeSubtitles.ImplementationType!), nativeSubtitles.Lifetime));
        serviceCollection.Add(ServiceDescriptor.Describe(subtitleType, services =>
            new Subtitles.NativeSubtitleManager(
                services.GetRequiredKeyedService<MediaBrowser.Controller.Subtitles.ISubtitleManager>(nativeSubtitleKey),
                services.GetRequiredService<Subtitles.ManagedSubtitleStore>(),
                services.GetRequiredService<Playback.PlaybackAccess>(),
                services.GetRequiredService<Playback.NativeVersionService>()), nativeSubtitles.Lifetime));

        const string nativeSearchKey = "Siphon.OriginalSearchManager";
        var nativeSearch = serviceCollection.Last(service => !service.IsKeyedService && service.ServiceType == typeof(ISearchManager));
        serviceCollection.Remove(nativeSearch);
        serviceCollection.Add(ServiceDescriptor.DescribeKeyed(typeof(ISearchManager), nativeSearchKey, (services, _) =>
            nativeSearch.ImplementationInstance
            ?? nativeSearch.ImplementationFactory?.Invoke(services)
            ?? ActivatorUtilities.CreateInstance(services, nativeSearch.ImplementationType!), nativeSearch.Lifetime));
        serviceCollection.Add(ServiceDescriptor.Describe(typeof(ISearchManager), services =>
            new Search.NativeSearchManager(
                services.GetRequiredKeyedService<ISearchManager>(nativeSearchKey),
                services.GetRequiredService<Infrastructure.UserPreferenceStore>(),
                services.GetRequiredService<ConfigurationAccessor>(),
                services.GetRequiredService<ILibraryManager>()), nativeSearch.Lifetime));

        const string nativeSegmentsKey = "Siphon.OriginalMediaSegmentManager";
        var segmentType = typeof(MediaBrowser.Controller.MediaSegments.IMediaSegmentManager);
        var nativeSegments = serviceCollection.Last(service => !service.IsKeyedService && service.ServiceType == segmentType);
        serviceCollection.Remove(nativeSegments);
        serviceCollection.Add(ServiceDescriptor.DescribeKeyed(segmentType, nativeSegmentsKey, (services, _) =>
            nativeSegments.ImplementationInstance
            ?? nativeSegments.ImplementationFactory?.Invoke(services)
            ?? ActivatorUtilities.CreateInstance(services, nativeSegments.ImplementationType!), nativeSegments.Lifetime));
        serviceCollection.Add(ServiceDescriptor.Describe(segmentType, services =>
            new MediaSegments.NativeIntroDbMediaSegmentManager(
                services.GetRequiredKeyedService<MediaBrowser.Controller.MediaSegments.IMediaSegmentManager>(nativeSegmentsKey),
                services.GetRequiredService<MediaSegments.IntroDbMediaSegmentProvider>(),
                services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Jellyfin.Database.Implementations.JellyfinDbContext>>(),
                services.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>()), nativeSegments.Lifetime));

        const string nativeChaptersKey = "Siphon.OriginalChapterRepository";
        var chapterType = typeof(MediaBrowser.Controller.Persistence.IChapterRepository);
        var nativeChapters = serviceCollection.Last(service => !service.IsKeyedService && service.ServiceType == chapterType);
        serviceCollection.Remove(nativeChapters);
        serviceCollection.Add(ServiceDescriptor.DescribeKeyed(chapterType, nativeChaptersKey, (services, _) =>
            nativeChapters.ImplementationInstance
            ?? nativeChapters.ImplementationFactory?.Invoke(services)
            ?? ActivatorUtilities.CreateInstance(services, nativeChapters.ImplementationType!), nativeChapters.Lifetime));
        serviceCollection.Add(ServiceDescriptor.Describe(chapterType, services =>
            new MediaSegments.IntroDbChapterRepository(
                services.GetRequiredKeyedService<MediaBrowser.Controller.Persistence.IChapterRepository>(nativeChaptersKey),
                services.GetRequiredService<MediaSegments.IntroDbMediaSegmentProvider>()), nativeChapters.Lifetime));
    }
}
