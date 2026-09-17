using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Diagnostics;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Metadata;
using Jellyfin.Plugin.Siphon.Protocol;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Identity;

public sealed class ItemInspectionTests
{
    [Fact]
    public void OmittedFieldsRetainOriginalEvidenceAcrossPersistenceAndProviderSelectionChanges()
    {
        var observed = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var old = new StremioMeta { Id = "tt0137523", Type = "movie", Name = "Old title", Description = "Original plot", Poster = "https://example.invalid/private?token=never-expose" };
        MetadataProvenance.Observe(old, new("Addon", Guid.NewGuid().ToString("N"), observed));
        var previous = Convert(old);
        var fresh = new StremioMeta { Id = old.Id, Type = "movie", Name = "Selected title" };
        MetadataProvenance.Observe(fresh, new("SelectedAddon", Guid.NewGuid().ToString("N"), observed.AddDays(1)));
        var merged = CatalogSyncService.MergeMetadata(Convert(fresh), previous, authoritativeMetadata: true);
        var saved = JsonSerializer.Deserialize<ManagedItem>(JsonSerializer.Serialize(merged))!;
        var native = new Movie { Id = Guid.NewGuid(), Name = "Legacy native title", DateLastSaved = DateTime.UtcNow };
        native.SetProviderId("Siphon", saved.Key);
        var response = Inspect(native, saved, new PluginConfiguration { MetadataAddonId = Guid.NewGuid().ToString("N") });
        var plot = Assert.Single(response.Fields, field => field.Field == "Managed.Description");
        Assert.Equal("Addon", plot.Origin);
        Assert.Equal(observed, plot.ObservedAtUtc);
        Assert.Equal("IncomingValueMissing", plot.PreservationReason);
        Assert.Equal("SelectedAddon", Assert.Single(response.Fields, field => field.Field == "Managed.Name").Origin);
        Assert.Equal("Unknown", Assert.Single(response.Fields, field => field.Field == "Native.Name").Origin);
        Assert.DoesNotContain("never-expose", JsonSerializer.Serialize(response));
    }

    [Fact]
    public void NativeRetentionKeepsPublishedOriginAndLockedEditsAreNotAttributedToCurrentAddon()
    {
        var source = new StremioMeta { Id = "tt0137523", Type = "movie", Name = "Published title" };
        MetadataProvenance.ObserveAddon(source, Guid.NewGuid().ToString("N"));
        var old = Convert(source);
        var native = new Movie { Id = Guid.NewGuid() };
        var config = new PluginConfiguration();
        var mapper = new ItemMetadataMapper(new(() => config), null!, null!, null!, null!);
        mapper.Apply(native, old, old.Key, []);
        var replacement = new StremioMeta { Id = source.Id, Type = "movie", Name = "New selected title" };
        MetadataProvenance.ObserveAddon(replacement, Guid.NewGuid().ToString("N"), selected: true);
        var updated = Convert(replacement);
        mapper.Apply(native, updated, updated.Key, []);
        var retained = Assert.Single(Inspect(native, updated, config).Fields, field => field.Field == "Native.Name");
        Assert.Equal("Published title", native.Name);
        Assert.Equal("Addon", retained.Origin);
        Assert.Equal("MetadataUpdatePolicy", retained.PreservationReason);
        native.Name = "Native edit";
        native.LockedFields = [MetadataField.Name];
        config.MetadataUpdateMode = "RefreshSelected";
        config.MetadataRefreshFields = ["Name"];
        mapper.Apply(native, updated, updated.Key, []);
        var edited = Assert.Single(Inspect(native, updated, config).Fields, field => field.Field == "Native.Name");
        Assert.Equal("Native edit", native.Name);
        Assert.Equal("ManualOrNative", edited.Origin);
        Assert.True(edited.IsLocked);
        Assert.Equal("FieldLocked", edited.PreservationReason);
        Assert.Empty(edited.SourceLabels);
    }

    [Fact]
    public void InspectorWithholdsNativeArtworkUrlsAndUntrustedProvenanceStrings()
    {
        var item = Convert(new StremioMeta { Id = "tt0137523", Type = "movie", Name = "Safe title" }) with
        {
            MetadataProvenance = new() { ["Name"] = new([new("https://secret.invalid/api?token=private", "private-installation-key", DateTimeOffset.UtcNow)], "private-error") }
        };
        var native = new Movie { Id = Guid.NewGuid(), Name = "Safe title", DateLastSaved = DateTime.UtcNow };
        native.SetProviderId("Siphon", item.Key);
        native.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = "https://secret.invalid/Siphon/image/signed-private-token", DateModified = DateTime.UtcNow }, 0);
        var response = Inspect(native, item, new());
        Assert.Equal("Unknown", Assert.Single(response.Fields, field => field.Field == "Managed.Name").Origin);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(response), StringComparison.OrdinalIgnoreCase);
        Assert.True(Assert.Single(response.Fields, field => field.Field == "Native.ImagePrimary").HasValue);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CollectionInspectionAttributesOnlyRetainedFallbackAdditions(bool episode, bool addsValue)
    {
        var full = Collections(episode, ["Drama", "Comedy"], ["Canada", "France"],
            [new("Alex", "Actor", "Lead"), new("Bob", "Director")]);
        var fallback = Collections(episode, addsValue ? ["drama", "Thriller"] : ["drama"],
            addsValue ? ["canada", "Japan"] : ["canada"],
            addsValue ? [new("alex", "Actor", "Discarded"), new("Casey", "Writer")] : [new("alex", "Actor", "Discarded")]);
        var observed = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var primary = new MetadataObservation("SelectedAddon", Guid.NewGuid().ToString("N"), observed);
        var preview = new MetadataObservation("Addon", Guid.NewGuid().ToString("N"), observed.AddDays(-1));
        MetadataProvenance.Observe(full, primary);
        MetadataProvenance.Observe(fallback, preview);
        var item = JsonSerializer.Deserialize<ManagedItem>(JsonSerializer.Serialize(Convert(full, fallback)))!;
        Assert.Equal(addsValue ? ["Drama", "Comedy", "Thriller"] : new[] { "Drama", "Comedy" }, episode ? item.EpisodeGenres : item.Genres);
        Assert.Equal(addsValue ? ["Canada", "France", "Japan"] : new[] { "Canada", "France" }, episode ? item.EpisodeProductionLocations : item.ProductionLocations);
        var people = episode ? item.EpisodePeople : item.People;
        Assert.Equal("Lead", Assert.Single(people, person => person.Name.Equals("Alex", StringComparison.OrdinalIgnoreCase)).Role);
        Assert.Equal(addsValue, people.Any(person => person.Name == "Casey"));
        BaseItem native = episode ? new Episode() : new Movie();
        native.Id = Guid.NewGuid();
        native.SetProviderId("Siphon", item.Key);
        var response = Inspect(native, item, new());
        foreach (var field in new[] { "Genres", "ProductionLocations", "People" })
        {
            var name = (episode ? "Episode" : "") + field;
            Assert.Equal(addsValue ? [primary, preview] : new[] { primary }, item.MetadataProvenance[name].Sources);
            var inspected = Assert.Single(response.Fields, value => value.Field == "Managed." + name);
            Assert.Equal(addsValue ? "Mixed" : "SelectedAddon", inspected.Origin);
            Assert.Equal(addsValue ? preview.ObservedAtUtc : primary.ObservedAtUtc, inspected.ObservedAtUtc);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CollectionLimitsDoNotAttributeDiscardedFallbackValues(bool episode)
    {
        var values = Enumerable.Range(0, 256).Select(index => $"Value {index}").ToArray();
        var full = Collections(episode, values, values, []);
        var fallback = Collections(episode, ["Discarded genre"], ["Discarded location"], []);
        var primary = new MetadataObservation("Addon", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        MetadataProvenance.Observe(full, primary);
        var item = Convert(full, fallback);
        Assert.Equal(values, episode ? item.EpisodeGenres : item.Genres);
        Assert.Equal(values, episode ? item.EpisodeProductionLocations : item.ProductionLocations);
        foreach (var field in new[] { "Genres", "ProductionLocations" })
            Assert.Equal([primary], item.MetadataProvenance[(episode ? "Episode" : "") + field].Sources);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MergedCreditDetailsRetainUnknownFallbackEvidence(bool episode)
    {
        var full = Collections(episode, [], [],
            [new("Alex", "Actor", PhotoUrl: "https://example.invalid/current.jpg"), new("Bob", "Actor", "Lead")]);
        var fallback = Collections(episode, [], [],
            [new("alex", "Actor", "Guest"), new("bob", "Actor", PhotoUrl: "https://example.invalid/retained.jpg")]);
        var primary = new MetadataObservation("Addon", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        MetadataProvenance.Observe(full, primary);
        var item = Convert(full, fallback);
        var people = episode ? item.EpisodePeople : item.People;
        Assert.Equal("Guest", people[0].Role);
        Assert.Equal("https://example.invalid/current.jpg", people[0].PhotoUrl);
        Assert.Equal("Lead", people[1].Role);
        Assert.Equal("https://example.invalid/retained.jpg", people[1].PhotoUrl);
        Assert.Equal([primary, new MetadataObservation("Unknown", null, null)],
            item.MetadataProvenance[episode ? "EpisodePeople" : "People"].Sources);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EpisodeIdentifierEvidenceIgnoresOverriddenIdsButIncludesRetainedIds(bool addsValue)
    {
        var full = new StremioMeta
        {
            Id = "tt0137523",
            Type = "series",
            Name = "Series",
            Videos = [new("episode", "Episode", 1, 1, null) { ProviderIds = new(StringComparer.OrdinalIgnoreCase) { ["Tvdb"] = "100", ["Imdb"] = "tt1000001" } }]
        };
        var fallbackIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tvdb"] = "200" };
        if (addsValue) fallbackIds["Tmdb"] = "300";
        var fallback = new StremioMeta
        {
            Id = full.Id,
            Type = "series",
            Name = "Series",
            Videos = [new("episode", "Episode", 1, 1, null) { ProviderIds = fallbackIds }]
        };
        var primary = new MetadataObservation("SelectedAddon", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var preview = new MetadataObservation("Addon", Guid.NewGuid().ToString("N"), primary.ObservedAtUtc!.Value.AddDays(-1));
        MetadataProvenance.Observe(full, primary);
        MetadataProvenance.Observe(fallback, preview);
        var item = Convert(full, fallback);
        Assert.Equal("100", item.EpisodeProviderIds["Tvdb"]);
        Assert.Equal(addsValue, item.EpisodeProviderIds.ContainsKey("Tmdb"));
        Assert.Equal(addsValue ? [primary, preview] : new[] { primary }, item.MetadataProvenance["EpisodeProviderIds"].Sources);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthoritativeCollectionsIgnorePreviewValuesAndEvidence(bool episode)
    {
        var full = Collections(episode, ["Drama"], ["Canada"], [new("Alex", "Actor")]);
        var fallback = Collections(episode, ["Comedy"], ["France"], [new("Bob", "Director")]);
        var primary = new MetadataObservation("SelectedAddon", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        MetadataProvenance.Observe(full, primary);
        MetadataProvenance.ObserveAddon(fallback, Guid.NewGuid().ToString("N"));
        var item = Convert(full, fallback, authoritativeMetadata: true);
        Assert.Equal(["Drama"], episode ? item.EpisodeGenres : item.Genres);
        Assert.Equal(["Canada"], episode ? item.EpisodeProductionLocations : item.ProductionLocations);
        Assert.Equal([new ManagedPerson("Alex", "Actor")], episode ? item.EpisodePeople : item.People);
        foreach (var field in new[] { "Genres", "ProductionLocations", "People" })
            Assert.Equal([primary], item.MetadataProvenance[(episode ? "Episode" : "") + field].Sources);
    }

    private static StremioMeta Collections(bool episode, string[] genres, string[] locations, ManagedPerson[] people) => new()
    {
        Id = "tt0137523",
        Type = episode ? "series" : "movie",
        Name = "Title",
        Genres = genres.ToList(),
        ProductionLocations = locations,
        People = people,
        Videos = episode ? [new("episode", "Episode", 1, 1, null) { Genres = genres, ProductionLocations = locations, People = people }] : []
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreditLimitDoesNotAttributeAnIncomingCreditThatWasNotRetained(bool episode)
    {
        var credits = Enumerable.Range(0, 256).Select(index => new ManagedPerson($"Actor {index}", "Actor")).ToArray();
        var full = Collections(episode, [], [], [new("Discarded actor", "Actor")]);
        var fallback = Collections(episode, [], [], credits);
        MetadataProvenance.ObserveAddon(full, Guid.NewGuid().ToString("N"), selected: true);
        var preview = new MetadataObservation("Addon", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        MetadataProvenance.Observe(fallback, preview);
        var item = Convert(full, fallback);
        Assert.Equal(credits, episode ? item.EpisodePeople : item.People);
        Assert.Equal([preview], item.MetadataProvenance[episode ? "EpisodePeople" : "People"].Sources);
    }

    private static ManagedItem Convert(StremioMeta meta, StremioMeta? fallback = null, bool authoritativeMetadata = false)
    {
        var values = new Dictionary<string, ManagedItem>();
        CatalogSyncService.ConvertMetadata(fallback ?? meta, meta, "installation", [], new Dictionary<(string, string), string>(), values,
            new Dictionary<string, ManagedItem>(), 10, authoritativeMetadata: authoritativeMetadata);
        return Assert.Single(values.Values);
    }

    private static ItemInspectionResponse Inspect(BaseItem native, ManagedItem item, PluginConfiguration config)
    {
        var library = DispatchProxy.Create<ILibraryManager, LibraryProxy>();
        ((LibraryProxy)(object)library).Item = native;
        return new ItemInspectionController(library, new State(item), new(() => config)).Get(native.Id).Value!;
    }

    public class LibraryProxy : DispatchProxy
    {
        public BaseItem Item { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "GetItemById" => Item,
            "GetPeople" => new List<PersonInfo>(),
            _ => throw new InvalidOperationException("Inspection must only read one native item and its local credits.")
        };
    }

    private sealed class State(ManagedItem item) : ISiphonStateStore
    {
        public ManagedItem? FindByKey(string key) => key == item.Key ? item : null;
        public ManagedItem? FindByContentKey(string key) => key == item.ContentKey ? item : null;
        public ManagedItem? FindByPath(string path) => throw new InvalidOperationException();
        public IReadOnlyList<ManagedItem> GetItems() => throw new InvalidOperationException("Inspection must not clone the entire catalog.");
        public Task SaveAsync(IReadOnlyList<ManagedItem> items, CancellationToken cancellationToken) => throw new InvalidOperationException("Inspection must not mutate persistent state.");
    }
}
