using System.Globalization;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Siphon.Metadata;

/// <summary>Enriches each content identity once; provider discoveries never re-key playable items.</summary>
public sealed class MetadataEnrichmentService(
    ConfigurationAccessor configuration,
    MetadataProviderClient providers,
    ILibraryManager library,
    IServerConfigurationManager server,
    ISiphonStateStore state,
    SyncDiagnostics? diagnostics = null)
{
    public async Task<IReadOnlyList<ManagedItem>> EnrichAsync(IReadOnlyList<ManagedItem> items, CancellationToken ct, IProgress<double>? progress = null, bool forceRefresh = false)
    {
        ct.ThrowIfCancellationRequested();
        var config = configuration.Current;
        var total = items.Select(item => item.ContentKey).Distinct(StringComparer.Ordinal).Count();
        diagnostics?.ReportStage("Metadata", "Titles", 0, total);
        if (items.Count == 0) { progress?.Report(100); return items; }
        if (!MetadataProviderClient.ProviderNames.Any(name => MetadataProviderClient.Enabled(config, name)))
        {
            var preserved = PreserveLockedProxyMetadata(items, ct, progress);
            diagnostics?.ReportStage("Metadata", "Titles", total, total);
            return preserved;
        }
        var completed = 0;
        var providerProgress = new ProgressRange(progress, 0, 90);
        var completionGate = new object();
        void Complete()
        {
            lock (completionGate)
            {
                completed++;
                diagnostics?.ReportStage("Metadata", "Titles", completed, total);
                providerProgress.Report(100d * completed / total);
            }
        }
        var output = items.ToArray();
        var fillMissing = new PluginConfiguration();
        var languages = LibraryLanguages();
        var groups = items.Select((item, index) => (Item: item, Index: index)).GroupBy(entry => entry.Item.ContentKey, StringComparer.Ordinal);
        await Parallel.ForEachAsync(groups, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(config.MaxConcurrentRequests, 1, 16), CancellationToken = ct }, async (group, token) =>
        {
            var entries = group.ToArray();
            var first = entries[0].Item;
            if (first.Type is not ("movie" or "series")) { Complete(); return; }
            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
                foreach (var (key, value) in entry.Item.ProviderIds) ids.TryAdd(key, value);
            var native = Native(first, first.Type == "series");
            if (native is not null)
                foreach (var (key, value) in native.ProviderIds) ids.TryAdd(key, value);
            var language = Clean(config.MetadataLanguage) ?? first.Owners.Order(StringComparer.Ordinal).Select(owner => languages.GetValueOrDefault(owner)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? Clean(native?.GetPreferredMetadataLanguage()) ?? Clean(server.Configuration.PreferredMetadataLanguage);
            var episodeSet = first.Type == "series"
                ? string.Join(";", entries.Select(entry => FormattableString.Invariant($"{entry.Item.Season}:{entry.Item.Episode}")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                : string.Empty;
            using var requestScope = providers.BeginScope(config, language, first.ContentKey, episodeSet, forceRefresh, diagnostics);
            var values = new MetadataValues();
            TmdbRecord? tmdb = null;
            TvdbRecord? tvdb = null;
            FanartRecord? fanart = null;
            string? imageBase = null;
            if (config.EnableTmdbMetadata)
            {
                var fetched = await providers.TryAsync("Tmdb", config, async () =>
                {
                    var id = Id(ids, "Tmdb");
                    if (id is null)
                    {
                        var external = Id(ids, "Imdb") ?? Id(ids, "Tvdb") ?? throw MissingId();
                        var source = external.StartsWith("tt", StringComparison.Ordinal) ? "imdb_id" : "tvdb_id";
                        var found = await providers.GetAsync<TmdbFind>("Tmdb", "https://api.themoviedb.org/3/find/" + external + "?external_source=" + source + Language(language), MetadataProviderClient.Bearer(config.TmdbReadAccessToken), true, token,
                            value => (first.Type == "movie" ? value.MovieResults : value.TvResults) is [{ Id: > 0 }]).ConfigureAwait(false);
                        var matches = first.Type == "movie" ? found.MovieResults : found.TvResults;
                        if (matches.Length != 1 || matches[0].Id <= 0) throw new MetadataProviderClient.ProviderFailure("NotFound");
                        id = matches[0].Id.ToString(CultureInfo.InvariantCulture);
                    }
                    var route = first.Type == "movie" ? "movie/" : "tv/";
                    var record = await providers.GetAsync<TmdbRecord>("Tmdb", "https://api.themoviedb.org/3/" + route + id + "?append_to_response=credits,external_ids" + Language(language), MetadataProviderClient.Bearer(config.TmdbReadAccessToken), true, token,
                        value => value.Id.ToString(CultureInfo.InvariantCulture) == id && !string.IsNullOrWhiteSpace(first.Type == "movie" ? value.Title : value.Name)).ConfigureAwait(false);
                    imageBase = await providers.TmdbImageBaseAsync(config, token).ConfigureAwait(false);
                    AddId(ids, "Tmdb", id);
                    AddId(ids, "Imdb", record.ExternalIds?.ImdbId);
                    AddId(ids, "Tvdb", record.ExternalIds?.TvdbId?.ToString(CultureInfo.InvariantCulture));
                    return record;
                }, token).ConfigureAwait(false);
                tmdb = fetched.Value;
                if (tmdb is not null) values = TmdbValues(tmdb, imageBase!);
            }
            if (config.EnableTvdbMetadata)
            {
                var fetched = await providers.TryAsync("Tvdb", config, async () =>
                {
                    var id = Id(ids, "Tvdb");
                    if (id is null)
                    {
                        // Numeric remote IDs are ambiguous across providers. Only IMDb is safe here.
                        var imdb = Id(ids, "Imdb") ?? throw MissingId();
                        var found = await providers.TvdbAsync<TvdbRemoteResult[]>(config, "search/remoteid/" + imdb, token,
                            value => value.Select(match => first.Type == "movie" ? match?.Movie : match?.Series).OfType<TvdbRecord>().ToArray() is [{ Id: > 0 }]).ConfigureAwait(false);
                        var matches = found.Select(match => first.Type == "movie" ? match?.Movie : match?.Series).OfType<TvdbRecord>().ToArray();
                        if (matches.Length != 1 || matches[0].Id <= 0) throw new MetadataProviderClient.ProviderFailure("NotFound");
                        id = matches[0].Id.ToString(CultureInfo.InvariantCulture);
                    }
                    var record = await providers.TvdbAsync<TvdbRecord>(config, (first.Type == "movie" ? "movies/" : "series/") + id + "/extended?meta=translations", token,
                        value => value.Id.ToString(CultureInfo.InvariantCulture) == id && !string.IsNullOrWhiteSpace(value.Name)).ConfigureAwait(false);
                    AddId(ids, "Tvdb", id);
                    foreach (var remote in record.RemoteIds)
                        if (remote?.SourceName?.Equals("IMDB", StringComparison.OrdinalIgnoreCase) == true) AddId(ids, "Imdb", remote.Id);
                    return record;
                }, token).ConfigureAwait(false);
                tvdb = fetched.Value;
                if (tvdb is not null) values = values.FillFrom(TvdbValues(tvdb, language));
            }
            if (config.EnableFanartMetadata)
            {
                var fetched = await providers.TryAsync("Fanart", config, async () =>
                {
                    var id = first.Type == "movie" ? Id(ids, "Tmdb") ?? Id(ids, "Imdb") : Id(ids, "Tvdb");
                    if (id is null) throw MissingId();
                    var result = await providers.GetAsync<FanartRecord>("Fanart", MetadataProviderClient.FanartUrl(config, (first.Type == "movie" ? "movies/" : "tv/") + id), null, false, token,
                        value => (first.Type == "movie" ? id.StartsWith("tt", StringComparison.Ordinal) ? value.ImdbId : value.TmdbId : value.TvdbId) == id).ConfigureAwait(false);
                    return result;
                }, token).ConfigureAwait(false);
                fanart = fetched.Value;
                if (fanart is not null)
                {
                    var movie = first.Type == "movie";
                    values = values with
                    {
                        Poster = Image(movie ? fanart.Movieposter : fanart.Tvposter, language) ?? values.Poster,
                        Backdrop = Image(movie ? fanart.Moviebackground : fanart.Showbackground, language) ?? values.Backdrop,
                        Logo = Image(movie ? fanart.Hdmovielogo : fanart.Hdtvlogo, language) ?? Image(movie ? fanart.Movielogo : fanart.Clearlogo, language) ?? values.Logo
                    };
                }
            }
            if (config.EnableMdbListMetadata)
            {
                var fetched = await providers.TryAsync("MdbList", config, async () =>
                {
                    var kind = new[] { "Imdb", "Tmdb", "Tvdb" }.FirstOrDefault(key => Id(ids, key) is not null) ?? throw MissingId();
                    var id = Id(ids, kind);
                    var type = first.Type == "movie" ? "movie" : "show";
                    var result = await providers.GetAsync<MdbRecord>("MdbList", MetadataProviderClient.MdbUrl(config, kind.ToLowerInvariant() + "/" + type + "/" + id), null, false, token,
                        value => (kind switch
                        {
                            "Imdb" => value.Ids?.Imdb,
                            "Tmdb" => value.Ids?.Tmdb?.ToString(CultureInfo.InvariantCulture),
                            _ => value.Ids?.Tvdb?.ToString(CultureInfo.InvariantCulture)
                        }) == id && value.Type == type && !string.IsNullOrWhiteSpace(value.Title)).ConfigureAwait(false);
                    return result;
                }, token).ConfigureAwait(false);
                if (fetched.Value?.Score is >= 0 and <= 100) values = values with { Rating = fetched.Value.Score / 10f };
            }

            // Consolidate pre-existing parent fields before enrichment so every retained episode shares one parent snapshot.
            var parent = first;
            foreach (var entry in entries.Skip(1)) parent = MetadataPolicy.Merge(parent, ParentValues(entry.Item), fillMissing);
            if (native?.IsLocked == true) values = values with { Poster = null, Backdrop = null, Logo = null };
            parent = MetadataPolicy.Merge(parent, values, config);
            foreach (var entry in entries)
            {
                var item = first.Type == "series" ? WithParent(entry.Item, parent) : MetadataPolicy.Merge(entry.Item, values, config);
                var discovered = new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase);
                foreach (var key in new[] { "Imdb", "Tmdb", "Tvdb" }) if (Id(ids, key) is { } id) discovered.TryAdd(key, id);
                item = item with { ProviderIds = discovered };
                if (fanart is not null && item.Season.HasValue && !SeasonLocked(item) && MetadataPolicy.Wants(config, "Images", string.IsNullOrWhiteSpace(item.SeasonPosterUrl)))
                    item = item with { SeasonPosterUrl = Image(fanart.Seasonposter.Where(image => image is not null && image.Season == item.Season.Value.ToString(CultureInfo.InvariantCulture)), language) ?? item.SeasonPosterUrl };
                output[entry.Index] = item;
            }
            if (first.Type != "series") { Complete(); return; }
            var episodeValues = new Dictionary<int, MetadataValues>();
            if (tmdb is not null && imageBase is not null)
            {
                foreach (var season in entries.Where(entry => NeedsEpisode(output[entry.Index], config, true, forceRefresh)).GroupBy(entry => entry.Item.Season))
                {
                    if (season.Key is not >= 0) continue;
                    var fetched = await providers.TryAsync("Tmdb", config, () => providers.GetAsync<TmdbRecord>("Tmdb", "https://api.themoviedb.org/3/tv/" + tmdb.Id.ToString(CultureInfo.InvariantCulture) + "/season/" + season.Key.Value.ToString(CultureInfo.InvariantCulture) + (language is null ? string.Empty : "?language=" + Uri.EscapeDataString(language)), MetadataProviderClient.Bearer(config.TmdbReadAccessToken), true, token,
                        value => value.Id > 0 && value.SeasonNumber == season.Key && value.Episodes is not null
                            && value.Episodes.All(episode => episode is not null && episode.Id > 0 && episode.SeasonNumber == season.Key && episode.EpisodeNumber > 0)
                            && value.Episodes.Select(episode => episode.EpisodeNumber).Distinct().Count() == value.Episodes.Length), token).ConfigureAwait(false);
                    if (fetched.Value is null) continue;
                    foreach (var entry in season)
                    {
                        var episode = (fetched.Value.Episodes ?? []).FirstOrDefault(record => record is not null && record.SeasonNumber == entry.Item.Season && record.EpisodeNumber == entry.Item.Episode);
                        if (episode is null || episode.Id <= 0) continue;
                        var metadata = TmdbValues(episode, imageBase, true);
                        if (Native(entry.Item)?.IsLocked == true) metadata = metadata with { Poster = null };
                        episodeValues[entry.Index] = metadata;
                        var item = MetadataPolicy.Merge(output[entry.Index], metadata, config, true);
                        var episodeIds = new Dictionary<string, string>(item.EpisodeProviderIds, StringComparer.OrdinalIgnoreCase);
                        AddId(episodeIds, "Tmdb", episode.Id.ToString(CultureInfo.InvariantCulture));
                        output[entry.Index] = item with { EpisodeProviderIds = episodeIds };
                    }
                }
            }
            if (tvdb is not null)
            {
                // The episode API is used only with a real episode ID, never the parent series ID.
                foreach (var entry in entries.Where(entry => NeedsEpisode(output[entry.Index], config, false, forceRefresh)))
                {
                    var id = Id(entry.Item.EpisodeProviderIds, "Tvdb");
                    if (id is null) continue;
                    var fetched = await providers.TryAsync("Tvdb", config, async () =>
                    {
                        var record = await providers.TvdbAsync<TvdbRecord>(config, "episodes/" + id + "/extended?meta=translations", token,
                            value => value.Id.ToString(CultureInfo.InvariantCulture) == id && value.SeasonNumber == entry.Item.Season
                                && value.Number == entry.Item.Episode && !string.IsNullOrWhiteSpace(value.Name)).ConfigureAwait(false);
                        return record;
                    }, token).ConfigureAwait(false);
                    if (fetched.Value is not null)
                    {
                        var metadata = TvdbValues(fetched.Value, language, true);
                        if (episodeValues.TryGetValue(entry.Index, out var preferred)) metadata = preferred.FillFrom(metadata);
                        if (Native(entry.Item)?.IsLocked == true) metadata = metadata with { Poster = null };
                        output[entry.Index] = MetadataPolicy.Merge(output[entry.Index], metadata, config, true);
                    }
                }
            }
            Complete();
        }).ConfigureAwait(false);
        return PreserveLockedProxyMetadata(output, ct, new ProgressRange(progress, 90, 100));
    }

    private IReadOnlyList<ManagedItem> PreserveLockedProxyMetadata(IReadOnlyList<ManagedItem> items, CancellationToken ct, IProgress<double>? progress)
    {
        ManagedItem[]? output = null;
        var config = configuration.Current;
        var peoplePolicy = new Dictionary<Guid, bool>();
        bool PreserveImage(BaseItem? target, ImageType type)
            => target is not null && !MetadataPolicy.CanUpdate(target, config, "Images", !target.HasImage(type));
        bool PreservePeople(BaseItem? target)
        {
            if (target is null) return false;
            if (peoplePolicy.TryGetValue(target.Id, out var preserve)) return preserve;
            preserve = !MetadataPolicy.CanUpdate(target, config, "People", true)
                || !MetadataPolicy.CanUpdate(target, config, "People", !library.GetPeople(target).Any());
            peoplePolicy[target.Id] = preserve;
            return preserve;
        }
        var parents = new Dictionary<string, BaseItem?>(StringComparer.Ordinal);
        var seasons = new Dictionary<(string, int), bool>();
        for (var index = 0; index < items.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            if (index % 64 == 0) progress?.Report(100d * index / items.Count);
            var item = items[index];
            var native = Native(item);
            var parent = native;
            var seasonLocked = false;
            if (item.Type == "series")
            {
                if (!parents.TryGetValue(item.ContentKey, out parent)) parents[item.ContentKey] = parent = Native(item, true);
                var seasonKey = (item.ContentKey, item.Season.GetValueOrDefault());
                if (!seasons.TryGetValue(seasonKey, out seasonLocked))
                {
                    var season = library.GetItemById(library.GetNewItemId("siphon:season:" + item.ContentKey + ":season:" + item.Season.GetValueOrDefault().ToString(CultureInfo.InvariantCulture), typeof(Season)));
                    seasons[seasonKey] = seasonLocked = PreserveImage(season, ImageType.Primary);
                }
            }
            var parentPoster = PreserveImage(parent, ImageType.Primary);
            var parentBackdrop = PreserveImage(parent, ImageType.Backdrop);
            var parentLogo = PreserveImage(parent, ImageType.Logo);
            var episodePoster = item.Type == "series" && PreserveImage(native, ImageType.Primary);
            var episodeBackdrop = item.Type == "series" && PreserveImage(native, ImageType.Backdrop);
            var episodeLogo = item.Type == "series" && PreserveImage(native, ImageType.Logo);
            var parentPeople = PreservePeople(parent);
            var episodePeople = item.Type == "series" && PreservePeople(native);
            if (!(parentPoster || parentBackdrop || parentLogo || episodePoster || episodeBackdrop || episodeLogo || seasonLocked || parentPeople || episodePeople)) continue;
            var previous = state.FindByKey(item.Key);
            if (previous is null) continue;
            var preserved = item with
            {
                PosterUrl = parentPoster ? previous.PosterUrl : item.PosterUrl,
                BackdropUrl = parentBackdrop ? previous.BackdropUrl : item.BackdropUrl,
                LogoUrl = parentLogo ? previous.LogoUrl : item.LogoUrl,
                SeasonPosterUrl = seasonLocked ? previous.SeasonPosterUrl ?? previous.PosterUrl : item.SeasonPosterUrl,
                ThumbnailUrl = episodePoster ? previous.ThumbnailUrl : item.ThumbnailUrl,
                EpisodeBackdropUrl = episodeBackdrop ? previous.EpisodeBackdropUrl : item.EpisodeBackdropUrl,
                EpisodeLogoUrl = episodeLogo ? previous.EpisodeLogoUrl : item.EpisodeLogoUrl,
                People = parentPeople ? previous.People : item.People,
                EpisodePeople = episodePeople ? previous.EpisodePeople : item.EpisodePeople
            };
            output ??= items.ToArray();
            output[index] = preserved;
        }
        progress?.Report(100);
        return output ?? items;
    }

    private BaseItem? Native(ManagedItem item, bool parent = false)
    {
        var type = parent ? typeof(Series) : item.Type == "movie" ? typeof(Movie) : typeof(Episode);
        return library.GetItemById(library.GetNewItemId(parent ? "siphon:series:" + item.ContentKey : "siphon:media:" + item.Key, type));
    }

    private bool SeasonLocked(ManagedItem item)
        => library.GetItemById(library.GetNewItemId("siphon:season:" + item.ContentKey + ":season:" + item.Season.GetValueOrDefault().ToString(CultureInfo.InvariantCulture), typeof(Season)))?.IsLocked == true;

    private Dictionary<string, string> LibraryLanguages()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var info in library.GetVirtualFolders())
        {
            if (!Guid.TryParse(info.ItemId, out var id) || library.GetItemById(id) is not CollectionFolder folder) continue;
            var owner = folder.GetProviderId("SiphonCatalog");
            var language = Clean(folder.GetLibraryOptions().PreferredMetadataLanguage);
            if (!string.IsNullOrWhiteSpace(owner) && language is not null) result.TryAdd(owner, language);
        }
        return result;
    }

    private bool NeedsEpisode(ManagedItem item, PluginConfiguration config, bool tmdb, bool force)
    {
        var native = Native(item);
        if (native?.IsLocked == true) return false;
        bool Needs(string field, bool missing, bool nativeMissing)
            => MetadataPolicy.Wants(config, field, force || missing) && (native is null || MetadataPolicy.CanUpdate(native, config, field, force || nativeMissing));
        return Needs("Name", string.IsNullOrWhiteSpace(item.Name), string.IsNullOrWhiteSpace(native?.Name))
            || Needs("Overview", string.IsNullOrWhiteSpace(item.Description), string.IsNullOrWhiteSpace(native?.Overview))
            || Needs("ReleaseDate", string.IsNullOrWhiteSpace(item.Released), native?.PremiereDate is null)
            || Needs("Images", string.IsNullOrWhiteSpace(item.ThumbnailUrl), native?.HasImage(ImageType.Primary) != true)
            || Needs("Runtime", !item.EpisodeRunTimeTicks.HasValue, native?.RunTimeTicks is null)
            || (tmdb && Needs("Ratings", !item.EpisodeCommunityRating.HasValue, native?.CommunityRating is null))
            || (tmdb && MetadataPolicy.Wants(config, "People", force || item.EpisodePeople.Length == 0)
                && (native is null || (!native.LockedFields.Contains(MetadataField.Cast) && Needs("People", item.EpisodePeople.Length == 0, !library.GetPeople(native).Any()))));
    }

    internal static string? Id(IReadOnlyDictionary<string, string> ids, string provider)
    {
        var value = ids.FirstOrDefault(pair => pair.Key.Equals(provider, StringComparison.OrdinalIgnoreCase)).Value?.Trim();
        if (provider == "Imdb") return value is { Length: >= 9 and <= 12 } && value.StartsWith("tt", StringComparison.Ordinal) && value.AsSpan(2).ContainsAnyExceptInRange('0', '9') == false ? value : null;
        return value is { Length: > 0 and <= 10 } && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric) && numeric > 0 ? numeric.ToString(CultureInfo.InvariantCulture) : null;
    }
    private static void AddId(Dictionary<string, string> ids, string provider, string? value)
    {
        if (value is null || ids.ContainsKey(provider)) return;
        var candidate = new Dictionary<string, string> { [provider] = value };
        if (Id(candidate, provider) is { } valid) ids.Add(provider, valid);
    }
    private static MetadataProviderClient.ProviderFailure MissingId() => new("MissingIdentifier");
    private static string Language(string? language) => language is null ? string.Empty : "&language=" + Uri.EscapeDataString(language);
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static long? Runtime(int? minutes) => minutes is > 0 and <= 10080 ? minutes.Value * TimeSpan.TicksPerMinute : null;
    private static int? Year(string? date) => date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out var year) && year is >= 1800 and <= 2200 ? year : null;
    private static string? Url(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo) ? uri.AbsoluteUri : null;
    private static string? TmdbImage(string imageBase, string? path) => path?.StartsWith('/') == true && !path.StartsWith("//", StringComparison.Ordinal) ? imageBase + "original" + path : null;
    private static string? Image(IEnumerable<FanartImage> images, string? language)
    {
        var primary = language?.Split('-')[0];
        return images.Where(image => image is not null && Url(image.Url) is not null)
            .OrderBy(image => image.Lang == primary ? 0 : string.IsNullOrWhiteSpace(image.Lang) || image.Lang == "00" ? 1 : 2)
            .ThenByDescending(image => int.TryParse(image.Likes, out var likes) ? likes : 0).Select(image => image.Url).FirstOrDefault();
    }
    private static string? TvdbLanguage(string? language)
    {
        if (language is null) return null;
        try { return CultureInfo.GetCultureInfo(language).ThreeLetterISOLanguageName; }
        catch (CultureNotFoundException) { return language.Length == 3 ? language : null; }
    }

    private static MetadataValues TmdbValues(TmdbRecord record, string imageBase, bool episode = false)
    {
        var date = episode ? record.AirDate : record.ReleaseDate ?? record.FirstAirDate;
        var cast = episode ? record.GuestStars : record.Credits?.Cast ?? [];
        var crew = episode ? record.Crew : record.Credits?.Crew ?? [];
        var people = cast.Where(person => !string.IsNullOrWhiteSpace(person?.Name)).Take(100).Select(person => new ManagedPerson(person.Name!, "Actor", person.Character, TmdbImage(imageBase, person.ProfilePath)))
            .Concat(crew.Where(person => !string.IsNullOrWhiteSpace(person?.Name) && person.Job is "Director" or "Writer" or "Screenplay" or "Producer").Take(50)
                .Select(person => new ManagedPerson(person.Name!, person.Job == "Screenplay" ? "Writer" : person.Job!, null, TmdbImage(imageBase, person.ProfilePath)))).DistinctBy(person => (person.Name, person.Type)).ToArray();
        return new()
        {
            Name = Clean(record.Title ?? record.Name),
            Overview = Clean(record.Overview),
            Released = Clean(date),
            Year = Year(date),
            Rating = record.VoteCount > 0 && record.VoteAverage is >= 0 and <= 10 ? record.VoteAverage : null,
            Runtime = Runtime(record.Runtime ?? (record.EpisodeRunTime.Length > 0 ? record.EpisodeRunTime[0] : null)),
            Poster = TmdbImage(imageBase, episode ? record.StillPath : record.PosterPath),
            Backdrop = episode ? null : TmdbImage(imageBase, record.BackdropPath),
            Genres = record.Genres.Select(genre => Clean(genre?.Name)).OfType<string>().Distinct().Take(100).ToArray(),
            Locations = record.ProductionCountries.Select(country => Clean(country?.Name)).OfType<string>().Distinct().Take(100).ToArray(),
            People = people,
            Status = record.Status switch { "Returning Series" => "Continuing", "Ended" or "Canceled" => "Ended", "Planned" or "In Production" => "Unreleased", _ => null }
        };
    }
    private static MetadataValues TvdbValues(TvdbRecord record, string? language, bool episode = false)
    {
        var lang = TvdbLanguage(language) ?? record.OriginalLanguage;
        var name = record.Translations?.NameTranslations.FirstOrDefault(value => value?.Language == lang)?.Name;
        var overview = record.Translations?.OverviewTranslations.FirstOrDefault(value => value?.Language == lang)?.Overview;
        var date = episode ? record.Aired : record.FirstAired ?? record.FirstRelease?.Date;
        return new()
        {
            Name = Clean(name) ?? Clean(record.Name),
            Overview = Clean(overview) ?? Clean(record.Overview),
            Released = Clean(date),
            Year = Year(record.Year) ?? Year(date),
            Runtime = Runtime(record.Runtime ?? record.AverageRuntime),
            Poster = Url(record.Image),
            Genres = record.Genres.Select(genre => Clean(genre?.Name)).OfType<string>().Distinct().Take(100).ToArray(),
            Status = record.Status?.Name switch { "Continuing" => "Continuing", "Ended" => "Ended", "Upcoming" => "Unreleased", _ => null }
            // TVDB score is popularity, not a user rating. Never expose it as CommunityRating.
        };
    }
    private static MetadataValues ParentValues(ManagedItem item) => new()
    {
        Name = item.Type == "series" ? item.SeriesName : item.Name,
        Overview = item.Type == "series" ? item.SeriesDescription : item.Description,
        Released = item.Type == "series" ? item.SeriesReleased : item.Released,
        Year = item.Year,
        Rating = item.CommunityRating,
        Runtime = item.RunTimeTicks,
        Poster = item.PosterUrl,
        Backdrop = item.BackdropUrl,
        Logo = item.LogoUrl,
        Genres = item.Genres,
        Locations = item.ProductionLocations,
        People = item.People,
        Status = item.SeriesStatus
    };
    internal static ManagedItem WithParent(ManagedItem item, ManagedItem parent) => item with
    {
        SeriesName = parent.SeriesName,
        SeriesDescription = parent.SeriesDescription,
        SeriesReleased = parent.SeriesReleased,
        Year = parent.Year,
        CommunityRating = parent.CommunityRating,
        RunTimeTicks = parent.RunTimeTicks,
        PosterUrl = parent.PosterUrl,
        BackdropUrl = parent.BackdropUrl,
        LogoUrl = parent.LogoUrl,
        Genres = parent.Genres,
        ProductionLocations = parent.ProductionLocations,
        People = parent.People,
        SeriesStatus = parent.SeriesStatus
    };
}
