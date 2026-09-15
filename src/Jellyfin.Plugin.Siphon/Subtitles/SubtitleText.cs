using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Siphon.Subtitles;

internal static partial class SubtitleText
{
    internal static void Validate(byte[] bytes, string format, string? charset)
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
        }
        catch (ArgumentException)
        {
            throw new InvalidDataException("Subtitle encoding is unsupported.");
        }
    }


    [GeneratedRegex(@"(?m)^\s*\d{1,3}:\d{2}:\d{2}[,.]\d{3}\s+-->\s+\d{1,3}:\d{2}:\d{2}[,.]\d{3}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SubRipCue();
}
