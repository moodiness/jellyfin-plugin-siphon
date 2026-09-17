using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Siphon.Subtitles;

internal static partial class SubtitleText
{
    internal static byte[] Normalize(byte[] bytes, string format, string? charset)
    {
        if (bytes.Length == 0) throw new InvalidDataException("Subtitle is empty.");
        try
        {
            var encoding = string.IsNullOrWhiteSpace(charset)
                ? new UTF8Encoding(false, true)
                : Encoding.GetEncoding(charset.Trim('"'), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            using var input = new MemoryStream(bytes, writable: false);
            using var reader = new StreamReader(input, encoding, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            if (text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
                throw new InvalidDataException("Subtitle is not text.");

            var valid = format switch
            {
                "srt" => SubRipCue().IsMatch(text),
                "vtt" => text.AsSpan().TrimStart().StartsWith("WEBVTT", StringComparison.Ordinal),
                "ass" or "ssa" => text.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase)
                    && text.Contains("[Events]", StringComparison.OrdinalIgnoreCase)
                    && text.Contains("Dialogue:", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            if (!valid) throw new InvalidDataException("Subtitle format is invalid.");
            // SubtitleResponse carries no charset; Jellyfin must receive self-describing
            // bytes rather than losing an upstream HTTP encoding declaration.
            return reader.CurrentEncoding.CodePage == Encoding.UTF8.CodePage ? bytes : Encoding.UTF8.GetBytes(text);
        }
        catch (ArgumentException)
        {
            throw new InvalidDataException("Subtitle encoding is unsupported.");
        }
    }


    [GeneratedRegex(@"(?m)^\s*\d{1,3}:\d{2}:\d{2}[,.]\d{3}\s+-->\s+\d{1,3}:\d{2}:\d{2}[,.]\d{3}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SubRipCue();
}
