using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;

namespace Jellyfin.Plugin.Siphon.MediaSegments;

/// <summary>Independent timing enrichment: never replaces the selected metadata provider or native chapters.</summary>
public sealed class IntroDbMediaSegmentProvider(
    IntroDbClient client,
    ConfigurationAccessor configuration,
    ILibraryManager library,
    ISiphonStateStore state) : IMediaSegmentProvider, IHasOrder
{
    public const string ProviderName = "Siphon IntroDB";
    internal static readonly string ProviderId = ProviderName.ToLowerInvariant().GetMD5().ToString("N");
    public string Name => ProviderName;
    public int Order => 100;

    public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(IsManaged(item));

    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
    {
        if (library.GetItemById(request.ItemId) is not Video item) return [];
        return (await ReadAsync(item, cancellationToken).ConfigureAwait(false)).Segments;
    }

    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken)
    {
        if (library.GetItemById(itemId) is Video item && Identity(item) is { } identity) client.Forget(identity);
        return Task.CompletedTask;
    }

    internal bool IsManaged(BaseItem item) => item is Movie or Episode
        && item.GetProviderId("Siphon") is { } key && state.FindByKey(key) is not null;

    internal bool IsEnabled(BaseItem item) => configuration.Current.EnableIntroDb && IsManaged(item)
        && !library.GetLibraryOptions(item).DisabledMediaSegmentProviders.Contains(ProviderName, StringComparer.OrdinalIgnoreCase);

    internal bool MayHaveSegments(Guid itemId) => library.GetItemById(itemId) is Video item
        && IsEnabled(item) && item.RunTimeTicks is > 0 && Identity(item) is not null
        && (configuration.Current.IntroDbSegments ?? []).Any(type => type is "intro" or "recap" or "outro");

    internal IntroDbRange? CachedPostCredits(Guid itemId)
    {
        if (library.GetItemById(itemId) is not Video item || !IsEnabled(item)
            || !(configuration.Current.IntroDbSegments ?? []).Contains("post-credits", StringComparer.Ordinal)
            || Identity(item) is not { } identity) return null;
        var range = client.Peek(identity)?.Data?.PostCredits;
        return IntroDbSegments.WithinRuntime(range, item.RunTimeTicks) ? range : null;
    }

    internal async Task<IntroDbItemSegments> ReadAsync(Video item, CancellationToken ct)
    {
        if (!IsEnabled(item)) return new("Disabled", null, [], null, null);
        var identity = Identity(item);
        if (identity is null) return new("MissingIdentity", null, [], null, null);
        if (item.RunTimeTicks is not > 0) return new("MissingRuntime", null, [], null, null);
        var response = await client.ReadAsync(identity, ct).ConfigureAwait(false);
        // Configuration can change during the read; never return a disabled timing from an old snapshot.
        if (!IsEnabled(item)) return new("Disabled", response, [], null, identity);
        var enabled = configuration.Current.IntroDbSegments ?? [];
        var segments = IntroDbSegments.Map(item.Id, response.Data, item.RunTimeTicks, enabled);
        var postCredits = enabled.Contains("post-credits", StringComparer.Ordinal)
            && IntroDbSegments.WithinRuntime(response.Data?.PostCredits, item.RunTimeTicks)
            ? response.Data!.PostCredits : null;
        var invalid = response.Data is { } data && (data.UnsafePostCredits
            || (data.PostCredits is not null && !IntroDbSegments.WithinRuntime(data.PostCredits, item.RunTimeTicks))
            || (data.Intro is not null && !IntroDbSegments.WithinRuntime(data.Intro, item.RunTimeTicks))
            || (data.Recap is not null && !IntroDbSegments.WithinRuntime(data.Recap, item.RunTimeTicks))
            || (data.Outro is not null && !IntroDbSegments.WithinRuntime(data.Outro, item.RunTimeTicks)));
        return new(invalid ? "InvalidRanges" : response.Status, response, segments, postCredits, identity);
    }

    private IntroDbIdentity? Identity(Video item)
    {
        if (item.GetProviderId("Siphon") is not { } key || state.FindByKey(key) is not { } managed) return null;
        if (item is Movie)
        {
            var imdb = item.GetProviderId("Imdb") ?? managed.ProviderIds.GetValueOrDefault("Imdb");
            return IntroDbIdentity.IsValidImdb(imdb) ? new(imdb!, true, null, null) : null;
        }
        if (item is not Episode episode) return null;
        // Episode-level IMDb IDs cannot be sent as show IDs. Retained/manual native series
        // identity wins; the selected addon's saved series identity is the only fallback.
        var series = library.GetItemById(episode.SeriesId);
        var seriesImdb = series?.GetProviderId("Imdb") ?? managed.ProviderIds.GetValueOrDefault("Imdb");
        var season = episode.ParentIndexNumber ?? managed.Season;
        var number = episode.IndexNumber ?? managed.Episode;
        return IntroDbIdentity.IsValidImdb(seriesImdb) && season is > 0 && number is > 0
            ? new(seriesImdb!, false, season, number) : null;
    }
}

internal sealed record IntroDbItemSegments(string Status, IntroDbRead? Response,
    IReadOnlyList<MediaSegmentDto> Segments, IntroDbRange? PostCredits, IntroDbIdentity? Identity);
