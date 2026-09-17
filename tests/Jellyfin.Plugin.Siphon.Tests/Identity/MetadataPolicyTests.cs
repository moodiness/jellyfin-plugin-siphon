using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Metadata;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Identity;

public sealed class MetadataPolicyTests
{
    [Fact]
    public void FillMissingPreservesAddonFieldsAndNeverCopiesParentPlotToEpisode()
    {
        var episode = EpisodeItem() with { Description = "Episode plot", SeriesDescription = "Addon series plot", EpisodeCommunityRating = 7.5f };
        var merged = MetadataPolicy.Merge(episode, new MetadataValues { Overview = "Provider series plot", Name = "Provider series", Rating = 9f, Poster = "https://images.example/series.jpg" }, new());
        Assert.Equal("Episode plot", merged.Description);
        Assert.Equal("Addon series plot", merged.SeriesDescription);
        Assert.Equal(7.5f, merged.EpisodeCommunityRating);
        Assert.Equal(9f, merged.CommunityRating);
        Assert.Equal("Episode title", merged.Name);
        Assert.Equal("https://images.example/series.jpg", merged.PosterUrl);
        Assert.Null(merged.ThumbnailUrl);
        Assert.Same(episode.StreamIdentities, merged.StreamIdentities);
        Assert.Same(episode.Owners, merged.Owners);
        Assert.Equal((episode.Key, episode.ContentKey, episode.ContentId, episode.VideoId, episode.Path), (merged.Key, merged.ContentKey, merged.ContentId, merged.VideoId, merged.Path));
    }

    [Fact]
    public void SelectedRefreshDoesNotClearOnFailureOrRefreshUnselectedFields()
    {
        var original = EpisodeItem() with { SeriesDescription = "Previous plot", PosterUrl = "https://images.example/old.jpg", CommunityRating = 6f };
        var config = new PluginConfiguration { MetadataUpdateMode = "RefreshSelected", MetadataRefreshFields = ["Overview"] };
        var merged = MetadataPolicy.Merge(original, new MetadataValues { Overview = "Fresh plot", Poster = "https://images.example/new.jpg", Rating = 9f }, config);
        Assert.Equal("Fresh plot", merged.SeriesDescription);
        Assert.Equal(original.PosterUrl, merged.PosterUrl);
        Assert.Equal(original.CommunityRating, merged.CommunityRating);
        Assert.Equal(merged, MetadataPolicy.Merge(merged, new MetadataValues(), config));
    }

    [Fact]
    public void HigherPriorityProviderWinsWithoutDiscardingFallbackFields()
    {
        var tmdb = new MetadataValues { Name = "TMDB title", Overview = "TMDB plot" };
        var tvdb = new MetadataValues { Name = "TVDB title", Overview = "TVDB plot", Runtime = TimeSpan.TicksPerMinute * 45 };
        var merged = MetadataPolicy.Merge(EpisodeItem(), tmdb.FillFrom(tvdb), new PluginConfiguration { MetadataUpdateMode = "RefreshSelected", MetadataRefreshFields = ["Name", "Overview", "Runtime"] });
        Assert.Equal("TMDB title", merged.SeriesName);
        Assert.Equal("TMDB plot", merged.SeriesDescription);
        Assert.Equal(TimeSpan.TicksPerMinute * 45, merged.RunTimeTicks);
    }

    [Fact]
    public async Task MapperHonorsGlobalAndPerFieldNativeLocksWithoutUnlocking()
    {
        var config = new PluginConfiguration { MetadataUpdateMode = "RefreshSelected", MetadataRefreshFields = ["Name", "Overview", "People", "Images", "Ratings", "Runtime"] };
        var mapper = new ItemMetadataMapper(new ConfigurationAccessor(() => config), null!, null!, null!, null!);
        var locked = new Movie { Name = "Manual name", Overview = "Manual plot", IsLocked = true, DateLastSaved = DateTime.UtcNow, CommunityRating = 8f };
        var item = EpisodeItem() with { Type = "movie", Name = "Provider name", Description = "Provider plot", CommunityRating = 2f, PosterUrl = "https://images.example/new.jpg" };
        mapper.Apply(locked, item, item.Key, item.ProviderIds);
        await mapper.SavePeopleAsync([(locked, item with { People = [new("New actor", "Actor")] }, false)], CancellationToken.None);
        Assert.True(locked.IsLocked);
        Assert.Equal("Manual name", locked.Name);
        Assert.Equal("Manual plot", locked.Overview);
        Assert.Equal(8f, locked.CommunityRating);
        Assert.False(locked.HasImage(ImageType.Primary, 0));

        var partial = new Movie { Name = "Manual name", Overview = "Old plot", DateLastSaved = DateTime.UtcNow, LockedFields = [MetadataField.Name, MetadataField.Cast] };
        mapper.Apply(partial, item, item.Key, item.ProviderIds);
        await mapper.SavePeopleAsync([(partial, item with { People = [new("New actor", "Actor")] }, false)], CancellationToken.None);
        Assert.Equal("Manual name", partial.Name);
        Assert.Equal("Provider plot", partial.Overview);
        Assert.Contains(MetadataField.Cast, partial.LockedFields);
    }

    [Fact]
    public void MapperPreservesNativeProbedRuntimeAndInitializesNewUnselectedTitle()
    {
        var config = new PluginConfiguration { MetadataUpdateMode = "RefreshSelected", MetadataRefreshFields = ["Runtime"] };
        var mapper = new ItemMetadataMapper(new ConfigurationAccessor(() => config), null!, null!, null!, null!);
        var movie = new Movie { Name = "Manual name", DateLastSaved = DateTime.UtcNow, Container = "mkv", RunTimeTicks = 100 };
        movie.ProviderIds["SiphonSource"] = "source";
        var item = EpisodeItem() with { Type = "movie", Name = "Movie title", RunTimeTicks = TimeSpan.TicksPerMinute * 100 };
        mapper.Apply(movie, item, item.Key, []);
        Assert.Equal(100, movie.RunTimeTicks);
        Assert.Equal("Manual name", movie.Name);
        var episode = new Episode { DateLastSaved = DateTime.MinValue };
        mapper.Apply(episode, EpisodeItem(), "episode", []);
        Assert.Equal("Episode title", episode.Name);
    }

    [Fact]
    public void ProviderIdentifiersRejectUntrustedPathComponentsAndDoNotGuess()
    {
        Assert.Null(MetadataEnrichmentService.Id(new Dictionary<string, string> { ["Tmdb"] = "123/credits" }, "Tmdb"));
        Assert.Null(MetadataEnrichmentService.Id(new Dictionary<string, string> { ["Imdb"] = "tt1234567?key=x" }, "Imdb"));
        Assert.Null(MetadataEnrichmentService.Id(new Dictionary<string, string> { ["Tvdb"] = "-123" }, "Tvdb"));
        Assert.Equal("tt0137523", MetadataEnrichmentService.Id(new Dictionary<string, string> { ["imdb"] = "tt0137523" }, "Imdb"));
    }

    internal static ManagedItem EpisodeItem() => new()
    {
        Key = "episode-key",
        ContentKey = "series-key",
        ContentId = "tt0137523",
        VideoId = "addon-exact-id",
        Path = "unchanged-path",
        Type = "series",
        Name = "Episode title",
        SeriesName = "Series title",
        Season = 1,
        Episode = 2,
        Owners = ["owner"],
        StreamIdentities = [new("series", "addon-exact-id")]
    };
}
