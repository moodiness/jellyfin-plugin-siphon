using System.Globalization;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Siphon.Playback;

/// <summary>Exposes opaque proxy sources for native Siphon library items.</summary>
public sealed class SiphonMediaSourceProvider(
    ISiphonStateStore state,
    NativeVersionService versions,
    StreamResolver resolver,
    ProxySessionStore sessions,
    CapabilityTokenService tokens,
    Configuration.ConfigurationAccessor configuration,
    IMediaEncoder encoder) : IMediaSourceProvider
{
    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is not Video video || !NativeVersionService.IsManaged(video)
            || video.GetProviderId("Siphon") is not { } itemKey || state.FindByKey(itemKey) is not { } managed)
        {
            return [];
        }

        var available = await versions.GetVersionsAsync(video, cancellationToken).ConfigureAwait(false);
        if (available.Count == 0) return [];
        // Remuxing resolves this provider again without opening another live stream. Preserve
        // the selected version's probed tracks so Jellyfin can map its actual audio/subtitle indices.
        var nativeSources = available[0].Item.GetMediaSources(false)
            .ToDictionary(source => source.Id, StringComparer.OrdinalIgnoreCase);
        var selectedVersions = string.IsNullOrEmpty(video.GetProviderId(NativeVersionService.VersionProvider))
            ? available
            : available.Where(version => version.Item.Id == video.Id);
        return selectedVersions.Select(version =>
        {
            var stream = version.Stream;
            var token = tokens.SignSource(managed.Key, stream.Id);
            var source = nativeSources[version.Item.Id.ToString("N", CultureInfo.InvariantCulture)];
            source.Name = stream.Name;
            source.Path = configuration.Current.PublicBaseUrl.TrimEnd('/') + "/Siphon/source/" + token;
            source.Protocol = MediaProtocol.Http;
            source.IsRemote = true;
            source.Size ??= stream.Size;
            source.Type = MediaSourceType.Default;
            source.VideoType = VideoType.VideoFile;
            source.RequiresOpening = true;
            source.OpenToken = token;
            source.RequiresClosing = true;
            source.SupportsProbing = true;
            source.SupportsDirectPlay = false;
            source.SupportsDirectStream = false;
            source.SupportsTranscoding = true;
            return source;
        }).ToArray();
    }

    public async Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        if (!tokens.TryReadSource(openToken, out var itemKey, out var sourceId) || state.FindByKey(itemKey) is not { } item)
        {
            throw new InvalidOperationException("The Siphon item is no longer available.");
        }

        var streams = await resolver.GetSourcesAsync(item, cancellationToken).ConfigureAwait(false);
        var selected = streams.FirstOrDefault(stream => stream.Id == sourceId)
            ?? throw new InvalidOperationException("The selected source is no longer available. Reload the item.");
        var versionId = versions.FindVersionId(item, sourceId);
        if (versionId == Guid.Empty) throw new InvalidOperationException("The selected version is no longer available. Reload the item.");
        var session = sessions.Create(item, selected);

        var source = new MediaSourceInfo
        {
            Id = versionId.ToString("N", CultureInfo.InvariantCulture),
            Name = session.Source.Name,
            Path = sessions.GetUrl(session),
            Protocol = MediaProtocol.Http,
            IsRemote = true,
            Type = MediaSourceType.Default,
            VideoType = MediaBrowser.Model.Entities.VideoType.VideoFile,
            RequiresOpening = false,
            RequiresClosing = true,
            LiveStreamId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            SupportsProbing = true
        };
        // Probe the selected variant, never infer codecs or track indices from a filename.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var info = await encoder.GetMediaInfo(new MediaInfoRequest
        {
            MediaSource = source,
            MediaType = DlnaProfileType.Video,
            ExtractChapters = false
        }, deadline.Token).ConfigureAwait(false);
        source.Container = info.Container;
        source.Formats = info.Formats;
        source.Bitrate = info.Container == "hls" ? null : info.Bitrate;
        source.Size = info.Container == "hls" ? session.Source.Size : info.Size ?? session.Source.Size;
        source.RunTimeTicks = info.RunTimeTicks;
        source.MediaStreams = info.MediaStreams;
        source.MediaAttachments = info.MediaAttachments;
        source.SupportsProbing = false;
        await versions.SaveMediaInfoAsync(versionId, source, cancellationToken).ConfigureAwait(false);
        return new ProxyLiveStream(source);
    }

    /// <summary>Jellyfin owns playback; the proxy lease is shared safely across users and probes.</summary>
    private sealed class ProxyLiveStream(MediaSourceInfo source) : ILiveStream
    {
        public int ConsumerCount { get; set; } = 1;
        public string OriginalStreamId { get; set; } = source.Id;
        public string TunerHostId => string.Empty;
        public bool EnableStreamSharing => false;
        public MediaSourceInfo MediaSource { get; set; } = source;
        public string UniqueId { get; } = source.LiveStreamId;
        public Task Open(CancellationToken openCancellationToken)
        {
            openCancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        // This lease owns no tuner or stream: HTTP request lifetimes own every upstream connection.
        public Task Close() => Task.CompletedTask;
        public Stream GetStream() => throw new NotSupportedException("Siphon is accessed through its HTTP proxy, not a tuner byte stream.");
        public void Dispose() { }
    }
}
