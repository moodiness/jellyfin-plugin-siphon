using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Metadata;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.Siphon.Identity;

/// <summary>Maps addon metadata without treating series metadata as episode metadata.</summary>
public sealed class ItemMetadataMapper(
    ConfigurationAccessor configuration,
    CapabilityTokenService tokens,
    ILibraryManager library,
    IDbContextFactory<JellyfinDbContext> database,
    IItemPersistenceService persistence,
    SyncDiagnostics? diagnostics = null)
{
    private const long MaximumRuntimeTicks = 7 * TimeSpan.TicksPerDay;
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"];

    public void Apply(BaseItem target, ManagedItem item, string key, IEnumerable<KeyValuePair<string, string>> providerIds)
    {
        var settings = configuration.Current;
        var initialize = target.DateLastSaved == DateTime.MinValue;
        bool Can(string field, bool missing) => MetadataPolicy.CanUpdate(target, settings, field, missing, initialize);
        target.IsVirtualItem = false;
        target.ChannelId = Guid.Empty;
        foreach (var (provider, id) in providerIds)
        {
            if (!string.IsNullOrWhiteSpace(id) && !target.ProviderIds.ContainsKey(provider) && !target.IsLocked)
                target.ProviderIds[provider] = id;
        }
        target.ProviderIds["Siphon"] = key;
        if (target.DateCreated == DateTime.MinValue) target.DateCreated = DateTime.UtcNow;
        target.DateModified = target.DateLastSaved = DateTime.UtcNow;

        if (target is Season)
        {
            // Episode plots and dates never belong on a season.
            SetImage(target, item, ImageType.Primary, item.SeasonPosterUrl, "season", initialize);
            return;
        }

        var isEpisode = target is Episode;
        var isSeries = target is Series;
        var name = isSeries ? item.SeriesName : item.Name;
        if (!string.IsNullOrWhiteSpace(name) && Can("Name", string.IsNullOrWhiteSpace(target.Name)))
        {
            target.Name = name;
            ItemMetadataOwnership.Record(target, "Name", target.Name, item, isSeries ? "SeriesName" : "Name");
        }
        var overview = isSeries ? item.SeriesDescription : item.Description;
        if (!string.IsNullOrWhiteSpace(overview) && Can("Overview", string.IsNullOrWhiteSpace(target.Overview)))
        {
            target.Overview = overview;
            ItemMetadataOwnership.Record(target, "Overview", target.Overview, item, isSeries ? "SeriesDescription" : "Description");
        }

        var premiere = ParseDate(isSeries ? item.SeriesReleased : item.Released);
        if (premiere.HasValue && Can("ReleaseDate", !target.PremiereDate.HasValue))
        {
            target.PremiereDate = premiere;
            ItemMetadataOwnership.Record(target, "PremiereDate", target.PremiereDate, item, isSeries ? "SeriesReleased" : "Released");
        }
        var year = isEpisode ? premiere?.Year : item.Year ?? premiere?.Year;
        if (year is >= 1800 and <= 2200 && Can("ReleaseDate", !target.ProductionYear.HasValue))
        {
            target.ProductionYear = year;
            ItemMetadataOwnership.Record(target, "ProductionYear", target.ProductionYear, item,
                isEpisode || !item.Year.HasValue ? isSeries ? "SeriesReleased" : "Released" : "Year");
        }

        var rating = isEpisode ? item.EpisodeCommunityRating : item.CommunityRating;
        if (rating is >= 0 and <= 10 && Can("Ratings", !target.CommunityRating.HasValue))
        {
            target.CommunityRating = rating;
            ItemMetadataOwnership.Record(target, "CommunityRating", target.CommunityRating, item, isEpisode ? "EpisodeCommunityRating" : "CommunityRating");
        }
        var runtime = isEpisode ? item.EpisodeRunTimeTicks : item.RunTimeTicks;
        // A probed native version can be a different cut from the catalog's advertised runtime.
        if (runtime is > 0 and <= MaximumRuntimeTicks
            && Can("Runtime", !target.RunTimeTicks.HasValue)
            && !(target is Video { Container: not null } && target.GetProviderId(Playback.NativeVersionService.SourceProvider) is not null))
        {
            target.RunTimeTicks = runtime;
            ItemMetadataOwnership.Record(target, "RunTimeTicks", target.RunTimeTicks, item, isEpisode ? "EpisodeRunTimeTicks" : "RunTimeTicks");
        }
        var certification = isEpisode ? item.EpisodeOfficialRating : item.OfficialRating;
        if (!string.IsNullOrWhiteSpace(certification) && Can("Ratings", string.IsNullOrWhiteSpace(target.OfficialRating)))
        {
            target.OfficialRating = certification;
            ItemMetadataOwnership.Record(target, "OfficialRating", target.OfficialRating, item, isEpisode ? "EpisodeOfficialRating" : "OfficialRating");
        }
        var genres = isEpisode ? item.EpisodeGenres : item.Genres;
        if (genres.Length > 0 && Can("Genres", target.Genres.Length == 0))
        {
            target.Genres = (string[])genres.Clone();
            ItemMetadataOwnership.Record(target, "Genres", target.Genres, item, isEpisode ? "EpisodeGenres" : "Genres");
        }
        var locations = isEpisode ? item.EpisodeProductionLocations : item.ProductionLocations;
        if (locations.Length > 0 && Can("ProductionLocations", target.ProductionLocations.Length == 0))
        {
            target.ProductionLocations = (string[])locations.Clone();
            ItemMetadataOwnership.Record(target, "ProductionLocations", target.ProductionLocations, item, isEpisode ? "EpisodeProductionLocations" : "ProductionLocations");
        }
        if (target is Series series)
        {
            var status = item.SeriesStatus switch
            {
                "Continuing" => SeriesStatus.Continuing,
                "Ended" => SeriesStatus.Ended,
                "Unreleased" => SeriesStatus.Unreleased,
                _ => (SeriesStatus?)null
            };
            if (status.HasValue && Can("Status", !series.Status.HasValue))
            {
                series.Status = status;
                ItemMetadataOwnership.Record(target, "Status", series.Status?.ToString(), item, "SeriesStatus");
            }
        }

        SetImage(target, item, ImageType.Primary, isEpisode ? item.ThumbnailUrl : item.PosterUrl, isEpisode ? "thumbnail" : "poster", initialize);
        SetImage(target, item, ImageType.Backdrop, isEpisode ? item.EpisodeBackdropUrl : item.BackdropUrl, isEpisode ? "episode-backdrop" : "backdrop", initialize);
        SetImage(target, item, ImageType.Logo, isEpisode ? item.EpisodeLogoUrl : item.LogoUrl, isEpisode ? "episode-logo" : "logo", initialize);
    }

    /// <summary>Publishes credits and lazy native portraits in bounded, cancellable batches.</summary>
    public async Task SavePeopleAsync(IReadOnlyList<(BaseItem Item, ManagedItem Managed, bool Initialize)> items,
        CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var eligible = items.Where(entry => entry.Item is not Season && entry.Item.SupportsPeople
            && !entry.Item.IsLocked && !entry.Item.LockedFields.Contains(MetadataField.Cast)
            && (entry.Item is Episode ? entry.Managed.EpisodePeople : entry.Managed.People).Length > 0).ToArray();
        diagnostics?.ReportStage("Credits", "Items", 0, eligible.Length);
        if (eligible.Length == 0)
        {
            progress?.Report(100);
            return;
        }

        // Resolve names once, using .NET's Unicode comparison rather than SQLite's ASCII lower().
        // Existing People IDs and native credit roles must survive subsequent refreshes.
        var identities = new Dictionary<(string Name, string Type), People>(new PersonIdentityComparer());
        await using (var context = await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            await foreach (var person in context.Peoples.AsNoTracking().AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
                identities.TryAdd((person.Name, person.PersonType ?? string.Empty), person);
        }
        var published = new Dictionary<Guid, bool>();
        var completed = 0;
        foreach (var batch in eligible.Chunk(128))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var context = await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var itemIds = batch.Select(entry => entry.Item.Id).ToArray();
            var rows = await context.PeopleBaseItemMap.Include(map => map.People)
                .Where(map => itemIds.Contains(map.ItemId)).ToListAsync(cancellationToken).ConfigureAwait(false);
            var byItem = rows.ToLookup(map => map.ItemId);
            var portraits = new Dictionary<Guid, (string Name, ManagedItem Managed, ManagedPerson Credit, string Variant)>();
            var publications = new List<(BaseItem Target, object People, MetadataFieldProvenance Provenance)>();
            foreach (var (target, item, initialize) in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = byItem[target.Id].OrderBy(map => map.ListOrder).ToList();
                if (!MetadataPolicy.CanUpdate(target, configuration.Current, "People", remaining.Count == 0, initialize)) continue;
                var credits = remaining.Select(map => new PersonInfo
                {
                    Id = map.PeopleId,
                    Name = map.People.Name,
                    Role = map.Role ?? string.Empty,
                    Type = Enum.TryParse<PersonKind>(map.People.PersonType, out var kind) ? kind : PersonKind.Actor,
                    SortOrder = map.SortOrder
                }).ToList();
                var oldPeople = ItemMetadataOwnership.People(credits);
                var oldReceipt = ItemMetadataOwnership.Read(target, "People");
                var evidence = item.MetadataProvenance.GetValueOrDefault(target is Episode ? "EpisodePeople" : "People")
                    ?? new MetadataFieldProvenance([new("Unknown", null, null)]);
                if (credits.Count > 0)
                    evidence = MetadataProvenance.Combine(evidence,
                        oldReceipt?.Fingerprint == ItemMetadataOwnership.Fingerprint(oldPeople) ? oldReceipt.Provenance
                            : new([new("Unknown", null, null)]));
                var incoming = target is Episode ? item.EpisodePeople : item.People;
                var photos = new Dictionary<(string Name, string Type), (ManagedPerson Credit, int Index)>(new PersonIdentityComparer());
                for (var index = 0; index < incoming.Length; index++)
                {
                    var credit = incoming[index];
                    if (credit.Type is not ("Actor" or "Director" or "Writer" or "Producer") || string.IsNullOrWhiteSpace(credit.Name)) continue;
                    var name = credit.Name.Trim();
                    var kind = Enum.Parse<PersonKind>(credit.Type);
                    var person = credits.FirstOrDefault(existing => existing.Type == kind && existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (person is null)
                    {
                        person = new PersonInfo { Name = name, Type = kind, SortOrder = credits.Count, Role = string.Empty };
                        credits.Add(person);
                    }
                    if (!string.IsNullOrWhiteSpace(credit.Role)) person.Role = credit.Role.Trim();
                    if (!photos.TryGetValue((name, credit.Type), out var previous) || string.IsNullOrWhiteSpace(previous.Credit.PhotoUrl))
                        photos[(name, credit.Type)] = (credit, index);
                }

                var mappings = remaining.GroupBy(map => (map.PeopleId, Role: (map.Role ?? string.Empty).ToLowerInvariant()))
                    .ToDictionary(group => group.Key, group => group.First());
                var seen = new HashSet<(Guid, string)>();
                var order = 0;
                foreach (var credit in credits)
                {
                    var identity = (credit.Name, credit.Type.ToString());
                    // Prefer the ID already attached to this item if a legacy database has duplicate names.
                    var person = remaining.FirstOrDefault(map => map.PeopleId == credit.Id)?.People;
                    if (person is null && !identities.TryGetValue(identity, out person))
                    {
                        person = new People { Id = credit.Id, Name = credit.Name, PersonType = identity.Item2 };
                        context.Peoples.Add(person);
                        identities.Add(identity, person);
                    }
                    if (photos.TryGetValue(identity, out var photo))
                    {
                        var personId = library.GetPersonId(person.Name);
                        if ((!published.TryGetValue(personId, out var complete) || (!complete && !string.IsNullOrWhiteSpace(photo.Credit.PhotoUrl)))
                            && (!portraits.TryGetValue(personId, out var previous) || string.IsNullOrWhiteSpace(previous.Credit.PhotoUrl)))
                            portraits[personId] = (person.Name, item, photo.Credit, (target is Episode ? "episode-person-" : "person-") + photo.Index.ToString(CultureInfo.InvariantCulture));
                    }
                    var key = (person.Id, (credit.Role ?? string.Empty).ToLowerInvariant());
                    if (!seen.Add(key)) continue;
                    if (mappings.TryGetValue(key, out var map))
                    {
                        map.ListOrder = order;
                        map.SortOrder = credit.SortOrder;
                        remaining.Remove(map);
                    }
                    else
                    {
                        context.PeopleBaseItemMap.Add(new PeopleBaseItemMap
                        {
                            ItemId = target.Id,
                            Item = null!,
                            PeopleId = person.Id,
                            People = null!,
                            Role = credit.Role ?? string.Empty,
                            ListOrder = order,
                            SortOrder = credit.SortOrder
                        });
                    }
                    order++;
                }
                context.PeopleBaseItemMap.RemoveRange(remaining);
                publications.Add((target, ItemMetadataOwnership.People(credits), evidence));
            }

            // Publish native Person entities before their credits: cancellation cannot leave a
            // committed cast whose new people would be skipped by FillMissing on the next run.
            PublishPeople(portraits, published, cancellationToken);
            if (context.ChangeTracker.HasChanges())
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var publication in publications)
                ItemMetadataOwnership.Record(publication.Target, "People", publication.People, publication.Provenance);
            if (publications.Count > 0) persistence.SaveItems(publications.Select(publication => publication.Target).ToArray(), cancellationToken);
            completed += batch.Length;
            progress?.Report(100d * completed / eligible.Length);
            diagnostics?.ReportStage("Credits", "Items", completed, eligible.Length);
        }
    }

    private void PublishPeople(Dictionary<Guid, (string Name, ManagedItem Managed, ManagedPerson Credit, string Variant)> portraits,
        Dictionary<Guid, bool> published, CancellationToken cancellationToken)
    {
        foreach (var batch in portraits.Chunk(128))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = library.GetItemList(new InternalItemsQuery
            {
                ItemIds = batch.Select(entry => entry.Key).ToArray(),
                IncludeItemTypes = [BaseItemKind.Person],
                GroupByPresentationUniqueKey = false
            }).OfType<Person>().ToDictionary(person => person.Id);
            var added = new List<BaseItem>();
            var updated = new List<BaseItem>();
            foreach (var (id, portrait) in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isNew = !existing.TryGetValue(id, out var person);
                if (person is null)
                {
                    person = new Person
                    {
                        Id = id,
                        Name = portrait.Name,
                        Path = Person.GetPath(portrait.Name),
                        DateCreated = DateTime.UtcNow,
                        DateModified = DateTime.UtcNow
                    };
                    person.PresentationUniqueKey = person.CreatePresentationUniqueKey();
                }
                // Preserve other libraries' and users' portraits. Siphon images use the same
                // on-demand native image path as movie/episode artwork; never download here.
                var changed = (!person.HasImage(ImageType.Primary) || person.GetProviderId("SiphonImagePrimary") is not null)
                    && SetImage(person, portrait.Managed, ImageType.Primary, portrait.Credit.PhotoUrl, portrait.Variant, isNew,
                        portrait.Managed.Key + "\n" + portrait.Variant);
                if (isNew || changed)
                {
                    person.DateLastSaved = DateTime.UtcNow;
                    (isNew ? added : updated).Add(person);
                }
                published[id] = person.HasImage(ImageType.Primary)
                    || !MetadataPolicy.CanUpdate(person, configuration.Current, "Images", true);
            }
            if (added.Count > 0) library.CreateItems(added, null, cancellationToken);
            if (updated.Count > 0)
            {
                persistence.SaveItems(updated, cancellationToken);
                foreach (var person in updated) library.RegisterItem(person);
            }
        }
    }

    private sealed class PersonIdentityComparer : IEqualityComparer<(string Name, string Type)>
    {
        public bool Equals((string Name, string Type) x, (string Name, string Type) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name) && x.Type == y.Type;
        public int GetHashCode((string Name, string Type) value)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name), value.Type);
    }

    private bool SetImage(BaseItem target, ManagedItem item, ImageType imageType, string? upstream, string variant, bool initialize, string? revisionVariant = null)
    {
        if (!MetadataPolicy.CanUpdate(target, configuration.Current, "Images", true, initialize)) return false;
        if (string.IsNullOrWhiteSpace(configuration.Current.PublicBaseUrl)
            || !Uri.TryCreate(upstream, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        var marker = "SiphonImage" + imageType;
        var imageKey = target is Series ? item.ContentKey : item.Key;
        var revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(configuration.Current.PublicBaseUrl + "\n" + imageKey + "\n" + (revisionVariant ?? variant) + "\n" + upstream)));
        if (target.GetProviderId(marker) == revision && target.HasImage(imageType)) return false;
        var inheritedFrom = imageType == ImageType.Primary && target is Season or Episode
            ? library?.GetItemById(library.GetNewItemId("siphon:series:" + item.ContentKey, typeof(Series))) : null;
        if (!MetadataPolicy.CanUpdateImage(target, configuration.Current, imageType, missingManagedValue: true,
                initialize: initialize, inheritedFrom: inheritedFrom)) return false;
        target.SetProviderId(marker, revision);
        target.SetImage(new ItemImageInfo
        {
            Type = imageType,
            Path = configuration.Current.PublicBaseUrl.TrimEnd('/') + "/Siphon/image/" + tokens.SignItem(imageKey) + "?type=" + variant,
            DateModified = DateTime.UtcNow
        }, 0);
        var field = variant switch
        {
            "season" => "SeasonPosterUrl",
            "thumbnail" => "ThumbnailUrl",
            "poster" => "PosterUrl",
            "backdrop" => "BackdropUrl",
            "logo" => "LogoUrl",
            "episode-backdrop" => "EpisodeBackdropUrl",
            "episode-logo" => "EpisodeLogoUrl",
            _ => variant.StartsWith("episode-person-", StringComparison.Ordinal) ? "EpisodePeople" : "People"
        };
        ItemMetadataOwnership.Record(target, "Image" + imageType, target.GetImageInfo(imageType, 0)?.Path, item, field);
        return true;
    }

    internal static DateTime? ParseDate(string? value)
        => value is { Length: >= 10 and <= 64 }
            && DateTime.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            && date.Year is >= 1800 and <= 2200 ? date : null;
}
