using Jellyfin.Plugin.Siphon.Playback;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Siphon.Subtitles;

/// <summary>Uses Jellyfin's subtitle validation and writer, with per-version local storage for remote media.</summary>
public sealed class NativeSubtitleManager(ISubtitleManager inner, ManagedSubtitleStore storage) : ISubtitleManager
{
    public event EventHandler<SubtitleDownloadFailureEventArgs> SubtitleDownloadFailure
    {
        add => inner.SubtitleDownloadFailure += value;
        remove => inner.SubtitleDownloadFailure -= value;
    }

    public Task<RemoteSubtitleInfo[]> SearchSubtitles(Video video, string language, bool? isPerfectMatch, bool isAutomated, CancellationToken cancellationToken)
        => inner.SearchSubtitles(video, language, isPerfectMatch, isAutomated, cancellationToken);
    public Task<RemoteSubtitleInfo[]> SearchSubtitles(SubtitleSearchRequest request, CancellationToken cancellationToken)
        => inner.SearchSubtitles(request, cancellationToken);
    public Task<SubtitleResponse> GetRemoteSubtitles(string id, CancellationToken cancellationToken) => inner.GetRemoteSubtitles(id, cancellationToken);
    public SubtitleProviderInfo[] GetSupportedProviders(BaseItem item) => inner.GetSupportedProviders(item);

    public async Task DownloadSubtitles(Video video, string subtitleId, CancellationToken cancellationToken)
    {
        await inner.DownloadSubtitles(StorageItem(video), subtitleId, cancellationToken).ConfigureAwait(false);
        if (NativeVersionService.IsManaged(video)) await storage.RefreshAsync(video, cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadSubtitles(Video video, LibraryOptions libraryOptions, string subtitleId, CancellationToken cancellationToken)
    {
        await inner.DownloadSubtitles(StorageItem(video), libraryOptions, subtitleId, cancellationToken).ConfigureAwait(false);
        if (NativeVersionService.IsManaged(video)) await storage.RefreshAsync(video, cancellationToken).ConfigureAwait(false);
    }

    public async Task UploadSubtitle(Video video, SubtitleResponse response)
    {
        await inner.UploadSubtitle(StorageItem(video), response).ConfigureAwait(false);
        if (NativeVersionService.IsManaged(video)) await storage.RefreshAsync(video, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task DeleteSubtitles(BaseItem item, int index)
    {
        await inner.DeleteSubtitles(item, index).ConfigureAwait(false);
        if (item is Video video && NativeVersionService.IsManaged(video))
            await storage.RefreshAsync(video, CancellationToken.None).ConfigureAwait(false);
    }

    private static Video StorageItem(Video video) => !NativeVersionService.IsManaged(video) ? video : new SubtitleStorageView(video)
    {
        Id = video.Id,
        ParentId = video.ParentId,
        Name = video.Name,
        // The view is never registered or persisted. Both native save-location policies resolve
        // to this version's metadata directory, without changing the library or playable path.
        Path = System.IO.Path.Combine(video.GetInternalMetadataPath(), video.Id.ToString("N") + ".strm")
    };

    private sealed class SubtitleStorageView(Video original) : Video
    {
        public override string GetInternalMetadataPath() => original.GetInternalMetadataPath();
    }
}

/// <summary>Restores locally saved subtitles that native file-only discovery skips for HTTP items.</summary>
public sealed class ManagedSubtitleStore(IMediaStreamRepository repository, IMediaEncoder encoder,
    IItemPersistenceService persistence, ILogger<ManagedSubtitleStore> logger, Configuration.ConfigurationAccessor configuration)
{
    public async Task<IReadOnlyList<MediaStream>> ReadAsync(Video video, CancellationToken cancellationToken)
    {
        var existing = repository.GetMediaStreams(new MediaStreamQuery { ItemId = video.Id });
        var directory = video.GetInternalMetadataPath();
        var prefix = video.Id.ToString("N") + ".";
        var external = existing.Where(stream => stream.IsExternal && (!Owned(stream.Path, directory, prefix) || File.Exists(stream.Path))).ToList();
        if (!Directory.Exists(directory)) return external;
        var known = external.Where(stream => !string.IsNullOrEmpty(stream.Path)).Select(stream => stream.Path).ToHashSet(StringComparer.Ordinal);
        var nextIndex = existing.Select(stream => stream.Index).DefaultIfEmpty(-1).Max() + 1;
        foreach (var path in Directory.EnumerateFiles(directory, prefix + "*").Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (known.Contains(path)) continue;
            var fields = System.IO.Path.GetFileName(path).Split('.');
            if (fields.Length < 3 || fields[1].Length == 0) continue;
            try
            {
                var info = await encoder.GetMediaInfo(new MediaInfoRequest
                {
                    MediaType = DlnaProfileType.Subtitle,
                    MediaSource = new MediaSourceInfo { Path = path, Protocol = MediaProtocol.File }
                }, cancellationToken).ConfigureAwait(false);
                foreach (var stream in info.MediaStreams.Where(stream => stream.Type == MediaStreamType.Subtitle))
                {
                    stream.Path = path;
                    stream.Index = nextIndex++;
                    stream.Language = fields[1];
                    stream.IsExternal = true;
                    stream.SupportsExternalStream = true;
                    stream.IsForced |= fields.Contains("forced", StringComparer.OrdinalIgnoreCase);
                    stream.IsHearingImpaired |= fields.Contains("sdh", StringComparer.OrdinalIgnoreCase);
                    external.Add(stream);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Match native sidecar discovery: one malformed subtitle must not prevent playback.
                logger.LogWarning("A saved Siphon subtitle could not be probed; other tracks remain available.");
            }
        }
        return external;
    }

    public async Task RefreshAsync(Video video, CancellationToken cancellationToken)
    {
        await configuration.MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var external = await ReadAsync(video, cancellationToken).ConfigureAwait(false);
            var embedded = repository.GetMediaStreams(new MediaStreamQuery { ItemId = video.Id }).Where(stream => !stream.IsExternal);
            var streams = embedded.Concat(external).ToArray();
            repository.SaveMediaStreams(video.Id, streams, cancellationToken);
            video.HasSubtitles = streams.Any(stream => stream.Type == MediaStreamType.Subtitle);
            persistence.SaveItems([video], cancellationToken);
        }
        finally
        {
            configuration.MutationGate.Release();
        }
    }

    private static bool Owned(string? path, string directory, string prefix)
        => path is not null && string.Equals(System.IO.Path.GetDirectoryName(path), directory, StringComparison.Ordinal)
            && System.IO.Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal);
}
