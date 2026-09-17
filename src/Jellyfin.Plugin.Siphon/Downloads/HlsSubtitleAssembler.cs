using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Siphon.Downloads;

internal sealed record HlsSubtitleInput(string FileName, string Name, string? Language, bool Default, bool Forced);

/// <summary>Older Jellyfin FFmpeg versions skip HLS subtitle renditions. Assemble finite WebVTT locally instead.</summary>
internal sealed class HlsSubtitleAssembler
{
    internal const int MaximumBytes = 8 * 1024 * 1024;
    private const int MaximumCues = 100000;
    private const long ClockWrap = 1L << 33;
    private readonly HashSet<Cue> _cues = [];
    private readonly HashSet<string> _headers = new(StringComparer.Ordinal);
    private int _bytes;
    private long? _previousClock;
    private long _clockWraps;

    internal void Add(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes - _bytes) throw new InvalidDataException("The HLS subtitle rendition exceeds its size limit.");
        _bytes += bytes.Length;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw new InvalidDataException("An HLS subtitle segment is not valid UTF-8."); }
        if (text.Contains('\0')) throw new InvalidDataException("An HLS subtitle segment contains invalid text.");
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines.Length == 0 || !(lines[0] == "WEBVTT" || lines[0].StartsWith("WEBVTT ", StringComparison.Ordinal)
            || lines[0].StartsWith("WEBVTT\t", StringComparison.Ordinal)))
            throw new InvalidDataException("Offline HLS subtitles must be WebVTT.");
        var index = 1;
        long offset = 0;
        var mapped = false;
        while (index < lines.Length && lines[index].Length != 0)
        {
            var line = lines[index++];
            if (!line.StartsWith("X-TIMESTAMP-MAP=", StringComparison.Ordinal))
                throw new InvalidDataException("An HLS subtitle header is not supported.");
            if (mapped) throw new InvalidDataException("An HLS subtitle has conflicting timestamp maps.");
            mapped = true;
            var fields = line[16..].Split(',');
            long? local = null;
            long? clock = null;
            foreach (var field in fields)
            {
                if (field.StartsWith("LOCAL:", StringComparison.Ordinal) && local is null) local = Timestamp(field[6..]);
                else if (field.StartsWith("MPEGTS:", StringComparison.Ordinal) && clock is null
                    && long.TryParse(field.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0 && value < ClockWrap) clock = value;
                else throw new InvalidDataException("An HLS subtitle timestamp map is invalid.");
            }
            if (local is null || clock is null) throw new InvalidDataException("An HLS subtitle timestamp map is incomplete.");
            if (_previousClock.HasValue && clock.Value < _previousClock.Value - ClockWrap / 2) _clockWraps++;
            if (_clockWraps > 2) throw new InvalidDataException("The HLS subtitle clock exceeds the supported duration.");
            _previousClock = clock.Value;
            offset = checked((long)Math.Round((clock.Value + _clockWraps * ClockWrap) / 90m, MidpointRounding.AwayFromZero) - local.Value);
        }
        while (index < lines.Length)
        {
            while (index < lines.Length && lines[index].Length == 0) index++;
            var start = index;
            while (index < lines.Length && lines[index].Length != 0) index++;
            if (start == index) break;
            var first = lines[start];
            if (first == "NOTE" || first.StartsWith("NOTE ", StringComparison.Ordinal) || first.StartsWith("NOTE\t", StringComparison.Ordinal)) continue;
            if (first is "STYLE" or "REGION")
            {
                var header = string.Join("\n", lines, start, index - start);
                if (header.Contains("-->", StringComparison.Ordinal)) throw new InvalidDataException("An HLS subtitle style block is invalid.");
                _headers.Add(header);
                continue;
            }
            string? identifier = null;
            if (!first.Contains("-->", StringComparison.Ordinal))
            {
                identifier = first;
                if (++start >= index) throw new InvalidDataException("An HLS subtitle cue is incomplete.");
            }
            var timing = lines[start++];
            var separator = timing.IndexOf("-->", StringComparison.Ordinal);
            if (separator < 0) throw new InvalidDataException("An HLS subtitle cue has no timestamp.");
            var beginning = Timestamp(timing[..separator].Trim());
            var right = timing[(separator + 3)..].Trim();
            var settingsStart = right.IndexOfAny([' ', '\t']);
            var end = Timestamp(settingsStart < 0 ? right : right[..settingsStart]);
            var settings = settingsStart < 0 ? string.Empty : right[settingsStart..];
            if (end < beginning || start >= index) throw new InvalidDataException("An HLS subtitle cue has invalid bounds.");
            beginning = checked(beginning + offset);
            end = checked(end + offset);
            if (beginning < 0 || end > (long)(HlsOfflineManifest.MaximumDurationSeconds * 1000) + 2 * ClockWrap / 90)
                throw new InvalidDataException("An HLS subtitle timestamp is outside the supported timeline.");
            _cues.Add(new Cue(beginning, end, identifier, settings, string.Join("\n", lines, start, index - start)));
            if (_cues.Count > MaximumCues) throw new InvalidDataException("The HLS subtitle rendition has too many cues.");
        }
    }

    internal byte[] Finish()
    {
        var output = new StringBuilder("WEBVTT\n\n");
        foreach (var header in _headers) output.Append(header).Append("\n\n");
        foreach (var cue in _cues.OrderBy(cue => cue.Start).ThenBy(cue => cue.End))
        {
            if (cue.Identifier is not null) output.Append(cue.Identifier).Append('\n');
            output.Append(Format(cue.Start)).Append(" --> ").Append(Format(cue.End)).Append(cue.Settings).Append('\n').Append(cue.Text).Append("\n\n");
            if (output.Length > MaximumBytes) throw new InvalidDataException("The assembled HLS subtitle exceeds its size limit.");
        }
        var bytes = Encoding.UTF8.GetBytes(output.ToString());
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("The assembled HLS subtitle exceeds its size limit.");
        return bytes;
    }

    private static long Timestamp(string value)
    {
        var pieces = value.Split(':');
        if (pieces.Length is < 2 or > 3) throw new InvalidDataException("An HLS subtitle timestamp is invalid.");
        var seconds = pieces[^1].Split('.');
        if (seconds.Length != 2 || seconds[0].Length != 2 || seconds[1].Length != 3
            || !long.TryParse(seconds[0], NumberStyles.None, CultureInfo.InvariantCulture, out var second) || second > 59
            || !long.TryParse(seconds[1], NumberStyles.None, CultureInfo.InvariantCulture, out var millisecond)
            || pieces[^2].Length != 2 || !long.TryParse(pieces[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var minute) || minute > 59)
            throw new InvalidDataException("An HLS subtitle timestamp is invalid.");
        long hour = 0;
        if (pieces.Length == 3 && (!long.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out hour) || hour > 96))
            throw new InvalidDataException("An HLS subtitle timestamp exceeds the supported duration.");
        return ((hour * 60 + minute) * 60 + second) * 1000 + millisecond;
    }

    private static string Format(long value) => string.Create(CultureInfo.InvariantCulture,
        $"{value / 3600000:00}:{value / 60000 % 60:00}:{value / 1000 % 60:00}.{value % 1000:000}");
    private sealed record Cue(long Start, long End, string? Identifier, string Settings, string Text);
}
