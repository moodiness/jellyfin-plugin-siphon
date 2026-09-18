using System.Globalization;
using System.Net;
using System.Text;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Playback;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.Siphon.Downloads;

internal sealed class HlsOfflineTransfer(ISafeHttpClient http, IMediaEncoder encoder, ResolvedStream source, DownloadTransferWorkspace workspace, DownloadTransferOptions options)
{
    private readonly Dictionary<(Uri Url, HlsByteRange? Range, HlsResourceKind Kind), string> _resources = [];
    private readonly Dictionary<Uri, string> _playlists = [];
    private readonly Dictionary<Uri, (string ETag, long Length)> _rangeRepresentations = [];
    private readonly List<string> _stagedNames = [];
    private int _nextName;
    private int _playlistRequests = 1;
    private int _requests = 1;
    private long _received;

    internal async Task<DownloadTransferResult> TransferAsync(Stream root, ReadOnlyMemory<byte> prefix, Uri finalUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(encoder.EncoderPath) || !File.Exists(encoder.EncoderPath))
            throw new DownloadSelectionException("Offline HLS requires Jellyfin's configured FFmpeg encoder. Configure the media tools or choose a progressive source.");
        var text = await ReadPlaylistAsync(root, prefix, cancellationToken).ConfigureAwait(false);
        var manifest = HlsOfflineManifest.Parse(text, finalUrl);
        string input;
        var audioRequired = false;
        var subtitleInputs = new List<HlsSubtitleInput>();
        if (manifest.IsMaster)
        {
            // Exactly one rendition: never switch to a different variant after a failed request.
            var selected = DownloadPreparationService.SelectVariant(manifest);
            var (audio, subtitles) = DownloadPreparationService.SelectRenditions(manifest, selected, options);
            if (audio.Length + subtitles.Length + 2 > HlsOfflineManifest.MaximumPlaylists)
                throw new InvalidDataException("The selected HLS variant has too many rendition playlists.");
            if (audio.Concat(subtitles).GroupBy(rendition => (rendition.Type, rendition.Name)).Any(group => group.Count() != 1))
                throw new InvalidDataException("The HLS rendition names conflict.");
            var media = await StagePlaylistAsync(selected.Url, cancellationToken).ConfigureAwait(false);
            var master = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:7\n");
            foreach (var rendition in audio)
            {
                var local = rendition.Url is null ? null : await StagePlaylistAsync(rendition.Url, cancellationToken).ConfigureAwait(false);
                master.Append("#EXT-X-MEDIA:TYPE=").Append(rendition.Type).Append(",GROUP-ID=\"")
                    .Append("audio").Append("\",NAME=\"").Append(rendition.Name)
                    .Append("\",DEFAULT=").Append(rendition.Default ? "YES" : "NO").Append(",AUTOSELECT=YES");
                if (rendition.Language is not null) master.Append(",LANGUAGE=\"").Append(rendition.Language).Append('"');
                if (local is not null) master.Append(",URI=\"").Append(local).Append('"');
                master.Append('\n');
            }
            master.Append("#EXT-X-STREAM-INF:BANDWIDTH=").Append(selected.Bandwidth.ToString(CultureInfo.InvariantCulture));
            if (audio.Length > 0) master.Append(",AUDIO=\"audio\"");
            master.Append('\n').Append(media).Append('\n');
            input = await WritePlaylistAsync(master.ToString(), cancellationToken).ConfigureAwait(false);
            audioRequired = audio.Length > 0;
            foreach (var rendition in subtitles)
            {
                var local = await StageSubtitleAsync(rendition.Url!, cancellationToken).ConfigureAwait(false);
                subtitleInputs.Add(new HlsSubtitleInput(local, rendition.Name, rendition.Language, rendition.Default, rendition.Forced));
            }
        }
        else
        {
            if (options.AudioLanguages is { Length: > 0 } || options.SubtitleLanguages is { Length: > 0 })
                throw new DownloadSelectionException("This media playlist does not advertise languages. Retain all tracks, select none, or choose a master playlist.");
            input = await StageMediaAsync(manifest, cancellationToken).ConfigureAwait(false);
        }

        await workspace.ReportAsync(_received, null, force: true, phase: "PreparingAudio").ConfigureAwait(false);
        var remuxer = new HlsRemuxer(encoder.EncoderPath);
        var audioInputs = await remuxer.PrepareAudioAsync(workspace, input, encoder.ProbePath, _received, options.AudioLanguages, cancellationToken).ConfigureAwait(false);
        if (audioRequired && audioInputs.Count == 0) throw new InvalidDataException("The selected HLS audio group has no playable audio.");
        await workspace.ReportAsync(_received, null, force: true, phase: "Assembling").ConfigureAwait(false);
        await remuxer.RemuxAsync(workspace, input, audioInputs, subtitleInputs, _received, options.SubtitleLanguages is null, cancellationToken).ConfigureAwait(false);
        var bytes = workspace.Length("remux.part");
        if (bytes == 0) throw new IOException("The HLS remux produced no media.");
        workspace.Move("remux.part", "media.mkv");
        foreach (var name in _stagedNames) workspace.Delete(name);
        await workspace.ReportAsync(_received, null, force: true, phase: "Assembling").ConfigureAwait(false);
        return new DownloadTransferResult("media.mkv", "video/x-matroska", bytes);
    }

    private async Task<string> StagePlaylistAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (_playlists.TryGetValue(uri, out var existing)) return existing;
        var manifest = await FetchMediaAsync(uri, cancellationToken).ConfigureAwait(false);
        var name = await StageMediaAsync(manifest, cancellationToken).ConfigureAwait(false);
        _playlists.Add(uri, name);
        return name;
    }

    private async Task<HlsOfflineManifest> FetchMediaAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (++_playlistRequests > HlsOfflineManifest.MaximumPlaylists) throw new InvalidDataException("The HLS playlist limit was exceeded.");
        CountRequest();
        using var response = await http.SendAsync(uri, HttpMethod.Get, DownloadTransferService.RequestHeaders(source, uri), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new IOException("An HLS playlist could not be downloaded.");
        DownloadTransferService.EnsureIdentity(response);
        if (response.Content.Headers.ContentLength > HlsPlaylistRewriter.MaximumPlaylistBytes) throw new InvalidDataException("The HLS playlist is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var text = await ReadPlaylistAsync(stream, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        var manifest = HlsOfflineManifest.Parse(text, response.RequestMessage?.RequestUri ?? uri);
        if (manifest.IsMaster) throw new InvalidDataException("Nested HLS master playlists are not supported for offline delivery.");
        return manifest;
    }

    private async Task<string> StageSubtitleAsync(Uri uri, CancellationToken cancellationToken)
    {
        var manifest = await FetchMediaAsync(uri, cancellationToken).ConfigureAwait(false);
        if (manifest.Entries.Any(entry => entry.Kind == HlsResourceKind.Map))
            throw new InvalidDataException("Offline HLS subtitles require WebVTT rather than fragmented MP4.");
        var assembler = new HlsSubtitleAssembler();
        byte[]? key = null;
        byte[]? iv = null;
        long sequence = 0;
        foreach (var entry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Text.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
                sequence = long.Parse(entry.Text.AsSpan(22), CultureInfo.InvariantCulture);
            if (entry.Text == "#EXT-X-KEY:METHOD=NONE") key = null;
            if (entry.Url is null) continue;
            var staged = await StageResourceAsync(entry, cancellationToken).ConfigureAwait(false);
            if (entry.Kind == HlsResourceKind.Key)
            {
                key = await File.ReadAllBytesAsync(workspace.PathFor(staged), cancellationToken).ConfigureAwait(false);
                var ivStart = entry.Text.IndexOf(",IV=0x", StringComparison.OrdinalIgnoreCase);
                iv = ivStart < 0 ? null : Convert.FromHexString(entry.Text[(ivStart + 6)..]);
                continue;
            }
            if (workspace.Length(staged) > HlsSubtitleAssembler.MaximumBytes + 16)
                throw new InvalidDataException("The HLS subtitle segment exceeds its size limit.");
            var bytes = await File.ReadAllBytesAsync(workspace.PathFor(staged), cancellationToken).ConfigureAwait(false);
            if (key is not null)
            {
                var segmentIv = iv;
                if (segmentIv is null)
                {
                    segmentIv = new byte[16];
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(segmentIv.AsSpan(8), sequence);
                }
                try
                {
                    using var aes = System.Security.Cryptography.Aes.Create();
                    aes.Key = key;
                    bytes = aes.DecryptCbc(bytes, segmentIv);
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    throw new InvalidDataException("The encrypted HLS subtitle segment is invalid.");
                }
            }
            assembler.Add(bytes);
            sequence++;
        }
        var name = NextName(".vtt");
        await workspace.WriteAsync(name, assembler.Finish(), cancellationToken).ConfigureAwait(false);
        _stagedNames.Add(name);
        return name;
    }

    private async Task<string> StageMediaAsync(HlsOfflineManifest manifest, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        foreach (var entry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = entry.Text;
            if (entry.Url is not null)
            {
                var local = await StageResourceAsync(entry, cancellationToken).ConfigureAwait(false);
                line = line.Replace("{resource}", local, StringComparison.Ordinal);
            }
            text.Append(line).Append('\n');
        }
        return await WritePlaylistAsync(text.ToString(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> StageResourceAsync(HlsEntry entry, CancellationToken cancellationToken)
    {
        var key = (entry.Url!, entry.Range, entry.Kind);
        if (_resources.TryGetValue(key, out var existing)) return existing;
        CountRequest();
        var headers = DownloadTransferService.RequestHeaders(source, entry.Url!);
        if (entry.Range is { } range)
            headers["Range"] = "bytes=" + range.Offset.ToString(CultureInfo.InvariantCulture) + "-" + (range.Offset + range.Length - 1).ToString(CultureInfo.InvariantCulture);
        if (entry.Range is not null && _rangeRepresentations.TryGetValue(entry.Url!, out var previous))
            headers["If-Range"] = previous.ETag;
        using var response = await http.SendAsync(entry.Url!, HttpMethod.Get, headers, cancellationToken).ConfigureAwait(false);
        DownloadTransferService.EnsureIdentity(response);
        if (entry.Range is { } expectedRange)
        {
            var actual = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent || actual is not { HasRange: true, HasLength: true, Unit: "bytes" }
                || actual.From != expectedRange.Offset || actual.To != expectedRange.Offset + expectedRange.Length - 1
                || actual.Length <= actual.To || response.Content.Headers.ContentLength != expectedRange.Length)
                throw new IOException("An HLS byte range did not match the requested resource.");
            var etag = response.Headers.ETag?.ToString();
            if (!DownloadTransferService.IsStrongETag(etag))
                throw new IOException("Offline HLS byte ranges require a strong representation validator.");
            var representation = (ETag: etag!, Length: actual.Length!.Value);
            if (_rangeRepresentations.TryGetValue(entry.Url!, out var prior) && prior != representation)
                throw new IOException("An HLS byte-range resource changed during staging.");
            _rangeRepresentations[entry.Url!] = representation;
        }
        else if (response.StatusCode != HttpStatusCode.OK) throw new IOException("An HLS resource could not be downloaded.");

        var maximum = entry.Kind == HlsResourceKind.Key ? 16 : HlsOfflineManifest.MaximumResourceBytes;
        var expected = entry.Range?.Length ?? response.Content.Headers.ContentLength;
        if (expected > maximum) throw new IOException("An HLS resource exceeds the supported size limit.");
        if (expected is long size) workspace.EnsureCapacity(size);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var prefix = new byte[512];
        var prefixLength = 0;
        while (prefixLength < prefix.Length)
        {
            var read = await DownloadTransferWorkspace.ReadAsync(stream, prefix.AsMemory(prefixLength), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            prefixLength += read;
            if (prefixLength > maximum) throw new IOException("An HLS resource exceeds the supported size limit.");
        }
        var prefixText = Encoding.UTF8.GetString(prefix, 0, prefixLength).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (entry.Kind != HlsResourceKind.Key && (prefixText.StartsWith("#EXTM3U", StringComparison.Ordinal) || prefixText.StartsWith("ffconcat", StringComparison.Ordinal)))
            throw new InvalidDataException("An HLS media resource cannot be another playlist.");
        var extension = ResourceExtension(entry, prefix.AsSpan(0, prefixLength));
        var name = NextName(extension);
        _stagedNames.Add(name);
        await using (var output = workspace.OpenWrite(name, FileMode.CreateNew))
        {
            long written = prefixLength;
            if (expected.HasValue && written > expected.Value) throw new IOException("An HLS resource has an inconsistent length.");
            await workspace.AppendAsync(output, prefix.AsMemory(0, prefixLength), cancellationToken).ConfigureAwait(false);
            _received += prefixLength;
            var buffer = new byte[DownloadTransferWorkspace.BufferSize];
            while (true)
            {
                var read = await DownloadTransferWorkspace.ReadAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (read > maximum - written || (expected.HasValue && read > expected.Value - written))
                    throw new IOException("An HLS resource exceeds its declared or supported size.");
                await workspace.AppendAsync(output, buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
                _received += read;
                await workspace.ReportAsync(_received, null).ConfigureAwait(false);
            }
            if (written == 0 || (expected.HasValue && written != expected.Value) || (entry.Kind == HlsResourceKind.Key && written != 16))
                throw new IOException("An HLS resource is incomplete or has an invalid encryption key.");
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        _resources.Add(key, name);
        return name;
    }

    private async Task<string> ReadPlaylistAsync(Stream stream, ReadOnlyMemory<byte> prefix, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        output.Write(prefix.Span);
        _received += prefix.Length;
        var buffer = new byte[DownloadTransferWorkspace.BufferSize];
        while (true)
        {
            var read = await DownloadTransferWorkspace.ReadAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (read > HlsPlaylistRewriter.MaximumPlaylistBytes - output.Length) throw new InvalidDataException("The HLS playlist is too large.");
            output.Write(buffer, 0, read);
            _received += read;
            await workspace.ReportAsync(_received, null).ConfigureAwait(false);
        }
        try { return new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, checked((int)output.Length)); }
        catch (DecoderFallbackException) { throw new InvalidDataException("The HLS playlist is not valid UTF-8."); }
    }

    private async Task<string> WritePlaylistAsync(string text, CancellationToken cancellationToken)
    {
        var name = NextName(".m3u8");
        await workspace.WriteAsync(name, Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
        _stagedNames.Add(name);
        return name;
    }

    private void CountRequest()
    {
        if (++_requests > HlsOfflineManifest.MaximumResources) throw new InvalidDataException("The HLS download requires too many resources.");
    }

    private string NextName(string extension) => "h" + (++_nextName).ToString("D5", CultureInfo.InvariantCulture) + extension;

    private static string ResourceExtension(HlsEntry entry, ReadOnlySpan<byte> prefix)
    {
        if (entry.Kind == HlsResourceKind.Key) return ".key";
        if (entry.Kind == HlsResourceKind.Map || (prefix.Length >= 8 && (prefix.Slice(4, 4).SequenceEqual("ftyp"u8)
            || prefix.Slice(4, 4).SequenceEqual("styp"u8) || prefix.Slice(4, 4).SequenceEqual("moof"u8)))) return ".mp4";
        if (prefix.StartsWith("WEBVTT"u8) || prefix.StartsWith("\uFEFFWEBVTT"u8)) return ".vtt";
        var extension = Path.GetExtension(entry.Url!.AbsolutePath).ToLowerInvariant();
        if (extension is ".ts" or ".m4s" or ".mp4" or ".m4a" or ".aac" or ".ac3" or ".eac3" or ".mp3" or ".vtt") return extension;
        if (prefix.Length > 1 && prefix[0] == 0xff && (prefix[1] & 0xf6) == 0xf0) return ".aac";
        return ".ts";
    }
}
