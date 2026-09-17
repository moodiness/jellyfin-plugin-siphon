using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Siphon.Infrastructure;

namespace Jellyfin.Plugin.Siphon.Downloads;

/// <summary>Atomic, fail-closed ledger. State transitions and reservations share one commit boundary.</summary>
public sealed class DownloadQueueStore
{
    internal const int MaximumJobs = 10_000;
    private const long MaximumLedgerBytes = 32 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _path;
    private Document _document;
    internal DownloadQueueFiles Files { get; }

    public DownloadQueueStore(SiphonPaths paths) : this(paths.DataDirectory) { }

    internal DownloadQueueStore(string dataDirectory)
    {
        DownloadQueueFiles.CheckPath(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        DownloadQueueFiles.CheckPath(dataDirectory);
        _path = Path.Combine(dataDirectory, "download-queue.json");
        if (File.Exists(_path))
        {
            DownloadQueueFiles.CheckPath(_path);
            using var stream = File.OpenRead(_path);
            if (stream.Length > MaximumLedgerBytes) throw new InvalidDataException("Download queue storage exceeds its limit.");
            _document = JsonSerializer.Deserialize<Document>(stream) ?? throw new InvalidDataException("Download queue storage is empty.");
            if (_document.Version != 1 || !Digest(_document.StorageKey) || _document.Jobs is null || _document.Jobs.Length > MaximumJobs
                || _document.Jobs.Any(job => !Valid(job)) || _document.Jobs.Select(job => job.Id).Distinct().Count() != _document.Jobs.Length)
                throw new InvalidDataException("Download queue storage has an unsupported format.");
        }
        else
        {
            // Do not adopt an existing directory if the ownership ledger is missing.
            if (Path.Exists(Path.Combine(dataDirectory, "downloads"))) throw new InvalidDataException("Download storage ownership is unavailable.");
            _document = new() { StorageKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) };
            Commit(_document);
        }
        Files = new(Path.Combine(dataDirectory, "downloads"), _document.StorageKey);
    }

    internal DownloadJob[] Snapshot() { lock (_gate) return (DownloadJob[])_document.Jobs.Clone(); }
    internal DownloadJob? Find(Guid id, Guid userId)
    {
        lock (_gate) return _document.Jobs.FirstOrDefault(job => job.Id == id && job.UserId == userId && !job.DeleteRequested);
    }

    internal DownloadJob Add(DownloadJob job, int maximumPerUser)
    {
        lock (_gate)
        {
            if (_document.Jobs.Length >= MaximumJobs || _document.Jobs.Count(value => value.UserId == job.UserId) >= maximumPerUser)
                throw new DownloadQueueException(409, "The download queue limit has been reached. Delete an existing job first.");
            if (_document.Jobs.Any(value => !value.DeleteRequested && value.UserId == job.UserId && value.ItemKey == job.ItemKey
                && value.SourceId == job.SourceId && value.State is DownloadJobState.Queued or DownloadJobState.Running))
                throw new DownloadQueueException(409, "This version is already queued.");
            if (!Valid(job)) throw new InvalidDataException("Invalid download job.");
            Commit(_document with { Jobs = [.. _document.Jobs, job] });
            return job;
        }
    }

    internal void Recover()
    {
        lock (_gate)
        {
            var jobs = _document.Jobs.Select(job => job.State == DownloadJobState.Running
                ? job with { State = job.DeleteRequested ? DownloadJobState.Cancelled : DownloadJobState.Queued, ReservedBytes = 0, UpdatedUtc = DateTimeOffset.UtcNow }
                : job).ToArray();
            if (!jobs.SequenceEqual(_document.Jobs)) Commit(_document with { Jobs = jobs });
        }
    }

    internal DownloadJob? TryStart(Guid id, int maximumConcurrent, long maximumStorage, long maximumFile)
    {
        lock (_gate)
        {
            var job = _document.Jobs.FirstOrDefault(value => value.Id == id && !value.DeleteRequested && value.State == DownloadJobState.Queued);
            if (job is null || _document.Jobs.Count(value => value.State == DownloadJobState.Running) >= maximumConcurrent) return null;
            var reservation = Math.Min(maximumFile, maximumStorage);
            if (reservation <= 0 || job.StoredBytes > reservation) return null;
            var committed = _document.Jobs.Sum(value => value.State == DownloadJobState.Running ? value.ReservedBytes : value.StoredBytes);
            if (committed - job.StoredBytes > maximumStorage - reservation) return null;
            return Replace(job with { State = DownloadJobState.Running, ReservedBytes = reservation, Error = null, UpdatedUtc = DateTimeOffset.UtcNow });
        }
    }

    internal DownloadJob? Update(Guid id, Func<DownloadJob, DownloadJob?> transition)
    {
        lock (_gate)
        {
            var current = _document.Jobs.FirstOrDefault(job => job.Id == id);
            if (current is null) return null;
            var next = transition(current);
            return next is null || next == current ? current : Replace(next);
        }
    }

    internal void RemoveDeleted(Guid id)
    {
        lock (_gate)
        {
            if (!_document.Jobs.Any(job => job.Id == id && job.DeleteRequested && job.State != DownloadJobState.Running)) return;
            Commit(_document with { Jobs = _document.Jobs.Where(job => job.Id != id).ToArray() });
        }
    }

    private DownloadJob Replace(DownloadJob job)
    {
        if (!Valid(job)) throw new InvalidDataException("Invalid download transition.");
        Commit(_document with { Jobs = _document.Jobs.Select(value => value.Id == job.Id ? job : value).ToArray() });
        return job;
    }

    private void Commit(Document document)
    {
        DownloadQueueFiles.CheckPath(Path.GetDirectoryName(_path)!);
        if (Path.Exists(_path)) DownloadQueueFiles.CheckPath(_path);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(file, document);
                if (file.Length > MaximumLedgerBytes) throw new InvalidDataException("Download queue storage exceeds its limit.");
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
            _document = document;
        }
        finally { File.Delete(temporary); }
    }

    private static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static bool Valid(DownloadJob? job)
        => job is not null && job.Id != Guid.Empty && job.UserId != Guid.Empty && job.ItemId != Guid.Empty && job.MediaSourceId != Guid.Empty
            && Digest(job.TransportKey)
            && job.ItemKey is { Length: > 0 and <= 1024 } && Digest(job.SourceId) && Digest(job.ProfileKey) && Digest(job.StorageKey)
            && job.Name is { Length: <= 256 } && Enum.IsDefined(job.State) && job.BytesReceived >= 0 && job.StoredBytes >= 0
            && job.StoredBytes <= 16L * 1024 * 1024 * 1024 * 1024 && job.ReservedBytes is >= 0 and <= 16L * 1024 * 1024 * 1024 * 1024
            && (job.TotalBytes is null or >= 0) && (job.Error is null || job.Error.Length <= 256)
            && (job.FileName is null || DownloadQueueFiles.SafeName(job.FileName))
            && (job.ContentType is null || job.ContentType is "video/mp4" or "video/x-matroska" or "video/webm" or "application/octet-stream")
            && (job.State != DownloadJobState.Completed || job.FileName is not null && job.ContentType is not null)
            && (job.State == DownloadJobState.Running ? job.ReservedBytes > 0 : job.ReservedBytes == 0);

    private sealed record Document
    {
        [JsonRequired]
        public int Version { get; init; } = 1;
        [JsonRequired]
        public string StorageKey { get; init; } = string.Empty;
        [JsonRequired]
        public DownloadJob[] Jobs { get; init; } = [];
    }
}
