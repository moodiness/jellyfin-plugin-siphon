using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.Siphon.Infrastructure;

/// <summary>Version-independent plugin data and managed library storage, separate from plugin binaries.</summary>
public sealed class SiphonPaths(IApplicationPaths paths)
{
    public string DataDirectory { get; } = Path.Combine(paths.DataPath, "siphon");
}
