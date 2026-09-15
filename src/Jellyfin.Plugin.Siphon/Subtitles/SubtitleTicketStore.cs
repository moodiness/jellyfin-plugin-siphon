using System.Security.Cryptography;

namespace Jellyfin.Plugin.Siphon.Subtitles;

internal sealed record SubtitleCandidate(
    string InstallationId,
    string ManifestDigest,
    string ItemPath,
    string ItemKey,
    string SourceId,
    Uri Url,
    string Language);

/// <summary>Server-only download capabilities; neither upstream IDs nor URLs leave this store.</summary>
internal sealed class SubtitleTicketStore(TimeProvider clock, int capacity, TimeSpan lifetime)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset Expires, SubtitleCandidate Candidate)> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    internal string Add(SubtitleCandidate candidate)
    {
        lock (_gate)
        {
            Prune();
            while (_entries.Count >= capacity) _entries.Remove(_order.Dequeue());
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _entries.Add(id, (clock.GetUtcNow().Add(lifetime), candidate));
            _order.Enqueue(id);
            return id;
        }
    }

    internal SubtitleCandidate? Get(string id)
    {
        if (id is not { Length: 64 } || id.Any(c => !char.IsAsciiHexDigit(c))) return null;
        lock (_gate)
        {
            Prune();
            return _entries.TryGetValue(id, out var entry) ? entry.Candidate : null;
        }
    }

    private void Prune()
    {
        var now = clock.GetUtcNow();
        while (_order.TryPeek(out var id) && _entries[id].Expires <= now)
        {
            _order.Dequeue();
            _entries.Remove(id);
        }
    }
}
