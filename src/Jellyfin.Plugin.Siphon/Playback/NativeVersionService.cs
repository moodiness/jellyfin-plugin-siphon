using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Siphon.Playback;

/// <summary>Materializes a title's streams as native alternate versions, without changing its canonical identity.</summary>
public sealed class NativeVersionService(
    ConfigurationAccessor configuration,
    CapabilityTokenService tokens,
    ISiphonStateStore state,
    ILibraryManager library,
    IItemPersistenceService persistence,
    IMediaStreamRepository mediaStreams,
    ItemMetadataMapper metadata,
    StreamResolver resolver,
    PlaybackAccess access,
    Subtitles.ManagedSubtitleStore? subtitles = null)
{
    public const string SourceProvider = "SiphonSource";
    public const string VersionProvider = "SiphonVersion";
    public const string SourceNameProvider = "SiphonSourceName";
    public const string SourceOrderProvider = "SiphonSourceOrder";
    public const string OwnerProvider = "SiphonSourceOwner";
    private const string ItemProvider = "Siphon";

    public static bool IsManaged(BaseItem item)
        => item is Movie or Episode && !string.IsNullOrEmpty(item.GetProviderId(ItemProvider));

    public async Task<IReadOnlyList<NativeStreamVersion>> GetVersionsAsync(Video item, Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty || access.GetVideo(item.Id, userId) is null || !IsManaged(item) || item.GetProviderId(ItemProvider) is not { } itemKey
            || state.FindByKey(itemKey) is not { } requested) return [];

        // Addon I/O must never hold the catalog/configuration mutation gate.
        var settings = configuration.Current;
        var streams = await resolver.GetSourcesAsync(requested, userId, ct).ConfigureAwait(false);
        await configuration.MutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var managed = state.FindByKey(requested.Key);
            if (managed is null || !ReferenceEquals(settings, configuration.Current)
                || managed.Type != requested.Type || managed.VideoId != requested.VideoId
                || !managed.StreamIdentities.SequenceEqual(requested.StreamIdentities)
                || string.IsNullOrWhiteSpace(configuration.Current.PublicBaseUrl)) return [];

            // Re-read after acquiring the gate: a catalog move/removal may have happened during I/O.
            var existing = LoadVersions(managed);
            var primary = FindPrimary(item, existing);
            if (primary is null) return [];
            var primaryId = primary.Id.ToString("N");
            var versions = existing.Where(version => version.Id == primary.Id
                || version.GetProviderId(OwnerProvider) == userId.ToString("N")).ToList();
            var changed = new HashSet<Video>();
            var created = new List<Video>();

            if (primary.PrimaryVersionId.HasValue)
            {
                primary.SetPrimaryVersionId(null);
                changed.Add(primary);
            }
            if (SetMarker(primary, VersionProvider, null)) changed.Add(primary);
            if (string.IsNullOrEmpty(primary.PresentationUniqueKey))
            {
                primary.PresentationUniqueKey = primary.CreatePresentationUniqueKey();
                changed.Add(primary);
            }

            // Canonical identity must never inherit one user's provider or credential-bearing source.
            if (SetMarker(primary, SourceProvider, null) | SetMarker(primary, SourceNameProvider, null)
                | SetMarker(primary, SourceOrderProvider, null) | SetMarker(primary, OwnerProvider, null))
            {
                primary.Path = configuration.Current.PublicBaseUrl.TrimEnd('/') + "/Siphon/s/" + tokens.SignItem(managed.Key);
                changed.Add(primary);
            }

            var bySource = new Dictionary<string, Video>(StringComparer.Ordinal);
            // The original canonical item always wins if recovering an interrupted earlier write.
            var primarySource = primary.GetProviderId(SourceProvider);
            if (!string.IsNullOrEmpty(primarySource)) bySource.Add(primarySource, primary);
            foreach (var version in versions)
            {
                var sourceId = version.GetProviderId(SourceProvider);
                if (!string.IsNullOrEmpty(sourceId)) bySource.TryAdd(sourceId, version);
            }

            var result = new List<NativeStreamVersion>(streams.Count);
            var active = new HashSet<Guid>();
            for (var index = 0; index < streams.Count; index++)
            {
                var stream = streams[index];
                if (!bySource.TryGetValue(stream.Id, out var version))
                {
                    var type = primary is Movie ? typeof(Movie) : typeof(Episode);
                    var id = library.GetNewItemId("siphon:version:" + managed.Key + ":" + stream.Id, type);
                    // Never replace an existing native item at a deterministic identity.
                    var occupant = library.GetItemById(id);
                    if (occupant is not null)
                    {
                        if (occupant is not Video found || found.GetType() != type
                            || found.GetProviderId(ItemProvider) != managed.Key
                            || found.GetProviderId(SourceProvider) != stream.Id)
                            throw new InvalidOperationException("A Siphon version identity is already occupied by another item.");
                        version = found;
                    }
                    else
                    {
                        version = primary is Movie ? new Movie { Id = id } : new Episode { Id = id };
                        version.SetProviderId(SourceProvider, stream.Id);
                        version.SetProviderId(OwnerProvider, userId.ToString("N"));
                        ApplyMetadata(version, primary, managed);
                        created.Add(version);
                    }
                    versions.Add(version);
                    bySource.Add(stream.Id, version);
                }

                if (SetMarker(version, SourceNameProvider, stream.Name)
                    | SetMarker(version, SourceOrderProvider, index.ToString(CultureInfo.InvariantCulture))) changed.Add(version);
                active.Add(version.Id);
                result.Add(new NativeStreamVersion(version, stream));
            }

            foreach (var version in versions)
            {
                if (version.Id != primary.Id && !active.Contains(version.Id) && SetMarker(version, SourceOrderProvider, "-1")) changed.Add(version);
                if (version.Id != primary.Id)
                {
                    if (SetMarker(version, VersionProvider, primaryId)) changed.Add(version);
                    if (version.PrimaryVersionId != primary.Id)
                    {
                        version.SetPrimaryVersionId(primary.Id);
                        changed.Add(version);
                    }
                    if (!SamePlacement(version, primary))
                    {
                        ApplyMetadata(version, primary, managed);
                        changed.Add(version);
                    }
                }
                var sourceId = version.GetProviderId(SourceProvider);
                if (!string.IsNullOrEmpty(sourceId))
                {
                    var path = SourcePath(managed.Key, sourceId);
                    if (version.Path != path)
                    {
                        version.Path = path;
                        changed.Add(version);
                    }
                }
            }

            // Read persisted relationships, not only the possibly unloaded in-memory array.
            // Retired siblings remain linked, keeping both their native identity and user data.
            var linked = library.GetLinkedAlternateVersions(primary).ToDictionary(version => version.Id);
            var missingLinks = versions.Where(version => version.Id != primary.Id && !linked.ContainsKey(version.Id)).ToArray();
            foreach (var version in missingLinks) linked.Add(version.Id, version);
            var links = linked.Keys.Order().Select(id => new LinkedChild
            {
                ItemId = id,
                Type = LinkedChildType.LinkedAlternateVersion
            }).ToArray();
            if (!primary.LinkedAlternateVersions.Select(link => link.ItemId).Order()
                .SequenceEqual(links.Select(link => link.ItemId)))
            {
                primary.LinkedAlternateVersions = links;
                if (missingLinks.Length > 0) changed.Add(primary);
            }

            if (created.Count > 0)
            {
                library.CreateItems(created, null, ct);
                foreach (var version in created) changed.Remove(version);
            }
            if (changed.Count > 0) Save(changed.ToArray(), ct);
            // This is the native relationship API: assigning LinkedAlternateVersions alone does
            // not guarantee that Jellyfin's persisted GetAllVersions lookup can see the siblings.
            foreach (var version in missingLinks)
                library.UpsertLinkedChild(primary.Id, version.Id, LinkedChildType.LinkedAlternateVersion);
            await metadata.SavePeopleAsync(created.Select(version => ((BaseItem)version, managed, true)).ToArray(), ct).ConfigureAwait(false);
            if ((created.Count > 0 || changed.Count > 0 || missingLinks.Length > 0) && primary.GetParent() is Folder parent)
                parent.Children = null;
            return result;
        }
        finally
        {
            configuration.MutationGate.Release();
        }
    }

    /// <summary>Finds even a retired version without resolving streams or creating a native item.</summary>
    public Guid FindVersionId(ManagedItem managed, string sourceId)
        => LoadVersions(managed).FirstOrDefault(version => version.GetProviderId(SourceProvider) == sourceId)?.Id ?? Guid.Empty;

    /// <summary>Retains the selected cut's real runtime and tracks for native resume and detail views.</summary>
    public async Task SaveMediaInfoAsync(Guid versionId, MediaSourceInfo info, CancellationToken ct)
    {
        await configuration.MutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (library.GetItemById(versionId) is not Video version || !IsManaged(version)) return;
            // Probing the upstream file cannot see subtitles/audio saved alongside this native
            // version. Keep those tracks in both the opened source and the persisted stream list.
            var external = subtitles is null
                ? mediaStreams.GetMediaStreams(new MediaStreamQuery { ItemId = versionId }).Where(stream => stream.IsExternal).ToArray()
                : (await subtitles.ReadAsync(version, ct).ConfigureAwait(false)).ToArray();
            if (external.Length > 0)
            {
                var merged = new List<MediaStream>(info.MediaStreams.Count + external.Length);
                merged.AddRange(info.MediaStreams);
                var usedIndices = info.MediaStreams.Select(stream => stream.Index).ToHashSet();
                var nextIndex = Math.Max(info.MediaStreams.Select(stream => stream.Index).DefaultIfEmpty(-1).Max(),
                    external.Select(stream => stream.Index).Max()) + 1;
                foreach (var stream in external)
                {
                    if (merged.Any(current => current.IsExternal && current.Type == stream.Type
                        && string.Equals(current.Path, stream.Path, StringComparison.Ordinal))) continue;
                    // Never renumber embedded streams: ffmpeg uses their actual container indices.
                    if (stream.Index < 0 || !usedIndices.Add(stream.Index))
                    {
                        stream.Index = nextIndex++;
                        usedIndices.Add(stream.Index);
                    }
                    stream.SupportsExternalStream = true;
                    merged.Add(stream);
                }
                info.MediaStreams = merged;
            }
            version.Container = info.Container;
            version.Size = info.Size;
            if (info.RunTimeTicks is > 0) version.RunTimeTicks = info.RunTimeTicks;
            version.DefaultVideoStreamIndex = info.MediaStreams.FirstOrDefault(stream => stream.Type == MediaStreamType.Video)?.Index;
            version.HasSubtitles = info.MediaStreams.Any(stream => stream.Type == MediaStreamType.Subtitle);
            mediaStreams.SaveMediaStreams(versionId, info.MediaStreams, ct);
            Save([version], ct);
        }
        finally
        {
            configuration.MutationGate.Release();
        }
    }

    /// <summary>Applies catalog metadata under the caller's gate; does not save or resolve streams.</summary>
    public void ApplyMetadata(Video version, Video primary, ManagedItem managed)
    {
        var sourceId = version.GetProviderId(SourceProvider);
        var sourceName = version.GetProviderId(SourceNameProvider);
        var sourceOrder = version.GetProviderId(SourceOrderProvider);
        var sourceOwner = version.GetProviderId(OwnerProvider);
        metadata.Apply(version, managed, managed.Key, version is Movie ? managed.ProviderIds : managed.EpisodeProviderIds);
        SetMarker(version, SourceProvider, sourceId);
        SetMarker(version, SourceNameProvider, sourceName);
        SetMarker(version, SourceOrderProvider, sourceOrder);
        SetMarker(version, OwnerProvider, sourceOwner);
        if (!string.IsNullOrEmpty(sourceId)) version.Path = SourcePath(managed.Key, sourceId);
        version.SetParent(primary.GetParent() as Folder
            ?? throw new InvalidOperationException("The Siphon title no longer has a library parent."));
        version.ParentId = primary.ParentId;
        if (version is Episode episode && primary is Episode original)
        {
            episode.IndexNumber = original.IndexNumber;
            episode.IndexNumberEnd = original.IndexNumberEnd;
            episode.ParentIndexNumber = original.ParentIndexNumber;
            episode.SeriesId = original.SeriesId;
            episode.SeriesName = original.SeriesName;
            episode.SeriesPresentationUniqueKey = original.SeriesPresentationUniqueKey;
            episode.SeasonId = original.SeasonId;
            episode.SeasonName = original.SeasonName;
            episode.AirsBeforeSeasonNumber = original.AirsBeforeSeasonNumber;
            episode.AirsAfterSeasonNumber = original.AirsAfterSeasonNumber;
            episode.AirsBeforeEpisodeNumber = original.AirsBeforeEpisodeNumber;
        }
        if (version.Id != primary.Id)
        {
            version.SetProviderId(VersionProvider, primary.Id.ToString("N"));
            version.SetPrimaryVersionId(primary.Id);
            version.PresentationUniqueKey = primary.PresentationUniqueKey;
        }
        else
        {
            SetMarker(version, VersionProvider, null);
        }
    }

    private List<Video> LoadVersions(ManagedItem managed)
        => library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [managed.Type == "movie" ? BaseItemKind.Movie : BaseItemKind.Episode],
            HasAnyProviderId = new Dictionary<string, string> { [ItemProvider] = managed.Key },
            IncludeAlternateVersions = true,
            GroupByPresentationUniqueKey = false,
            EnableTotalRecordCount = false
        }).OfType<Video>().Where(version => IsManaged(version) && version.GetProviderId(ItemProvider) == managed.Key).ToList();

    private static Video? FindPrimary(Video requested, IReadOnlyList<Video> existing)
    {
        var current = existing.FirstOrDefault(version => version.Id == requested.Id);
        if (current is null) return null;
        if (Guid.TryParse(current.GetProviderId(VersionProvider), out var markedId)
            && existing.FirstOrDefault(version => version.Id == markedId) is { } marked) return marked;
        if (current.PrimaryVersionId is { } primaryId
            && existing.FirstOrDefault(version => version.Id == primaryId) is { } primary) return primary;
        if (string.IsNullOrEmpty(current.GetProviderId(VersionProvider)) && !current.PrimaryVersionId.HasValue) return current;
        return existing.FirstOrDefault(version => string.IsNullOrEmpty(version.GetProviderId(VersionProvider)) && !version.PrimaryVersionId.HasValue);
    }

    private static bool SamePlacement(Video version, Video primary)
        => version.ParentId == primary.ParentId && version.PresentationUniqueKey == primary.PresentationUniqueKey
            && (version is not Episode episode || primary is Episode original
                && episode.IndexNumber == original.IndexNumber && episode.IndexNumberEnd == original.IndexNumberEnd
                && episode.ParentIndexNumber == original.ParentIndexNumber && episode.SeriesId == original.SeriesId
                && episode.SeriesName == original.SeriesName && episode.SeriesPresentationUniqueKey == original.SeriesPresentationUniqueKey
                && episode.SeasonId == original.SeasonId && episode.SeasonName == original.SeasonName
                && episode.AirsBeforeSeasonNumber == original.AirsBeforeSeasonNumber
                && episode.AirsAfterSeasonNumber == original.AirsAfterSeasonNumber
                && episode.AirsBeforeEpisodeNumber == original.AirsBeforeEpisodeNumber);

    private string SourcePath(string key, string sourceId)
        => configuration.Current.PublicBaseUrl.TrimEnd('/') + "/Siphon/source/" + tokens.SignSource(key, sourceId);

    private static bool SetMarker(Video item, string provider, string? value)
    {
        if (item.GetProviderId(provider) == value) return false;
        if (value is null) item.ProviderIds.Remove(provider);
        else item.SetProviderId(provider, value);
        return true;
    }

    private void Save(IReadOnlyList<BaseItem> items, CancellationToken ct)
    {
        foreach (var item in items) item.DateLastSaved = DateTime.UtcNow;
        // Match catalog persistence: never clear BaseItem.UserData or download credits on reads.
        persistence.SaveItems(items, ct);
        foreach (var item in items) library.RegisterItem(item);
    }
}

public sealed record NativeStreamVersion(Video Item, ResolvedStream Stream);
