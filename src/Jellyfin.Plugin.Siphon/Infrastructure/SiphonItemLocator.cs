using Jellyfin.Plugin.Siphon.Identity;

namespace Jellyfin.Plugin.Siphon.Infrastructure;

/// <summary>Resolves stored item paths and signed native playback URLs.</summary>
public sealed class SiphonItemLocator
{
    private readonly ISiphonStateStore _state;
    private readonly CapabilityTokenService? _tokens;

    public SiphonItemLocator(ISiphonStateStore state, CapabilityTokenService tokens)
    {
        _state = state;
        _tokens = tokens;
    }

    internal SiphonItemLocator(ISiphonStateStore state)
    {
        _state = state;
    }

    public ManagedItem? Find(string? path, out string? sourceId)
    {
        sourceId = null;
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!Uri.TryCreate(path, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return _state.FindByPath(path);
        }
        if (_tokens is null) return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Match the endpoint suffix: a reverse-proxy base path may itself contain
        // a "Siphon" segment and must not shadow the actual native playback route.
        var tokenIndex = segments.Length - 3;
        if (tokenIndex < 0 || !segments[tokenIndex].Equals("siphon", StringComparison.OrdinalIgnoreCase)) return null;
        var resource = segments[tokenIndex + 1];
        var token = segments[tokenIndex + 2];
        if (resource.Equals("s", StringComparison.OrdinalIgnoreCase) && _tokens.TryReadItem(token, out var key))
            return _state.FindByKey(key);
        if (resource.Equals("source", StringComparison.OrdinalIgnoreCase) && _tokens.TryReadSource(token, out key, out var selected))
        {
            sourceId = selected;
            return _state.FindByKey(key);
        }
        return null;
    }
}
