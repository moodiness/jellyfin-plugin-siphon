using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.PieceWriter;
using ReusableTasks;

namespace Jellyfin.Plugin.Siphon.P2p;

internal static class P2pStorage
{
    internal static void CheckPath(string root, string path)
    {
        root = Path.GetFullPath(root);
        path = Path.GetFullPath(path);
        if (path != root && !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("P2P path is outside its storage directory.");

        for (var cursor = path; !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
        {
            try
            {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("P2P storage cannot contain symbolic links.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static void ValidateRelativePath(string path)
    {
        if (Path.DirectorySeparatorChar == '\\') path = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || Path.IsPathRooted(path)
            || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl)
            || path.Split('/').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
            throw new IOException("Torrent contains an unsafe file path.");
    }

    internal static long Size(string root, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckPath(root, root);
        if (!Directory.Exists(root)) return 0;
        long bytes = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckPath(root, entry);
            bytes = checked(bytes + (Directory.Exists(entry) ? Size(entry, cancellationToken) : new FileInfo(entry).Length));
        }

        return bytes;
    }

    internal static void Delete(string root, string directory)
    {
        CheckPath(root, directory);
        if (!Directory.Exists(directory)) return;
        // Inspect every descendant before recursive deletion, so a link cannot redirect cleanup.
        _ = Size(directory);
        Directory.Delete(directory, true);
    }

    internal static int MapFileIndex(Torrent torrent, ReadOnlySpan<byte> metadata, int index)
    {
        var root = BEncodedValue.Decode<BEncodedDictionary>(metadata);
        if (!root.TryGetValue("info", out var infoValue) || infoValue is not BEncodedDictionary info)
            throw new IOException("P2P metadata has no info dictionary.");
        if (!info.TryGetValue("files", out var value) || value is not BEncodedList rawFiles) return index;
        if (index < 0 || index >= rawFiles.Count || rawFiles[index] is not BEncodedDictionary file)
            throw new InvalidOperationException("P2P file index is outside the torrent.");
        // MonoTorrent folds BEP-47 padding entries into adjacent files. Addon fileIdx still refers to the raw list.
        if (file.TryGetValue("attr", out var attribute) && attribute is BEncodedString attributes
            && (attributes.Text.Contains('p') || attributes.Text.Contains('l')))
            throw new InvalidOperationException("P2P file index identifies padding or a symbolic link.");
        if (!file.TryGetValue("path.utf-8", out var pathValue)) file.TryGetValue("path", out pathValue);
        if (pathValue is not BEncodedList parts || parts.Any(part => part is not BEncodedString))
            throw new IOException("P2P indexed file has no valid path.");
        var path = string.Join(Path.DirectorySeparatorChar, parts.Cast<BEncodedString>().Select(part => part.Text));
        var matches = Enumerable.Range(0, torrent.Files.Count).Where(candidate => torrent.Files[candidate].Path == path).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidOperationException("P2P file index cannot be mapped unambiguously.");
    }

    internal static int SelectFile(IReadOnlyList<(string Path, long Length)> files, P2pSource source)
    {
        if (source.FileIndex is int index)
        {
            if (index < 0 || index >= files.Count || !IsVideo(files[index].Path) || files[index].Length <= 0)
                throw new InvalidOperationException("P2P file index does not identify a playable video.");
            return index;
        }

        var candidates = Enumerable.Range(0, files.Count).Where(i => files[i].Length > 0 && IsVideo(files[i].Path)).ToArray();
        if (!string.IsNullOrWhiteSpace(source.FileName))
        {
            var name = source.FileName.Replace('\\', '/');
            var exact = candidates.Where(i => string.Equals(files[i].Path.Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (exact.Length == 1) return exact[0];
            candidates = candidates.Where(i => string.Equals(Path.GetFileName(files[i].Path), Path.GetFileName(name), StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        else if (candidates.Length > 1 && source.Season is int season && source.Episode is int episode)
        {
            var pattern = $@"(?i)(?<![a-z0-9])(?:S0*{season}E0*{episode}(?!\d)|0*{season}x0*{episode}(?!\d))";
            candidates = candidates.Where(i => System.Text.RegularExpressions.Regex.IsMatch(files[i].Path, pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))).ToArray();
        }

        if (candidates.Length != 1) throw new InvalidOperationException("P2P source must identify one unambiguous video file.");
        return candidates[0];
    }

    private static bool IsVideo(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mkv" or ".mp4" or ".m4v" or ".avi" or ".mov" or ".webm" or ".ts" or ".m2ts" or ".mpg" or ".mpeg";
}

/// <summary>Checks the final engine paths at the disk boundary, not only the torrent's suggested names.</summary>
internal sealed class GuardedPieceWriter(string root, int maximumOpenFiles) : IPieceWriter
{
    private readonly DiskWriter _inner = new(maximumOpenFiles);
    public int OpenFiles => _inner.OpenFiles;
    public int MaximumOpenFiles => _inner.MaximumOpenFiles;
    public void Dispose() => _inner.Dispose();
    public ReusableTask CloseAsync(ITorrentManagerFile file) => _inner.CloseAsync(file);
    public ReusableTask FlushAsync(ITorrentManagerFile file) => _inner.FlushAsync(file);
    public ReusableTask SetMaximumOpenFilesAsync(int value) => _inner.SetMaximumOpenFilesAsync(value);
    public ReusableTask<bool> ExistsAsync(ITorrentManagerFile file)
    {
        P2pStorage.CheckPath(root, file.FullPath);
        return _inner.ExistsAsync(file);
    }

    public ReusableTask MoveAsync(ITorrentManagerFile file, string fullPath, bool overwrite)
    {
        P2pStorage.CheckPath(root, file.FullPath);
        P2pStorage.CheckPath(root, fullPath);
        return _inner.MoveAsync(file, fullPath, overwrite);
    }

    public ReusableTask<int> ReadAsync(ITorrentManagerFile file, long offset, Memory<byte> buffer)
    {
        P2pStorage.CheckPath(root, file.FullPath);
        return _inner.ReadAsync(file, offset, buffer);
    }

    public ReusableTask WriteAsync(ITorrentManagerFile file, long offset, ReadOnlyMemory<byte> buffer)
    {
        P2pStorage.CheckPath(root, file.FullPath);
        if (offset < 0 || offset > file.Length || buffer.Length > file.Length - offset) throw new IOException("P2P write exceeds its reservation.");
        return _inner.WriteAsync(file, offset, buffer);
    }
}
