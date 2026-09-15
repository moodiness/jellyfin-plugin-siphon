using Jellyfin.Plugin.Siphon.Identity;

namespace Jellyfin.Plugin.Siphon.Infrastructure;

/// <summary>Resolves Siphon items from virtual channel paths and capability URLs.</summary>
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
        var item = _state.FindByPath(path);
        if (item is not null || _tokens is null || !Uri.TryCreate(path, UriKind.Absolute, out var uri)) return item;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var tokenIndex = Array.FindIndex(segments, segment => segment.Equals("siphon", StringComparison.OrdinalIgnoreCase));
        if (tokenIndex < 0 || tokenIndex + 2 >= segments.Length || !segments[tokenIndex + 1].Equals("s", StringComparison.OrdinalIgnoreCase)) return null;

        return _tokens.TryReadItem(segments[tokenIndex + 2], out var key) ? _state.FindByKey(key) : null;
    }
}
