using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Siphon.Infrastructure;

public sealed class CapabilityTokenService(SiphonSecretStore secrets)
{
    private const int MaximumKeyBytes = 512;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public string SignItem(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains("://", StringComparison.Ordinal)
            || key.Any(char.IsControl) || Utf8.GetByteCount(key) > MaximumKeyBytes)
        {
            throw new ArgumentException("Invalid managed item key.", nameof(key));
        }

        var payload = Utf8.GetBytes("1:" + key);
        var signature = HMACSHA256.HashData(secrets.GetSigningKey(), payload);
        return Encode(payload) + "." + Encode(signature);
    }

    public bool TryReadItem(string token, out string key)
    {
        key = string.Empty;
        if (string.IsNullOrEmpty(token) || token.Length > 732)
        {
            return false;
        }

        var separator = token.IndexOf('.');
        if (separator < 1 || separator != token.LastIndexOf('.'))
        {
            return false;
        }

        try
        {
            var payload = Decode(token[..separator]);
            var signature = Decode(token[(separator + 1)..]);
            if (payload.Length is < 3 or > MaximumKeyBytes + 2 || signature.Length != 32
                || payload[0] != (byte)'1' || payload[1] != (byte)':')
            {
                return false;
            }

            var expected = HMACSHA256.HashData(secrets.GetSigningKey(), payload);
            if (!CryptographicOperations.FixedTimeEquals(signature, expected))
            {
                return false;
            }

            var candidate = Utf8.GetString(payload.AsSpan(2));
            if (string.IsNullOrWhiteSpace(candidate) || candidate.Contains("://", StringComparison.Ordinal) || candidate.Any(char.IsControl))
            {
                return false;
            }

            key = candidate;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    public string SignSource(string itemKey, string sourceId)
    {
        if (itemKey.Contains('|') || sourceId.Length != 64 || !sourceId.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("Invalid source identity.");
        }

        return SignItem("source:" + itemKey + "|" + sourceId);
    }

    public bool TryReadSource(string token, out string itemKey, out string sourceId)
    {
        itemKey = sourceId = string.Empty;
        if (!TryReadItem(token, out var payload) || !payload.StartsWith("source:", StringComparison.Ordinal))
        {
            return false;
        }

        var separator = payload.LastIndexOf('|');
        if (separator <= 7 || payload.Length - separator - 1 != 64)
        {
            return false;
        }

        sourceId = payload[(separator + 1)..];
        itemKey = payload[7..separator];
        return sourceId.All(char.IsAsciiHexDigit);
    }

    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string value)
    {
        if (value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
        {
            throw new FormatException("Invalid base64url.");
        }

        var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
        if (Encode(bytes) != value)
        {
            throw new FormatException("Noncanonical base64url.");
        }

        return bytes;
    }
}
