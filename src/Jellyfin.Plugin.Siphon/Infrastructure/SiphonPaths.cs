using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.Siphon.Infrastructure;

/// <summary>Version-independent plugin data, separate from plugin binaries and media libraries.</summary>
public sealed class SiphonPaths(IApplicationPaths paths)
{
    public string DataDirectory { get; } = Path.Combine(paths.DataPath, "siphon");
}
