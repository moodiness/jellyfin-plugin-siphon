using System.Globalization;
using System.Text;
using Jellyfin.Plugin.Siphon.Playback;

namespace Jellyfin.Plugin.Siphon.Downloads;

internal enum HlsResourceKind { Segment, Map, Key }
internal sealed record HlsByteRange(long Offset, long Length);
internal sealed record HlsEntry(string Text, Uri? Url = null, HlsByteRange? Range = null, HlsResourceKind Kind = HlsResourceKind.Segment);
internal sealed record HlsVariant(Uri Url, long Bandwidth, string? Audio, string? Subtitles, bool HasVideo);
internal sealed record HlsRendition(string Type, string Group, string Name, string? Language, bool Default, bool Forced, Uri? Url);

/// <summary>A deliberately finite subset: no live refreshes, variables, DRM, or inherited remote references.</summary>
internal sealed class HlsOfflineManifest
{
    internal const int MaximumResources = 4096;
    internal const int MaximumPlaylists = 32;
    internal const long MaximumResourceBytes = 256L * 1024 * 1024;
    internal const double MaximumDurationSeconds = 48 * 60 * 60;
    internal List<HlsVariant> Variants { get; } = [];
    internal List<HlsRendition> Renditions { get; } = [];
    internal List<HlsEntry> Entries { get; } = [];
    internal bool IsMaster => Variants.Count != 0;
    internal double DurationSeconds { get; private set; }

    internal static HlsOfflineManifest Parse(string text, Uri finalUrl)
    {
        var resources = new Dictionary<string, Uri>(StringComparer.Ordinal);
        // Reuse the playback validator, including URI attributes, malformed quoting and forbidden URI schemes.
        var rewritten = HlsPlaylistRewriter.Rewrite(text, finalUrl, uri =>
        {
            var name = "r" + resources.Count.ToString(CultureInfo.InvariantCulture);
            resources.Add(name, uri);
            return name;
        });
        var lines = rewritten.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > MaximumResources * 6 || lines.Any(line => line.Length > 8192))
            throw Unsupported("The HLS playlist has too many or oversized entries.");
        var manifest = new HlsOfflineManifest();
        if (lines.Any(line => line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)))
            manifest.ReadMaster(lines, resources);
        else
            manifest.ReadMedia(lines, resources);
        return manifest;
    }

    private void ReadMaster(string[] lines, Dictionary<string, Uri> resources)
    {
        Dictionary<string, string>? variant = null;
        foreach (var line in lines)
        {
            if (!line.StartsWith('#'))
            {
                if (variant is null) throw Unsupported("The HLS master playlist contains an unexpected resource.");
                if (variant.ContainsKey("VIDEO")) throw Unsupported("Alternate HLS video groups are not supported for offline delivery.");
                var bandwidth = Number(Required(variant, "BANDWIDTH"), 1, long.MaxValue);
                var codecs = variant.GetValueOrDefault("CODECS", string.Empty);
                Variants.Add(new HlsVariant(Resource(resources, line), bandwidth, variant.GetValueOrDefault("AUDIO"),
                    variant.GetValueOrDefault("SUBTITLES"), variant.ContainsKey("RESOLUTION")
                        || codecs.Split(',').Any(codec => codec.Trim().StartsWith("avc", StringComparison.OrdinalIgnoreCase)
                            || codec.Trim().StartsWith("hev", StringComparison.OrdinalIgnoreCase)
                            || codec.Trim().StartsWith("hvc", StringComparison.OrdinalIgnoreCase)
                            || codec.Trim().StartsWith("vp", StringComparison.OrdinalIgnoreCase)
                            || codec.Trim().StartsWith("av01", StringComparison.OrdinalIgnoreCase))));
                if (Variants.Count > 128) throw Unsupported("The HLS master playlist has too many variants.");
                variant = null;
                continue;
            }
            if (variant is not null) throw Unsupported("The HLS variant is missing its media playlist.");
            var (tag, value) = Tag(line);
            switch (tag)
            {
                case "#EXTM3U": case "#EXT-X-INDEPENDENT-SEGMENTS": break;
                case "#EXT-X-VERSION": Number(value, 1, 10); break;
                case "#EXT-X-STREAM-INF": variant = Attributes(value); break;
                case "#EXT-X-MEDIA":
                    {
                        var attributes = Attributes(value);
                        var type = Required(attributes, "TYPE");
                        if (type is not ("AUDIO" or "SUBTITLES" or "CLOSED-CAPTIONS"))
                            throw Unsupported("The HLS rendition type is not supported for offline delivery.");
                        if (type == "CLOSED-CAPTIONS") break; // In-band captions remain in the copied video bitstream.
                        var uri = attributes.TryGetValue("URI", out var resource) ? Resource(resources, resource) : null;
                        if (type == "SUBTITLES" && uri is null) throw Unsupported("The HLS subtitle rendition is missing a playlist.");
                        Renditions.Add(new HlsRendition(type, Required(attributes, "GROUP-ID"), Label(Required(attributes, "NAME")),
                            Language(attributes.GetValueOrDefault("LANGUAGE")), attributes.GetValueOrDefault("DEFAULT") == "YES",
                            attributes.GetValueOrDefault("FORCED") == "YES", uri));
                        if (Renditions.Count > 128) throw Unsupported("The HLS master playlist has too many renditions.");
                        break;
                    }
                case "#EXT-X-SESSION-KEY": ValidateKey(Attributes(value), resources); break;
                case "#EXT-X-I-FRAME-STREAM-INF": case "#EXT-X-SESSION-DATA": Attributes(value); break;
                case "#EXT-X-START": Attributes(value); break; // Offline delivery always contains the whole VOD.
                default: throw Unsupported("The HLS master playlist uses an unsupported feature.");
            }
        }
        if (variant is not null || Variants.Count == 0) throw Unsupported("The HLS master playlist has no complete variant.");
    }

    private void ReadMedia(string[] lines, Dictionary<string, Uri> resources)
    {
        var ended = false;
        var segments = 0;
        var durationPending = false;
        string? pendingRange = null;
        Uri? previousUrl = null;
        long previousRangeEnd = 0;
        var previousHadRange = false;
        var encrypted = false;
        var explicitIv = false;
        foreach (var line in lines)
        {
            if (ended) throw Unsupported("The HLS playlist contains data after its end marker.");
            if (!line.StartsWith('#'))
            {
                if (!durationPending) throw Unsupported("The HLS segment has no finite duration.");
                var uri = Resource(resources, line);
                HlsByteRange? range = null;
                if (pendingRange is not null)
                {
                    var implicitOffset = previousHadRange && previousUrl == uri ? previousRangeEnd : (long?)null;
                    range = ByteRange(pendingRange, implicitOffset);
                    previousRangeEnd = checked(range.Offset + range.Length);
                }
                Entries.Add(new HlsEntry("{resource}", uri, range));
                previousUrl = uri;
                previousHadRange = range is not null;
                durationPending = false;
                pendingRange = null;
                if (++segments > MaximumResources) throw Unsupported("The HLS playlist has too many segments.");
                continue;
            }
            var (tag, value) = Tag(line);
            switch (tag)
            {
                case "#EXTM3U":
                case "#EXT-X-INDEPENDENT-SEGMENTS":
                case "#EXT-X-DISCONTINUITY":
                    Entries.Add(new HlsEntry(tag)); break;
                case "#EXT-X-VERSION":
                    Entries.Add(new HlsEntry(tag + ":" + Number(value, 1, 10).ToString(CultureInfo.InvariantCulture))); break;
                case "#EXT-X-TARGETDURATION":
                    Entries.Add(new HlsEntry(tag + ":" + Number(value, 1, 3600).ToString(CultureInfo.InvariantCulture))); break;
                case "#EXT-X-MEDIA-SEQUENCE":
                case "#EXT-X-DISCONTINUITY-SEQUENCE":
                    Entries.Add(new HlsEntry(tag + ":" + Number(value, 0, long.MaxValue - MaximumResources).ToString(CultureInfo.InvariantCulture))); break;
                case "#EXT-X-PLAYLIST-TYPE":
                    if (value != "VOD") throw Unsupported("Only finite HLS VOD playlists can be downloaded.");
                    Entries.Add(new HlsEntry(tag + ":VOD")); break;
                case "#EXTINF":
                    {
                        var durationText = value.Split(',', 2)[0];
                        if (durationPending || !double.TryParse(durationText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var duration)
                            || !double.IsFinite(duration) || duration <= 0 || duration > 3600)
                            throw Unsupported("The HLS segment duration is not supported.");
                        DurationSeconds += duration;
                        if (DurationSeconds > MaximumDurationSeconds) throw Unsupported("The HLS VOD exceeds the duration limit.");
                        Entries.Add(new HlsEntry("#EXTINF:" + duration.ToString("0.#########", CultureInfo.InvariantCulture) + ","));
                        durationPending = true;
                        break;
                    }
                case "#EXT-X-BYTERANGE":
                    if (pendingRange is not null) throw Unsupported("The HLS segment has conflicting byte ranges.");
                    pendingRange = value; break;
                case "#EXT-X-KEY":
                    {
                        var attributes = Attributes(value);
                        var key = ValidateKey(attributes, resources);
                        encrypted = key is not null;
                        explicitIv = attributes.ContainsKey("IV");
                        Entries.Add(key is null ? new HlsEntry("#EXT-X-KEY:METHOD=NONE")
                            : new HlsEntry("#EXT-X-KEY:METHOD=AES-128,URI=\"{resource}\""
                                + (explicitIv ? ",IV=" + attributes["IV"] : string.Empty), key, Kind: HlsResourceKind.Key));
                        break;
                    }
                case "#EXT-X-MAP":
                    {
                        var attributes = Attributes(value);
                        if (encrypted && !explicitIv) throw Unsupported("Encrypted HLS initialization sections require an explicit IV.");
                        Entries.Add(new HlsEntry("#EXT-X-MAP:URI=\"{resource}\"", Resource(resources, Required(attributes, "URI")),
                            attributes.TryGetValue("BYTERANGE", out var range) ? ByteRange(range, 0) : null, HlsResourceKind.Map));
                        break;
                    }
                case "#EXT-X-ENDLIST":
                    if (durationPending || pendingRange is not null) throw Unsupported("The HLS playlist ends with an incomplete segment.");
                    ended = true;
                    Entries.Add(new HlsEntry(tag)); break;
                case "#EXT-X-PROGRAM-DATE-TIME": case "#EXT-X-DATERANGE": case "#EXT-X-START": break;
                default: throw Unsupported("The HLS media playlist uses an unsupported feature (live, gaps, or DRM).");
            }
        }
        if (!ended || segments == 0) throw Unsupported("Only finite, complete HLS VOD playlists can be downloaded.");
    }

    private static Uri? ValidateKey(Dictionary<string, string> attributes, Dictionary<string, Uri> resources)
    {
        var method = Required(attributes, "METHOD");
        if (attributes.GetValueOrDefault("KEYFORMAT", "identity") != "identity"
            || attributes.GetValueOrDefault("KEYFORMATVERSIONS", "1") != "1" || method is not ("NONE" or "AES-128"))
            throw Unsupported("HLS DRM and sample encryption are not supported for offline delivery.");
        if (method == "NONE")
        {
            if (attributes.Count != 1) throw Unsupported("The unencrypted HLS key declaration is invalid.");
            return null;
        }
        if (attributes.TryGetValue("IV", out var iv) && (iv.Length != 34 || !iv.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || iv.AsSpan(2).IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0))
            throw Unsupported("The HLS encryption IV is invalid.");
        return Resource(resources, Required(attributes, "URI"));
    }

    private static HlsByteRange ByteRange(string value, long? implicitOffset)
    {
        var pieces = value.Split('@');
        if (pieces.Length is < 1 or > 2) throw Unsupported("The HLS byte range is invalid.");
        var length = Number(pieces[0], 1, MaximumResourceBytes);
        var offset = pieces.Length == 2 ? Number(pieces[1], 0, long.MaxValue - length)
            : implicitOffset ?? throw Unsupported("The HLS implicit byte range has no matching predecessor.");
        if (offset > long.MaxValue - length) throw Unsupported("The HLS byte range is too large.");
        return new HlsByteRange(offset, length);
    }

    private static (string Tag, string Value) Tag(string line)
    {
        var colon = line.IndexOf(':');
        return colon < 0 ? (line, string.Empty) : (line[..colon], line[(colon + 1)..]);
    }

    private static Dictionary<string, string> Attributes(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var position = 0;
        while (position < value.Length)
        {
            var equals = value.IndexOf('=', position);
            if (equals < 0) throw Unsupported("Invalid HLS attributes.");
            var name = value[position..equals];
            if (name.Length == 0 || name.Any(c => c is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and not '-'))
                throw Unsupported("Invalid HLS attribute name.");
            position = equals + 1;
            string item;
            if (position < value.Length && value[position] == '"')
            {
                var end = value.IndexOf('"', ++position);
                if (end < 0) throw Unsupported("Invalid HLS quoted attribute.");
                item = value[position..end];
                position = end + 1;
            }
            else
            {
                var end = value.IndexOf(',', position);
                if (end < 0) end = value.Length;
                item = value[position..end];
                position = end;
            }
            if (!result.TryAdd(name, item) || result.Count > 64) throw Unsupported("Conflicting or excessive HLS attributes.");
            if (position < value.Length && value[position++] != ',') throw Unsupported("Invalid HLS attribute separator.");
        }
        return result;
    }

    private static string Required(Dictionary<string, string> attributes, string key)
        => attributes.TryGetValue(key, out var value) && value.Length != 0 ? value : throw Unsupported("A required HLS attribute is missing.");
    private static Uri Resource(Dictionary<string, Uri> resources, string name)
        => resources.TryGetValue(name, out var uri) ? uri : throw Unsupported("An HLS resource was not validated.");
    private static long Number(string value, long minimum, long maximum)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result >= minimum && result <= maximum
            ? result : throw Unsupported("An HLS numeric value is out of range.");
    private static string? Language(string? value) => value is { Length: > 0 and <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? value : null;
    private static string Label(string value)
    {
        if (value.Length > 128 || value.Any(c => char.IsControl(c) || c is '"' or '\\')) throw Unsupported("An HLS rendition label is invalid.");
        return value;
    }
    private static InvalidDataException Unsupported(string message) => new(message);
}
