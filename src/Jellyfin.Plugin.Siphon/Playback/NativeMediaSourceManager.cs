using System.Globalization;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Siphon.Playback;

/// <summary>Keeps Jellyfin's native source processing while restoring the addon's version order.</summary>
public sealed class NativeMediaSourceManager(IMediaSourceManager inner, ILibraryManager library, PlaybackAccess access) : IMediaSourceManager
{
    private static readonly string SourceTokenPrefix = typeof(SiphonMediaSourceProvider).FullName!
        .GetMD5().ToString("N", CultureInfo.InvariantCulture) + "_";

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User? user = null)
    {
        var sources = inner.GetStaticMediaSources(item, enablePathSubstitution, user);
        if (!NativeVersionService.IsManaged(item)) return sources;

        var video = (Video)item;
        user ??= access.CurrentUser();
        IReadOnlyList<MediaSourceInfo> selected = user is null || access.GetVideo(video.Id, user.Id) is null ? []
            : SelectSources(sources, GetActiveVersions(video, user.Id), video, user, playback: false);
        if (selected.Count > 0) return selected;
        // Native DTO generation indexes this collection before the result filter can
        // discover a user's versions. Return metadata only, never another user's source.
        return [new MediaSourceInfo
        {
            Id = item.Id.ToString("N"),
            Name = "Siphon",
            Protocol = MediaProtocol.Http,
            IsRemote = true,
            MediaStreams = [],
            RunTimeTicks = item.RunTimeTicks,
            SupportsDirectPlay = false,
            SupportsDirectStream = false,
            SupportsTranscoding = false,
            SupportsProbing = false
        }];
    }

    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
        BaseItem item,
        User? user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        CancellationToken cancellationToken)
    {
        var managed = NativeVersionService.IsManaged(item);
        user ??= access.CurrentUser();
        if (managed && (user is null || access.GetVideo(item.Id, user.Id) is null)) return [];
        var sources = await inner.GetPlaybackMediaSources(
            item, user, managed ? false : allowMediaProbe, enablePathSubstitution, cancellationToken).ConfigureAwait(false);
        if (!managed) return sources;

        // The provider materializes versions during the inner call. Read the resulting native group,
        // not the pre-call fallback, and preserve its permission/track processing and prefixed token.
        var video = library.GetItemById(item.Id) as Video ?? (Video)item;
        return SelectSources(sources, GetActiveVersions(video, user!.Id), video, user, playback: true);
    }

    public async Task<MediaSourceInfo> GetMediaSource(
        BaseItem item,
        string mediaSourceId,
        string? liveStreamId,
        bool enablePathSubstitution,
        CancellationToken cancellationToken)
    {
        if (!NativeVersionService.IsManaged(item))
        {
            return await inner.GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken).ConfigureAwait(false);
        }

        var video = library.GetItemById(item.Id) as Video ?? (Video)item;
        var user = access.CurrentUser();
        if (user is null || access.GetVideo(video.Id, user.Id) is null)
            throw new ResourceNotFoundException("The selected Siphon source is unavailable.");
        if (!Guid.TryParse(mediaSourceId, out var requestedId) || access.GetVideo(requestedId, user.Id) is not { } requested
            || requested.GetProviderId("Siphon") != video.GetProviderId("Siphon"))
            throw new ResourceNotFoundException("The selected Siphon source is unavailable.");
        if (!string.IsNullOrEmpty(liveStreamId))
            return await inner.GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken).ConfigureAwait(false);
        var sources = SelectSources(inner.GetStaticMediaSources(video, enablePathSubstitution, user),
            GetActiveVersions(video, user.Id), video, user, playback: false);
        // Opening already persisted this exact version's real tracks. Seeking, remuxing and
        // subtitle extraction must use that probed VOD source, not another unopened placeholder.
        return sources.FirstOrDefault(source => string.Equals(source.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase)
            && (!video.PrimaryVersionId.HasValue || string.Equals(source.Id, video.Id.ToString("N"), StringComparison.OrdinalIgnoreCase)))
            ?? throw new ResourceNotFoundException("The selected Siphon source is no longer available.");
    }

    private Dictionary<Guid, VersionSource> GetActiveVersions(Video item, Guid userId)
    {
        var result = new Dictionary<Guid, VersionSource>();
        var primary = item;
        var primaryMarker = item.GetProviderId(NativeVersionService.VersionProvider);
        if (!string.IsNullOrEmpty(primaryMarker) || item.PrimaryVersionId.HasValue)
        {
            if (!Guid.TryParse(primaryMarker, out var primaryId)
                || item.PrimaryVersionId != primaryId
                || library.GetItemById(primaryId) is not Video owner)
            {
                return result;
            }

            primary = owner;
        }

        if (!SameManagedContent(primary, item)
            || primary.PrimaryVersionId.HasValue
            || !string.IsNullOrEmpty(primary.GetProviderId(NativeVersionService.VersionProvider)))
        {
            return result;
        }

        if (primary.GetProviderId(NativeVersionService.OwnerProvider) == userId.ToString("N")) AddActiveVersion(primary, result);
        var belongsToGroup = item.Id == primary.Id;
        foreach (var alternate in library.GetLinkedAlternateVersions(primary))
        {
            if (!SameManagedContent(alternate, primary)
                || alternate.PrimaryVersionId != primary.Id
                || !Guid.TryParse(alternate.GetProviderId(NativeVersionService.VersionProvider), out var ownerId)
                || ownerId != primary.Id)
            {
                continue;
            }

            belongsToGroup |= alternate.Id == item.Id;
            if (alternate.GetProviderId(NativeVersionService.OwnerProvider) == userId.ToString("N")) AddActiveVersion(alternate, result);
        }

        if (!belongsToGroup) result.Clear();
        return result;
    }

    private static bool SameManagedContent(Video candidate, Video item) =>
        NativeVersionService.IsManaged(candidate)
        && candidate.GetType() == item.GetType()
        && string.Equals(candidate.GetProviderId("Siphon"), item.GetProviderId("Siphon"), StringComparison.Ordinal);

    private static void AddActiveVersion(Video version, Dictionary<Guid, VersionSource> versions)
    {
        if (!string.IsNullOrWhiteSpace(version.GetProviderId(NativeVersionService.SourceProvider))
            && int.TryParse(version.GetProviderId(NativeVersionService.SourceOrderProvider), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rank)
            && rank >= 0)
        {
            versions[version.Id] = new VersionSource(rank, version.GetProviderId(NativeVersionService.SourceNameProvider) ?? string.Empty);
        }
    }

    private IReadOnlyList<MediaSourceInfo> SelectSources(
        IReadOnlyList<MediaSourceInfo> sources,
        Dictionary<Guid, VersionSource> versions,
        Video item,
        User? user,
        bool playback)
    {
        var selected = new List<(MediaSourceInfo Source, int Rank)>();
        foreach (var source in sources)
        {
            if (!Guid.TryParse(source.Id, out var versionId) || !versions.TryGetValue(versionId, out var version)) continue;
            if (playback)
            {
                if (!source.RequiresOpening
                    || source.OpenToken is null
                    || !source.OpenToken.StartsWith(SourceTokenPrefix, StringComparison.OrdinalIgnoreCase)
                    || (item.PrimaryVersionId.HasValue && versionId != item.Id))
                {
                    continue;
                }

                // Inner filters static alternate versions per user, but dynamic provider results
                // receive only track/transcoding defaults. Apply the same native visibility check.
                if (user is not null && versionId != item.Id && library.GetItemById<Video>(versionId, user) is null) continue;
            }

            source.Name = version.Name;
            selected.Add((source, version.Rank));
        }

        return selected.OrderBy(entry => entry.Rank).Select(entry => entry.Source).ToArray();
    }

    private readonly record struct VersionSource(int Rank, string Name);

    public void AddParts(IEnumerable<IMediaSourceProvider> providers) => inner.AddParts(providers);

    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId) => inner.GetMediaStreams(itemId);

    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query) => inner.GetMediaStreams(query);

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) => inner.GetMediaAttachments(itemId);

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) => inner.GetMediaAttachments(query);

    public async Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken)
    {
        var response = await inner.OpenLiveStream(request, cancellationToken).ConfigureAwait(false);
        await CompleteVodOpenAsync(request, response).ConfigureAwait(false);
        return response;
    }

    public async Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken cancellationToken)
    {
        var response = await inner.OpenLiveStreamInternal(request, cancellationToken).ConfigureAwait(false);
        await CompleteVodOpenAsync(request, response.Item1).ConfigureAwait(false);
        return response;
    }

    private async Task CompleteVodOpenAsync(LiveStreamRequest request, LiveStreamResponse response)
    {
        if (request.OpenToken?.StartsWith(SourceTokenPrefix, StringComparison.OrdinalIgnoreCase) != true) return;
        var source = response.MediaSource;
        if (!string.IsNullOrEmpty(source.LiveStreamId))
            await inner.CloseLiveStream(source.LiveStreamId).ConfigureAwait(false);
        // This is finite HTTP media, not a tuner. Its opaque proxy lease outlives individual
        // readers. A native live-stream ID would be closed on an audio switch and then reused.
        source.LiveStreamId = null;
        source.RequiresClosing = false;
    }

    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken) => inner.GetLiveStream(id, cancellationToken);

    public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken) =>
        inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);

    public ILiveStream GetLiveStreamInfo(string id) => inner.GetLiveStreamInfo(id);

    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) => inner.GetLiveStreamInfoByUniqueId(uniqueId);

    public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(ActiveRecordingInfo info, CancellationToken cancellationToken) =>
        inner.GetRecordingStreamMediaSources(info, cancellationToken);

    public Task CloseLiveStream(string id) => inner.CloseLiveStream(id);

    public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken) => inner.GetLiveStreamMediaInfo(id, cancellationToken);

    public bool SupportsDirectStream(string path, MediaProtocol protocol) => inner.SupportsDirectStream(path, protocol);

    public MediaProtocol GetPathProtocol(string path) => inner.GetPathProtocol(path);

    public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user) =>
        inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);

    public Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken cancellationToken) =>
        inner.AddMediaInfoWithProbe(mediaSource, isAudio, cacheKey, addProbeDelay, isLiveStream, cancellationToken);
}
