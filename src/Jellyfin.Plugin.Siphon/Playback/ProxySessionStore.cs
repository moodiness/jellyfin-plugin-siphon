using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.Siphon.Playback;

public sealed record ProxySession(string Token, ResolvedStream Source, string ItemKey)
{
    internal string RootToken { get; init; } = Token;
    public bool Download { get; init; }
    public DateTimeOffset? DownloadExpiresAtUtc { get; init; }
}

internal readonly record struct NativeMediaClassification(string? ETag, DateTimeOffset? LastModified, long? Length);

/// <summary>Bounded server-only leases. Tokens contain no resource or credential information.</summary>
public sealed class ProxySessionStore(ConfigurationAccessor configuration)
{
    private const int MaximumSessions = 65536;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Token, DateTimeOffset Expires)> _defaults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<Uri, string>> _children = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid User, string Playback, Guid Version), (string Token, byte[] Source, DateTimeOffset Expires)> _playbacks = new();
    private readonly Dictionary<string, NativeMediaClassification> _nativeMedia = new(StringComparer.Ordinal);

    private DateTimeOffset _nextPrune;

    private static bool SameHeaders(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second)
        => first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var value) && pair.Value == value);

    public ProxySession Create(ManagedItem item, ResolvedStream source, bool download = false)
    {
        lock (_gate)
        {
            var existing = _sessions.Values.FirstOrDefault(e => e.Session.RootToken == e.Session.Token && e.Session.ItemKey == item.Key
                && e.Session.Source.UserId == source.UserId && e.Session.Download == download
                && e.Session.Source.Id == source.Id && e.Session.Source.Url == source.Url
                && (!download || e.Session.DownloadExpiresAtUtc > DateTimeOffset.UtcNow)
                && SameHeaders(e.Session.Source.RequestHeaders, source.RequestHeaders) && e.Expires > DateTimeOffset.UtcNow);
            if (existing is not null)
            {
                existing.Expires = DateTimeOffset.UtcNow + Lifetime;
                return existing.Session;
            }

            return Add(new ProxySession(NewToken(), source, item.Key) { Download = download, DownloadExpiresAtUtc = download ? DateTimeOffset.UtcNow.AddMinutes(5) : null });
        }
    }
    internal void MarkNativeMedia(ProxySession session, string? etag, DateTimeOffset? lastModified, long? length)
    {
        lock (_gate)
        {
            if (!session.Download && session.Source.P2p is null
                && _sessions.TryGetValue(session.RootToken, out var root)
                && root.Session.RootToken == session.RootToken)
            {
                var previous = _nativeMedia.GetValueOrDefault(session.RootToken);
                _nativeMedia[session.RootToken] = new(etag ?? previous.ETag, lastModified ?? previous.LastModified, length ?? previous.Length);
            }
        }
    }

    internal NativeMediaClassification? GetNativeMedia(ProxySession session)
    {
        lock (_gate)
        {
            return _sessions.ContainsKey(session.RootToken)
                && _nativeMedia.TryGetValue(session.RootToken, out var media) ? media : null;
        }
    }



    internal void BindPlayback(Guid userId, string? playSessionId, MediaSourceInfo source)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(playSessionId) || playSessionId.Length > 128
            || !Guid.TryParse(source.Id, out var version) || !Uri.TryCreate(source.EncoderPath, UriKind.Absolute, out var uri)) return;
        var segments = uri.AbsolutePath.Split('/');
        if (segments.Length < 3) return;
        var token = segments[^2];
        var snapshot = JsonSerializer.SerializeToUtf8Bytes(source);
        if (snapshot.Length > 1024 * 1024) throw new InvalidOperationException("Playback snapshot exceeds size limit.");
        lock (_gate)
        {
            var session = Get(token);
            if (session is null || session.Download || session.RootToken != token || session.Source.UserId != userId) return;
            var now = DateTimeOffset.UtcNow;
            foreach (var expired in _playbacks.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
                _playbacks.Remove(expired);
            var key = (userId, playSessionId, version);
            if (_playbacks.Count >= 1024 && !_playbacks.ContainsKey(key))
                throw new InvalidOperationException("Playback binding capacity reached.");
            _playbacks[key] = (token, snapshot, now + Lifetime);
        }
    }

    internal (ProxySession Session, MediaSourceInfo Source)? GetPlayback(Guid userId, string playSessionId, Guid version)
    {
        lock (_gate)
        {
            var key = (userId, playSessionId, version);
            if (!_playbacks.TryGetValue(key, out var saved)) return null;
            if (saved.Expires <= DateTimeOffset.UtcNow) { _playbacks.Remove(key); return null; }
            var session = Get(saved.Token);
            if (session is null) { _playbacks.Remove(key); return null; }
            // Native helpers mutate DTOs while choosing tracks and output formats.
            // Keep the successful opening snapshot private and return an independent copy.
            return (session, JsonSerializer.Deserialize<MediaSourceInfo>(saved.Source)!);
        }
    }

    internal ProxySession? GetDefault(string key)
    {
        lock (_gate)
        {
            return _defaults.TryGetValue(key, out var entry) && entry.Expires > DateTimeOffset.UtcNow ? Get(entry.Token) : null;
        }
    }

    internal void SetDefault(ProxySession session, string? selectionKey = null)
    {
        lock (_gate)
        {
            _defaults[selectionKey ?? session.Source.UserId.ToString("N") + "|" + session.ItemKey] = (session.Token, DateTimeOffset.UtcNow.AddSeconds(45));
        }
    }

    public void InvalidateDefaults(string itemKey, Guid userId)
    {
        lock (_gate)
        {
            foreach (var key in _defaults.Where(pair => _sessions.TryGetValue(pair.Value.Token, out var entry)
                && entry.Session.ItemKey == itemKey && entry.Session.Source.UserId == userId).Select(pair => pair.Key).ToArray())
                _defaults.Remove(key);
        }
    }

    public ProxySession? Get(string token)
    {
        if (token.Length != 64 || !token.All(char.IsAsciiHexDigit))
        {
            return null;
        }

        lock (_gate)
        {
            if (!_sessions.TryGetValue(token, out var entry))
            {
                return null;
            }

            if (entry.Session.DownloadExpiresAtUtc <= DateTimeOffset.UtcNow || entry.Expires <= DateTimeOffset.UtcNow || !_sessions.TryGetValue(entry.Session.RootToken, out var root) || root.Expires <= DateTimeOffset.UtcNow)
            {
                Remove(token);
                return null;
            }

            entry.Expires = root.Expires = DateTimeOffset.UtcNow + Lifetime;
            return entry.Session;
        }
    }

    internal ProxySession CreateChild(ProxySession parent, Uri uri)
    {
        if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("Unsupported playlist resource.");
        }

        lock (_gate)
        {
            if (!_sessions.TryGetValue(parent.RootToken, out var root))
            {
                throw new InvalidDataException("Expired playlist lease.");
            }

            if (!_children.TryGetValue(parent.RootToken, out var children))
            {
                children = new Dictionary<Uri, string>();
                _children.Add(parent.RootToken, children);
            }

            if (children.TryGetValue(uri, out var existingToken) && _sessions.TryGetValue(existingToken, out var existing))
            {
                existing.Expires = DateTimeOffset.UtcNow + Lifetime;
                return existing.Session;
            }

            // Bound one playlist lineage independently so a malformed addon cannot fill the server cache.
            if (children.Count >= 8192)
            {
                foreach (var child in children.Values.Select(token => _sessions.GetValueOrDefault(token)).OfType<Entry>().OrderBy(e => e.Expires).Take(4096).ToArray())
                {
                    Remove(child.Session.Token);
                }
            }

            // Credentials apply only to the original authority, never arbitrary playlist hosts.
            var headers = root.Session.Source.P2p is null && root.Session.Source.Url.GetLeftPart(UriPartial.Authority).Equals(uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
                ? root.Session.Source.RequestHeaders : new Dictionary<string, string>();
            var childSession = Add(new ProxySession(NewToken(), parent.Source with { Url = uri, RequestHeaders = headers, FileName = null, Size = null }, parent.ItemKey) { RootToken = parent.RootToken });
            children[uri] = childSession.Token;
            return childSession;
        }
    }

    internal bool TryAcquire(ProxySession session)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session.RootToken, out var root) || root.Active >= 8)
            {
                return false;
            }

            root.Active++;
            return true;
        }
    }

    internal void Complete(ProxySession session)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(session.RootToken, out var root))
            {
                root.Active = Math.Max(0, root.Active - 1);
            }
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _sessions.Clear();
            _defaults.Clear();
            _children.Clear();
            _playbacks.Clear();
            _nativeMedia.Clear();
        }
    }

    public void Invalidate(Func<Guid, bool> changedUser)
    {
        lock (_gate)
        {
            var roots = _sessions.Values.Where(entry => entry.Session.Token == entry.Session.RootToken
                && changedUser(entry.Session.Source.UserId)).Select(entry => entry.Session.Token).ToArray();
            foreach (var token in roots) Remove(token);
        }
    }

    public void Release(string token)
    {
        lock (_gate)
        {
            Remove(token);
        }
    }

    public string GetUrl(ProxySession session)
        => configuration.Current.PublicBaseUrl.TrimEnd('/') + "/Siphon/media/" + GetMediaPath(session);

    internal static string GetMediaPath(ProxySession session)
    {
        var extension = session.Source.P2p is null ? Path.GetExtension(session.Source.Url.AbsolutePath).ToLowerInvariant() : string.Empty;
        if (extension.Length == 0 && session.Source.FileName is not null)
        {
            extension = Path.GetExtension(session.Source.FileName).ToLowerInvariant();
        }

        // ffmpeg rejects HLS segment URLs without a recognized media extension.
        // Only the extension is published, never the upstream filename or query.
        if (extension is not (".m3u8" or ".m3u" or ".ts" or ".m4s" or ".mp4" or ".m4a" or ".aac" or ".mp3" or ".vtt" or ".webm" or ".mkv" or ".key"))
        {
            extension = ".ts";
        }

        return session.Token + "/stream" + extension;
    }

    private ProxySession Add(ProxySession session)
    {
        var now = DateTimeOffset.UtcNow;
        if (now >= _nextPrune)
        {
            foreach (var token in _sessions.Where(p => p.Value.Expires <= now && p.Value.Active == 0).Select(p => p.Key).ToArray())
            {
                Remove(token);
            }

            _nextPrune = now.AddMinutes(1);
        }

        if (_sessions.Count >= MaximumSessions)
        {
            throw new InvalidOperationException("Proxy lease capacity reached.");
        }

        _sessions.Add(session.Token, new Entry(session, now + Lifetime));
        return session;
    }

    private void Remove(string token)
    {
        if (!_sessions.Remove(token, out var entry))
        {
            return;
        }

        foreach (var key in _playbacks.Where(pair => pair.Value.Token == token).Select(pair => pair.Key).ToArray())
            _playbacks.Remove(key);
        foreach (var key in _defaults.Where(p => p.Value.Token == token).Select(p => p.Key).ToArray())
        {
            _defaults.Remove(key);
        }

        if (entry.Session.RootToken == token)
        {
            _nativeMedia.Remove(token);
            foreach (var child in _sessions.Where(p => p.Value.Session.RootToken == token).Select(p => p.Key).ToArray())
            {
                _sessions.Remove(child);
            }
            _children.Remove(token);
        }
        else if (_children.TryGetValue(entry.Session.RootToken, out var siblings))
        {
            siblings.Remove(entry.Session.Source.Url);
        }
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private sealed class Entry(ProxySession session, DateTimeOffset expires)
    {
        public ProxySession Session { get; } = session;
        public DateTimeOffset Expires { get; set; } = expires;
        public int Active { get; set; }
    }
}
