using System.Text.Json;
using Jellyfin.Plugin.Siphon.Identity;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Identity;

public sealed class ManagedItemComparisonTests
{
    [Fact]
    public void ReloadedMetadataAndReorderedProviderKeysDoNotTriggerPublication()
    {
        var original = FollowedSeriesSelectorTests.Episode("show") with
        {
            ProviderIds = new() { ["Imdb"] = "tt1234567", ["Tmdb"] = "42" },
            EpisodeProviderIds = new() { ["Tvdb"] = "99" },
            Genres = ["Drama"],
            People = [new("Performer", "Actor", "Role")],
            EpisodeGenres = ["Comedy"],
            EpisodePeople = [new("Director", "Director")]
        };
        var reloaded = JsonSerializer.Deserialize<ManagedItem>(JsonSerializer.Serialize(original))! with
        {
            ProviderIds = new() { ["tmdb"] = "42", ["imdb"] = "tt1234567" },
            MissingSinceUtc = DateTimeOffset.UtcNow,
            MissingOwners = original.Owners
        };
        Assert.True(ManagedItemComparison.Equals(original, reloaded));
        Assert.Equal(ManagedItemComparison.Fingerprint(original), ManagedItemComparison.Fingerprint(reloaded));
    }

    [Fact]
    public void NestedEpisodeMetadataChangesAndCreditOrderRequirePublication()
    {
        var original = FollowedSeriesSelectorTests.Episode("show") with
        {
            EpisodeProviderIds = new() { ["Tvdb"] = "99" },
            People = [new("First", "Actor"), new("Second", "Actor")],
            EpisodePeople = [new("Director", "Director", PhotoUrl: "https://example.org/old")]
        };
        var changed = new[]
        {
            original with { EpisodeProviderIds = new() { ["Tvdb"] = "100" } },
            original with { People = [original.People[1], original.People[0]] },
            original with { EpisodePeople = [original.EpisodePeople[0] with { PhotoUrl = "https://example.org/new" }] },
            original with { StreamIdentities = [new("series", "replacement:video")] },
            original with { EpisodeRunTimeTicks = 100 }
        };
        foreach (var item in changed)
        {
            Assert.False(ManagedItemComparison.Equals(original, item));
            Assert.NotEqual(ManagedItemComparison.Fingerprint(original), ManagedItemComparison.Fingerprint(item));
        }
    }
}
