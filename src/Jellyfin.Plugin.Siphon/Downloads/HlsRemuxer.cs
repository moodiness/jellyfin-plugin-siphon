using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.Siphon.Downloads;

internal sealed record HlsAudioInput(int SourceIndex, int? NormalizedIndex);

internal sealed class HlsRemuxer(string encoderPath)
{
    private const string NormalizedAudio = "audio-config.m4a";

    internal async Task<IReadOnlyList<HlsAudioInput>> PrepareAudioAsync(DownloadTransferWorkspace workspace, string playlist,
        string probePath, long received, CancellationToken cancellationToken)
    {
        workspace.PathFor(playlist);
        if (string.IsNullOrWhiteSpace(probePath) || !File.Exists(probePath))
            throw new IOException("Offline HLS requires Jellyfin's configured FFprobe executable.");
        const string probeFile = "audio-probe.json";
        var arguments = new List<string> { "-v", "error", "-max_alloc", "67108864" };
        arguments.AddRange(InputArguments(playlist));
        arguments.AddRange(["-select_streams", "a", "-show_entries", "stream=index,codec_name,extradata_size", "-of", "json"]);
        await RunToFileAsync(probePath, workspace, probeFile, arguments, received, 65536, TimeSpan.FromMinutes(1), false, cancellationToken).ConfigureAwait(false);
        var audio = new List<HlsAudioInput>();
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(workspace.PathFor(probeFile), cancellationToken).ConfigureAwait(false));
            var streams = document.RootElement.GetProperty("streams");
            if (streams.GetArrayLength() > 128) throw new InvalidDataException("The HLS source contains too many audio tracks.");
            var indices = new HashSet<int>();
            var normalized = 0;
            foreach (var stream in streams.EnumerateArray())
            {
                var index = stream.GetProperty("index").GetInt32();
                if (index is < 0 or > 8191 || !indices.Add(index)) throw new InvalidDataException("The HLS audio stream identities are invalid.");
                var needsConfiguration = stream.GetProperty("codec_name").GetString() == "aac"
                    && (!stream.TryGetProperty("extradata_size", out var extra) || extra.GetInt32() == 0);
                audio.Add(new(index, needsConfiguration ? normalized++ : null));
            }
        }
        finally { workspace.Delete(probeFile); }

        if (audio.Any(stream => stream.NormalizedIndex.HasValue))
        {
            // ADTS AAC lacks AudioSpecificConfig until its first packet. Matroska cannot patch
            // that header through a pipe. A bounded fragmented-MP4 pass resolves only those AAC
            // tracks, preserving encoded packets and timestamps; other codecs stay untouched.
            arguments = EncoderArguments(playlist);
            foreach (var stream in audio.Where(stream => stream.NormalizedIndex.HasValue))
                arguments.AddRange(["-map", "0:" + stream.SourceIndex.ToString(CultureInfo.InvariantCulture)]);
            arguments.AddRange(["-c", "copy", "-bsf:a", "aac_adtstoasc", "-movflags", "+frag_keyframe+empty_moov+default_base_moof+delay_moov",
                "-protocol_whitelist", "pipe", "-f", "mp4", "pipe:1"]);
            await RunToFileAsync(encoderPath, workspace, NormalizedAudio, arguments, received, long.MaxValue,
                TimeSpan.FromMinutes(30), false, cancellationToken).ConfigureAwait(false);
        }
        return audio;
    }

    internal async Task RemuxAsync(DownloadTransferWorkspace workspace, string playlist, IReadOnlyList<HlsAudioInput> audio,
        IReadOnlyList<HlsSubtitleInput> subtitles, long received, CancellationToken cancellationToken)
    {
        workspace.PathFor(playlist);
        foreach (var subtitle in subtitles) workspace.PathFor(subtitle.FileName);
        var normalized = audio.Any(stream => stream.NormalizedIndex.HasValue);
        if (normalized) workspace.PathFor(NormalizedAudio);
        await RunToFileAsync(encoderPath, workspace, "remux.part", Arguments(playlist, audio, subtitles), received,
            long.MaxValue, TimeSpan.FromMinutes(30), true, cancellationToken).ConfigureAwait(false);
        if (normalized) workspace.Delete(NormalizedAudio);
    }

    private static async Task RunToFileAsync(string executable, DownloadTransferWorkspace workspace, string outputName,
        IEnumerable<string> arguments, long received, long maximumBytes, TimeSpan timeout, bool matroska, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        workspace.Delete(outputName);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workspace.DirectoryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            if (!process.Start()) throw new IOException("The offline HLS media tool could not start.");
        }
        catch (Win32Exception) { throw new IOException("The offline HLS media tool could not start."); }

        var stderr = DrainErrorsAsync(process.StandardError, deadline.Token);
        try
        {
            await using (var output = workspace.OpenWrite(outputName, FileMode.CreateNew))
            {
                var buffer = new byte[DownloadTransferWorkspace.BufferSize];
                var header = new byte[4];
                var headerLength = 0;
                long bytes = 0;
                while (true)
                {
                    var read = await DownloadTransferWorkspace.ReadAsync(process.StandardOutput.BaseStream, buffer, deadline.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (read > maximumBytes - bytes) throw new IOException("The offline HLS media tool exceeded its output limit.");
                    if (headerLength < header.Length)
                    {
                        var count = Math.Min(read, header.Length - headerLength);
                        buffer.AsSpan(0, count).CopyTo(header.AsSpan(headerLength));
                        headerLength += count;
                    }
                    await workspace.AppendAsync(output, buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                    bytes += read;
                    await workspace.ReportAsync(received, received).ConfigureAwait(false);
                }
                await process.WaitForExitAsync(deadline.Token).WaitAsync(DownloadTransferWorkspace.IdleTimeout, deadline.Token).ConfigureAwait(false);
                await stderr.ConfigureAwait(false);
                if (process.ExitCode != 0 || bytes == 0 || (matroska && (bytes < 4 || !header.AsSpan().SequenceEqual(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }))))
                    throw new IOException("The HLS media could not be prepared without conversion. Check the configured media tools and source format.");
                await output.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The offline HLS media tool exceeded its time limit.");
        }
        catch (TimeoutException) { throw new IOException("The offline HLS media tool stopped responding."); }
        finally
        {
            // No cancelled, timed-out or over-budget child may keep writing after this returns.
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await deadline.CancelAsync().ConfigureAwait(false);
            try { await stderr.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    private static IEnumerable<string> InputArguments(string playlist) =>
    [
        // Both the probe and encoder see generated local files only. Nested media cannot
        // become a concat/HLS playlist or enable external movie references.
        "-protocol_whitelist", "file,crypto",
        "-format_whitelist", "hls,mpegts,mov,aac,ac3,eac3,mp3,webvtt",
        "-allowed_extensions", "m3u8,ts,mp4,m4s,m4a,aac,ac3,eac3,mp3,vtt,key",
        "-seg_format_options", "protocol_whitelist=file,crypto:format_whitelist=mpegts,mov,aac,ac3,eac3,mp3,webvtt",
        "-i", playlist
    ];

    private static List<string> EncoderArguments(string playlist)
    {
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-nostats", "-xerror", "-max_alloc", "67108864", "-copyts" };
        arguments.AddRange(InputArguments(playlist));
        return arguments;
    }

    private static IEnumerable<string> Arguments(string playlist, IReadOnlyList<HlsAudioInput> audio, IReadOnlyList<HlsSubtitleInput> subtitles)
    {
        var arguments = EncoderArguments(playlist);
        var normalized = audio.Any(stream => stream.NormalizedIndex.HasValue);
        if (normalized) arguments.AddRange(["-protocol_whitelist", "file", "-format_whitelist", "mov", "-i", NormalizedAudio]);
        foreach (var subtitle in subtitles)
            arguments.AddRange(["-protocol_whitelist", "file", "-format_whitelist", "webvtt", "-f", "webvtt", "-i", subtitle.FileName]);
        arguments.AddRange(["-map", "0:v:0"]);
        for (var index = 0; index < audio.Count; index++)
        {
            var stream = audio[index];
            var sourceIndex = stream.SourceIndex.ToString(CultureInfo.InvariantCulture);
            var map = stream.NormalizedIndex is { } n ? "1:a:" + n.ToString(CultureInfo.InvariantCulture) : "0:" + sourceIndex;
            arguments.AddRange(["-map", map, "-map_metadata:s:a:" + index.ToString(CultureInfo.InvariantCulture), "0:s:" + sourceIndex]);
        }
        for (var index = 0; index < subtitles.Count; index++)
        {
            var stream = index.ToString(CultureInfo.InvariantCulture);
            var input = (index + (normalized ? 2 : 1)).ToString(CultureInfo.InvariantCulture);
            var subtitle = subtitles[index];
            arguments.AddRange(["-map", input + ":s:0", "-metadata:s:s:" + stream, "title=" + subtitle.Name]);
            if (subtitle.Language is not null) arguments.AddRange(["-metadata:s:s:" + stream, "language=" + subtitle.Language]);
            var disposition = subtitle.Default ? (subtitle.Forced ? "default+forced" : "default") : (subtitle.Forced ? "forced" : "0");
            arguments.AddRange(["-disposition:s:" + stream, disposition]);
        }
        arguments.AddRange([
            "-map", "0:s?", "-c", "copy", "-max_muxing_queue_size", "1024", "-max_interleave_delta", "1000000", "-avoid_negative_ts", "make_zero",
            // All subprocess output passes through the bounded managed writer, including AAC preparation.
            "-protocol_whitelist", "pipe", "-f", "matroska", "pipe:1"
        ]);
        return arguments;
    }

    private static async Task DrainErrorsAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) != 0) { }
        // Media diagnostics are untrusted; neither stderr nor process arguments are logged.
    }
}
