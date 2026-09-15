using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Siphon.Playback;

public static partial class HlsPlaylistRewriter
{
    public const int MaximumPlaylistBytes = 2 * 1024 * 1024;

    public static string Rewrite(string playlist, Uri finalUrl, Func<Uri, string> proxyUrl)
    {
        if (playlist.Length > MaximumPlaylistBytes || !playlist.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("#EXTM3U", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Invalid HLS playlist.");
        }

        var output = new StringBuilder(playlist.Length);
        using var reader = new StringReader(playlist);
        var resources = 0;
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Contains("{$", StringComparison.Ordinal) || line.StartsWith("#EXT-X-DEFINE:", StringComparison.Ordinal) || line.StartsWith("#EXT-X-CONTENT-STEERING:", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Unsupported HLS feature.");
            }

            if (line.Length == 0 || (line.StartsWith('#') && !line.StartsWith("#EXT", StringComparison.Ordinal)))
            {
                continue;
            }

            if (!line.StartsWith('#'))
            {
                line = RewriteUri(line);
            }
            else
            {
                line = UriAttribute().Replace(line, match => match.Groups[1].Value + "\"" + RewriteUri(match.Groups[2].Value) + "\"");
                // A malformed or unquoted URI must never pass through to a client.
                var stripped = UriAttribute().Replace(raw.Trim(), string.Empty);
                if (UnrewrittenUri().IsMatch(stripped) || stripped.Contains("://", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Invalid HLS resource attribute.");
                }
            }

            output.Append(line).Append('\n');
        }

        return output.ToString();

        string RewriteUri(string value)
        {
            if (++resources > 4096 || value.Length == 0 || !Uri.TryCreate(finalUrl, value, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new InvalidDataException("Unsupported HLS resource.");
            }

            return proxyUrl(uri);
        }
    }

    [GeneratedRegex("((?:^|[:,])(?:[A-Z0-9-]*URI)=)\"([^\"\r\n]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex UriAttribute();

    [GeneratedRegex("(?:^|[:,])[A-Z0-9-]*URI=", RegexOptions.CultureInvariant)]
    private static partial Regex UnrewrittenUri();
}
