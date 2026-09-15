using System.Text;

namespace Jellyfin.Plugin.Siphon.Identity;

/// <summary>Filesystem boundaries and portable display names for plugin-owned media.</summary>
internal static class ManagedPath
{
    internal static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool IsWithin(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return full.Equals(parent, Comparison) || full.StartsWith(parent + Path.DirectorySeparatorChar, Comparison);
    }

    public static void EnsureNoLinks(string path)
    {
        for (var info = new DirectoryInfo(Path.GetFullPath(path)); info is not null; info = info.Parent)
        {
            if (info.LinkTarget is not null)
            {
                throw new IOException("Siphon managed paths cannot contain symbolic links.");
            }
        }
    }

    public static string SafeName(string name)
    {
        var result = new StringBuilder(100);
        foreach (var rune in name.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            if (result.Length >= 100)
            {
                break;
            }

            if (Rune.IsControl(rune) || "<>:\"/\\|?*[]{}".Contains(rune.ToString(), StringComparison.Ordinal))
            {
                result.Append('_');
            }
            else
            {
                result.Append(rune.ToString());
            }
        }

        var value = result.ToString().Trim(' ', '.');
        return value.Length == 0 ? "Untitled" : value;
    }
}
