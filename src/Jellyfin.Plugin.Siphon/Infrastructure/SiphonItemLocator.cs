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

    public ManagedItem? Find(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!Uri.TryCreate(path, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return _state.FindByPath(path);
        }
        if (_tokens is null) return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var tokenIndex = Array.FindIndex(segments, segment => segment.Equals("siphon", StringComparison.OrdinalIgnoreCase));
        if (tokenIndex < 0 || tokenIndex + 2 >= segments.Length || !segments[tokenIndex + 1].Equals("s", StringComparison.OrdinalIgnoreCase)) return null;

        return _tokens.TryReadItem(segments[tokenIndex + 2], out var key) ? _state.FindByKey(key) : null;
    }
}
