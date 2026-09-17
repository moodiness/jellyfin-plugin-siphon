using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.Siphon.Downloads;

/// <summary>Flat, private files only. The queue's ownership marker is measured but never modified.</summary>
internal sealed class DownloadTransferWorkspace
{
    internal const int BufferSize = 64 * 1024;
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    private const string Marker = ".siphon-transfer-v1";
    private readonly string _directory;
    private readonly long _maximumBytes;
    private readonly Func<DownloadTransferProgress, Task> _progress;
    private long _storedBytes;
    private long _lastReport;

    internal DownloadTransferWorkspace(string directory, long maximumBytes, Func<DownloadTransferProgress, Task> progress)
    {
        if (maximumBytes <= 0) throw new IOException("The download storage budget is exhausted.");
        _directory = Path.GetFullPath(directory);
        _maximumBytes = maximumBytes;
        _progress = progress;
        CheckAncestors(_directory);
        Directory.CreateDirectory(_directory);
        var entries = Directory.GetFileSystemEntries(_directory);
        foreach (var entry in entries) CheckFile(entry);
        if (!File.Exists(PathFor(Marker)))
        {
            if (entries.Any(entry => Path.GetFileName(entry) != ".siphon-owner"))
                throw new IOException("The download directory is not an empty owned workspace.");
            using var marker = OpenWrite(Marker, FileMode.CreateNew);
        }
        _storedBytes = entries.Sum(entry => new FileInfo(entry).Length);
        EnsureCapacity(0);
    }

    internal long StoredBytes => _storedBytes;
    internal long RemainingBytes => _maximumBytes - _storedBytes;
    internal string DirectoryPath => _directory;

    internal string PathFor(string name)
    {
        if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || name is "." or ".."
            || name.IndexOfAny(['/', '\\', ':', '\0']) >= 0)
            throw new IOException("Invalid download workspace filename.");
        CheckAncestors(_directory);
        var path = Path.Combine(_directory, name);
        if (File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null) CheckFile(path);
        return path;
    }

    internal long Length(string name) => File.Exists(PathFor(name)) ? new FileInfo(PathFor(name)).Length : 0;

    internal void EnsureCapacity(long bytes)
    {
        if (bytes < 0 || bytes > RemainingBytes) throw new IOException("The download exceeds its storage budget.");
    }

    internal FileStream OpenWrite(string name, FileMode mode)
    {
        var path = PathFor(name);
        var stream = new FileStream(path, mode, FileAccess.Write, FileShare.Read, BufferSize, FileOptions.Asynchronous);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return stream;
    }

    internal async Task AppendAsync(FileStream output, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        EnsureCapacity(bytes.Length);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        _storedBytes += bytes.Length;
    }

    internal async Task WriteAsync(string name, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        Delete(name);
        EnsureCapacity(bytes.Length);
        await using var output = OpenWrite(name, FileMode.CreateNew);
        await AppendAsync(output, bytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal void Delete(string name)
    {
        if (name is Marker or ".siphon-owner") throw new IOException("Cannot remove a download ownership marker.");
        var path = PathFor(name);
        if (!File.Exists(path)) return;
        var bytes = new FileInfo(path).Length;
        File.Delete(path);
        _storedBytes -= bytes;
    }

    internal void Reset()
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(_directory))
        {
            CheckFile(path);
            var name = Path.GetFileName(path);
            if (name is Marker or ".siphon-owner") continue;
            if (name is not ("transfer.part" or "resume.json" or "resume.new" or "remux.part" or "audio-probe.json" or "audio-config.m4a")
                && !IsGeneratedName(name))
                throw new IOException("The download workspace contains an unowned file.");
            Delete(name);
        }
    }

    internal void Move(string source, string destination) => File.Move(PathFor(source), PathFor(destination), overwrite: false);

    internal async Task SaveResumeAsync(DownloadResumeState state, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
        await WriteAsync("resume.new", bytes, cancellationToken).ConfigureAwait(false);
        Delete("resume.json");
        Move("resume.new", "resume.json");
    }

    internal DownloadResumeState? ReadResume()
    {
        var path = PathFor("resume.json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 4096) return null;
        try { return JsonSerializer.Deserialize<DownloadResumeState>(File.ReadAllBytes(path)); }
        catch (JsonException) { return null; }
    }

    internal async Task ReportAsync(long received, long? total, bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now - _lastReport < 500) return;
        _lastReport = now;
        await _progress(new DownloadTransferProgress(received, total, _storedBytes)).ConfigureAwait(false);
    }

    internal static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static async ValueTask<int> ReadAsync(Stream input, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(IdleTimeout);
        try { return await input.ReadAsync(buffer, idle.Token).AsTask().WaitAsync(idle.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The download stopped receiving data.");
        }
    }

    private static bool IsGeneratedName(string name)
        => (name.StartsWith("h", StringComparison.Ordinal) && name.Length >= 9 && name.AsSpan(1, 5).IndexOfAnyExceptInRange('0', '9') < 0
            && name[6] == '.' && name[7..] is "m3u8" or "ts" or "mp4" or "m4s" or "m4a" or "aac" or "ac3" or "eac3" or "mp3" or "vtt" or "key")
            || name is "media.mkv" or "media.mp4" or "media.webm" or "media.avi" or "media.mov" or "media.m4v" or "media.ts" or "media.bin";

    private static void CheckAncestors(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.LinkTarget is not null || (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new IOException("Download paths cannot contain symbolic links.");
        }
    }

    private static void CheckFile(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("Download workspaces cannot contain directories or symbolic links.");
    }
}

internal sealed record DownloadResumeState(string Kind, string Fingerprint, string? ETag, long? TotalBytes, string Extension);
