namespace Jellyfin.Plugin.Siphon.Downloads;

/// <summary>Flat private artifact directories require both ledger membership and an ownership marker.</summary>
internal sealed class DownloadQueueFiles(string root, string owner)
{
    private const string Marker = ".siphon-owner";
    private readonly object _gate = new();

    internal string Prepare(DownloadJob job)
    {
        lock (_gate)
        {
            EnsureRoot();
            var directory = DirectoryFor(job);
            if (!Path.Exists(directory))
            {
                CreatePrivateDirectory(directory);
                WriteMarker(directory, job.StorageKey);
            }
            ValidateDirectory(directory, job.StorageKey);
            return directory;
        }
    }

    internal long Measure(DownloadJob job)
    {
        lock (_gate)
        {
            ValidateRootIfPresent();
            var directory = DirectoryFor(job);
            if (!Path.Exists(directory)) return 0;
            ValidateDirectory(directory, job.StorageKey);
            return Entries(directory).Sum(file => file.Length);
        }
    }

    internal FileStream Open(DownloadJob job)
    {
        lock (_gate)
        {
            ValidateRootIfPresent();
            var directory = DirectoryFor(job);
            ValidateDirectory(directory, job.StorageKey);
            if (job.FileName is null || !SafeName(job.FileName)) throw new InvalidDataException("Download artifact is unavailable.");
            var path = Path.Combine(directory, job.FileName);
            CheckPath(path);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
    }

    internal long Finish(DownloadJob job, DownloadTransferResult result)
    {
        lock (_gate)
        {
            ValidateRootIfPresent();
            var directory = DirectoryFor(job);
            ValidateDirectory(directory, job.StorageKey);
            if (!SafeName(result.FileName) || result.FileName == Marker || result.Bytes <= 0)
                throw new InvalidDataException("The transfer did not produce a valid file.");
            var files = Entries(directory);
            if (files.FirstOrDefault(file => file.Name == result.FileName) is not { } completed || completed.Length != result.Bytes
                || files.Sum(file => file.Length) > job.ReservedBytes)
                throw new InvalidDataException("The transfer exceeded its reserved storage or produced an invalid file.");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(completed.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return files.Sum(file => file.Length);
        }
    }

    internal long RetainCompleted(DownloadJob job)
    {
        lock (_gate)
        {
            ValidateRootIfPresent();
            var directory = DirectoryFor(job);
            ValidateDirectory(directory, job.StorageKey);
            var files = Entries(directory);
            if (job.State != DownloadJobState.Completed || job.FileName is null
                || files.FirstOrDefault(file => file.Name == job.FileName) is not { } completed || completed.Length != job.TotalBytes)
                throw new InvalidDataException("The completed download is unavailable.");
            // Only a durable Completed record permits discarding resumable transfer metadata.
            foreach (var file in files.Where(file => file.Name != Marker && file.Name != job.FileName)) file.Delete();
            return Entries(directory).Sum(file => file.Length);
        }
    }

    internal void Delete(DownloadJob job)
    {
        lock (_gate)
        {
            ValidateRootIfPresent();
            var directory = DirectoryFor(job);
            if (!Path.Exists(directory)) return;
            ValidateDirectory(directory, job.StorageKey);
            var files = Entries(directory); // Validate the entire directory before deleting anything.
            foreach (var file in files.Where(file => file.Name != Marker)) file.Delete();
            File.Delete(Path.Combine(directory, Marker));
            Directory.Delete(directory, recursive: false);
        }
    }

    private string DirectoryFor(DownloadJob job) => Path.Combine(root, job.Id.ToString("N"));
    private void EnsureRoot()
    {
        CheckPath(Path.GetDirectoryName(root)!);
        if (!Path.Exists(root))
        {
            CreatePrivateDirectory(root);
            WriteMarker(root, owner);
        }
        ValidateDirectory(root, owner);
    }
    private void ValidateRootIfPresent()
    {
        CheckPath(Path.GetDirectoryName(root)!);
        if (Path.Exists(root)) ValidateDirectory(root, owner);
    }
    private static void CreatePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void WriteMarker(string directory, string value)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(Path.Combine(directory, Marker), options);
        stream.Write(System.Text.Encoding.ASCII.GetBytes(value));
        stream.Flush(flushToDisk: true);
    }
    private static void ValidateDirectory(string directory, string expected)
    {
        CheckPath(directory);
        if (!Directory.Exists(directory)) throw new InvalidDataException("Download storage is unavailable.");
        var marker = Path.Combine(directory, Marker);
        CheckPath(marker);
        if (!File.Exists(marker) || new FileInfo(marker).Length != 64 || File.ReadAllText(marker) != expected)
            throw new InvalidDataException("Download storage ownership could not be verified.");
    }
    private static FileInfo[] Entries(string directory)
    {
        var entries = new DirectoryInfo(directory).GetFileSystemInfos();
        if (entries.Length > 100_000 || entries.Any(entry => entry is not FileInfo || (entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry.LinkTarget is not null))
            throw new InvalidDataException("Download storage contains unsafe entries.");
        return entries.Cast<FileInfo>().ToArray();
    }
    internal static bool SafeName(string value)
        => value is { Length: > 0 and <= 160 } && value is not "." and not ".." && value[0] != '.'
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    internal static void CheckPath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            var entry = new FileInfo(current);
            if (entry.LinkTarget is not null || entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0
                || Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Download storage cannot contain symbolic links.");
            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent;
        }
    }
}
