using System.Net;
using System.Text;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Playback;

namespace Jellyfin.Plugin.Siphon.Downloads;

internal sealed class DownloadSelectionException(string message) : IOException(message) { }

/// <summary>Only checked playlist metadata is fetched; segment, key and peer bodies are never prepared.</summary>
internal sealed class DownloadPreparationService(ISafeHttpClient http)
{
    private const string RestartWarning = "Finite HLS is staged again after pause, restart or failure; incomplete Matroska output is never appended.";
    private const string FormatWarning = "Offline HLS requires video, compatible copy-mode codecs and configured FFmpeg/FFprobe. Audio-only HLS, live streams and DRM are not supported; choose a progressive source instead.";

    internal async Task<DownloadPreparation> PrepareAsync(ResolvedStream source, CancellationToken ct)
    {
        if (source.P2p is not null) return new("P2p", [], [], Positive(source.Size), Positive(source.Size),
            ["Peer exports retain the complete selected file. Track filtering is not supported."]);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var root = await FetchAsync(source, source.Url, allowProgressive: true, deadline.Token).ConfigureAwait(false);
        if (root.Manifest is null) return new("Progressive", [], [], root.Bytes ?? Positive(source.Size), root.Bytes ?? Positive(source.Size),
            ["Progressive exports retain the complete selected file. Track filtering is not supported. Resume requires an unchanged strong ETag and exact byte ranges."]);
        var manifest = root.Manifest;
        var media = new List<HlsOfflineManifest>();
        string[] audio = [], subtitles = [];
        var warnings = new List<string> { RestartWarning, FormatWarning,
            "Language choices apply to advertised renditions. Null retains all; an empty list removes separate tracks. Embedded closed captions cannot be removed without conversion.",
            "Temporary storage includes downloaded resources, audio preparation and the final container; estimates are not a storage guarantee." };
        long bandwidth = 0;
        if (manifest.IsMaster)
        {
            var selected = SelectVariant(manifest);
            var groups = SelectRenditions(manifest, selected, new());
            audio = Languages(groups.Audio);
            subtitles = Languages(groups.Subtitles);
            if (groups.Audio.Concat(groups.Subtitles).Any(value => value.Language is null))
                warnings.Add("Some renditions have no advertised language; retain all to include unnamed tracks.");
            if (groups.Audio.Any(value => value.Url is null))
                warnings.Add("Embedded audio cannot be isolated by advertised rendition; use all audio or no audio.");
            if (manifest.HasClosedCaptions) warnings.Add("Embedded closed captions are retained in the video bitstream; subtitle filtering is unavailable for this source.");
            var urls = new[] { selected.Url }.Concat(groups.Audio.Concat(groups.Subtitles).Where(value => value.Url is not null).Select(value => value.Url!)).Distinct().ToArray();
            if (urls.Length + 1 > HlsOfflineManifest.MaximumPlaylists) throw new DownloadSelectionException("The selected HLS variant exceeds the playlist preparation limit.");
            foreach (var url in urls)
            {
                var fetched = await FetchAsync(source, url, allowProgressive: false, deadline.Token).ConfigureAwait(false);
                if (fetched.Manifest is not { IsMaster: false } child) throw new DownloadSelectionException("Nested HLS master playlists are not supported. Choose a direct finite HLS version.");
                media.Add(child);
            }
            bandwidth = selected.Bandwidth;
        }
        else
        {
            media.Add(manifest);
            warnings.Add("This media playlist does not advertise track languages. Retain all tracks or remove a track type; named selection requires a master playlist.");
        }
        var resources = media.SelectMany(value => value.Entries).Where(value => value.Url is not null)
            .DistinctBy(value => (value.Url, value.Range, value.Kind)).ToArray();
        if (resources.Length > HlsOfflineManifest.MaximumResources) throw new DownloadSelectionException("The HLS source exceeds the resource preparation limit.");
        long? estimated = resources.Length > 0 && resources.All(value => value.Range is not null)
            ? resources.Sum(value => value.Range!.Length) : null;
        if (estimated is null && bandwidth > 0)
        {
            var guess = media.Max(value => value.DurationSeconds) * bandwidth / 8d;
            if (double.IsFinite(guess) && guess > 0 && guess <= long.MaxValue / 4d) estimated = (long)Math.Ceiling(guess);
        }
        long? temporary = estimated is > 0 && estimated <= (long.MaxValue - 4 * 1024 * 1024) / 3
            ? estimated * 3 + 4 * 1024 * 1024 : null;
        return new("Hls", audio, subtitles, estimated, temporary, warnings.ToArray());
    }

    internal static HlsVariant SelectVariant(HlsOfflineManifest manifest)
        => manifest.Variants.OrderByDescending(value => value.HasVideo).ThenByDescending(value => value.Bandwidth).First();

    internal static (HlsRendition[] Audio, HlsRendition[] Subtitles) SelectRenditions(HlsOfflineManifest manifest,
        HlsVariant selected, DownloadTransferOptions options)
    {
        var audio = manifest.Renditions.Where(value => value.Type == "AUDIO" && value.Group == selected.Audio).ToArray();
        var subtitles = manifest.Renditions.Where(value => value.Type == "SUBTITLES" && value.Group == selected.Subtitles).ToArray();
        if (selected.Audio is not null && audio.Length == 0 || selected.Subtitles is not null && subtitles.Length == 0)
            throw new DownloadSelectionException("The selected HLS variant references a missing rendition group.");
        if (options.SubtitleLanguages is not null && manifest.HasClosedCaptions)
            throw new DownloadSelectionException("Embedded closed captions cannot be removed by subtitle selection. Retain all subtitles or choose a source without embedded captions.");
        if (options.AudioLanguages is { Length: > 0 } && audio.Any(value => value.Url is null))
            throw new DownloadSelectionException("Named audio selection cannot isolate embedded HLS audio. Retain all audio, select none, or choose separate audio renditions.");
        return (Filter(audio, options.AudioLanguages, "audio"), Filter(subtitles, options.SubtitleLanguages, "subtitle"));
    }

    private static HlsRendition[] Filter(HlsRendition[] values, string[]? selected, string kind)
    {
        if (selected is null) return values;
        foreach (var language in selected)
            if (!values.Any(value => string.Equals(value.Language, language, StringComparison.OrdinalIgnoreCase)))
                throw new DownloadSelectionException("A requested " + kind + " language is no longer advertised. Prepare the selected version again.");
        return values.Where(value => value.Language is not null && selected.Contains(value.Language, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    private static string[] Languages(IEnumerable<HlsRendition> renditions) => renditions.Where(value => value.Language is not null)
        .Select(value => value.Language!).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    private static long? Positive(long? value) => value is >= 0 ? value : null;

    private async Task<(HlsOfflineManifest? Manifest, long? Bytes)> FetchAsync(ResolvedStream source, Uri url, bool allowProgressive, CancellationToken ct)
    {
        using var response = await http.SendAsync(url, HttpMethod.Get, DownloadTransferService.RequestHeaders(source, url), ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new DownloadSelectionException("The selected source metadata is unavailable. Select the version again.");
        DownloadTransferService.EnsureIdentity(response);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var prefix = new byte[512];
        var length = 0;
        while (length < prefix.Length)
        {
            var read = await DownloadTransferWorkspace.ReadAsync(stream, prefix.AsMemory(length), ct).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        var final = response.RequestMessage?.RequestUri ?? url;
        var hls = response.Content.Headers.ContentType?.MediaType?.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) == true
            || final.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || Encoding.UTF8.GetString(prefix, 0, length).TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("#EXTM3U", StringComparison.Ordinal);
        if (!hls && allowProgressive) return (null, Positive(response.Content.Headers.ContentLength));
        if (response.Content.Headers.ContentLength > HlsPlaylistRewriter.MaximumPlaylistBytes)
            throw new DownloadSelectionException("The HLS playlist exceeds the metadata size limit.");
        using var bytes = new MemoryStream();
        bytes.Write(prefix, 0, length);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await DownloadTransferWorkspace.ReadAsync(stream, buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            if (bytes.Length + read > HlsPlaylistRewriter.MaximumPlaylistBytes) throw new DownloadSelectionException("The HLS playlist exceeds the metadata size limit.");
            bytes.Write(buffer, 0, read);
        }
        return (HlsOfflineManifest.Parse(Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length), final), null);
    }
}
