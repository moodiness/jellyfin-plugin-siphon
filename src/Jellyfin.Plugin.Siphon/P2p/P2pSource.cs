using MonoTorrent;

namespace Jellyfin.Plugin.Siphon.P2p;

/// <summary>Server-only source data. Tracker URLs can contain account credentials.</summary>
public sealed record P2pSource(string InfoHash, int? FileIndex, string? FileName, string[] Trackers)
{
    public int? Season { get; init; }
    public int? Episode { get; init; }

    public static P2pSource? Parse(string? infoHash, int? fileIndex, string? fileName, IEnumerable<string>? sources, string? magnet)
    {
        if (string.IsNullOrWhiteSpace(infoHash) && string.IsNullOrWhiteSpace(magnet))
        {
            return null;
        }

        if (fileIndex < 0 || fileName?.Length > 1024 || magnet?.Length > 32768)
        {
            throw new ArgumentException("Invalid P2P file selection or magnet.");
        }

        var trackers = new List<string>();
        InfoHash? v1 = null;
        InfoHash? v2 = null;
        if (!string.IsNullOrWhiteSpace(magnet))
        {
            if (!Uri.TryCreate(magnet, UriKind.Absolute, out var uri) || uri.Scheme != "magnet")
            {
                throw new ArgumentException("Invalid P2P magnet.");
            }

            foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = parameter.Split('=', 2);
                if (pair.Length != 2)
                {
                    continue;
                }

                var value = Uri.UnescapeDataString(pair[1].Replace("+", " ", StringComparison.Ordinal));
                if (pair[0] == "xt")
                {
                    if (value.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
                    {
                        if (v1 is not null) throw new ArgumentException("Repeated P2P hash.");
                        v1 = ParseHash(value[9..], false);
                    }
                    else if (value.StartsWith("urn:btmh:1220", StringComparison.OrdinalIgnoreCase))
                    {
                        if (v2 is not null) throw new ArgumentException("Repeated P2P hash.");
                        v2 = ParseHash(value[13..], true);
                    }
                    else
                    {
                        throw new ArgumentException("Unsupported P2P hash algorithm.");
                    }
                }
                else if (pair[0] == "tr")
                {
                    trackers.Add(value);
                }
            }

            if (v1 is null && v2 is null) throw new ArgumentException("Magnet has no supported P2P hash.");
        }

        if (!string.IsNullOrWhiteSpace(infoHash))
        {
            var parsed = ParseHash(infoHash.Trim(), infoHash.Trim().Length == 64);
            if ((v1 is not null || v2 is not null) && !parsed.Equals(v1) && !parsed.Equals(v2))
            {
                throw new ArgumentException("P2P source and magnet hashes disagree.");
            }

            if (infoHash.Trim().Length == 64) v2 = parsed;
            else v1 = parsed;
        }

        if (sources is not null)
        {
            foreach (var hint in sources.Take(65))
            {
                if (hint?.StartsWith("tracker:", StringComparison.OrdinalIgnoreCase) == true) trackers.Add(hint[8..]);
                else if (Uri.TryCreate(hint, UriKind.Absolute, out var hintUri) && hintUri.Scheme is "udp" or "http" or "https") trackers.Add(hint!);
            }
        }

        if (trackers.Count > 64) throw new ArgumentException("Too many P2P trackers.");
        foreach (var tracker in trackers) ValidateTracker(tracker);
        // Retain both hashes for hybrids: never silently substitute a conflicting hash.
        var canonical = v1 is not null && v2 is not null ? v1.ToHex() + ":" + v2.ToHex() : (v1 ?? v2)!.ToHex();
        return new P2pSource(canonical, fileIndex, fileName, trackers.Distinct(StringComparer.Ordinal).ToArray());
    }

    internal MagnetLink ToMagnet()
    {
        var parts = InfoHash.Split(':');
        InfoHashes hashes;
        if (parts.Length == 2)
        {
            hashes = new InfoHashes(ParseHash(parts[0], false), ParseHash(parts[1], true));
        }
        else if (parts.Length == 1)
        {
            var hash = ParseHash(parts[0], parts[0].Length == 64);
            hashes = parts[0].Length == 64 ? new InfoHashes(null, hash) : new InfoHashes(hash, null);
        }
        else throw new ArgumentException("Invalid P2P hash.");
        if (FileIndex < 0 || Trackers is null || Trackers.Length > 64) throw new ArgumentException("Invalid P2P source.");
        foreach (var tracker in Trackers) ValidateTracker(tracker);
        return new MagnetLink(hashes, announceUrls: Trackers);
    }

    internal static void ValidateTracker(string value)
    {
        if (value.Length > 4096 || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "udp") || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Scheme == "udp" && uri.Port <= 0))
        {
            throw new ArgumentException("Unsupported or invalid P2P tracker.");
        }
    }

    private static InfoHash ParseHash(string value, bool v2)
    {
        try
        {
            if (v2 && value.Length == 64 && value.All(Uri.IsHexDigit)) return MonoTorrent.InfoHash.FromHex(value);
            if (!v2 && value.Length == 40 && value.All(Uri.IsHexDigit)) return MonoTorrent.InfoHash.FromHex(value);
            if (!v2 && value.Length == 32 && value.All(c => "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".Contains(char.ToUpperInvariant(c)))) return MonoTorrent.InfoHash.FromBase32(value.ToUpperInvariant());
        }
        catch (FormatException)
        {
            throw new ArgumentException("Invalid P2P hash.");
        }

        throw new ArgumentException("P2P requires a SHA-1 hex/base32 or SHA-256 hex hash.");
    }
}
