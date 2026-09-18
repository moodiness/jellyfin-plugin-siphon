using System.Text.Json;
using Jellyfin.Plugin.Siphon.Downloads;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Downloads;

public sealed class DownloadQueueStoreTests : IDisposable
{
    private readonly string _directory = NewDirectory();

    [Fact]
    public void RestartPreservesExactSelectionWithoutResurrectingCancelledOrDeletedJobs()
    {
        var store = new DownloadQueueStore(_directory);
        var running = store.Add(Job(), 20);
        var cancelled = store.Add(Job(), 20);
        var deleted = store.Add(Job(), 20);
        Assert.NotNull(store.TryStart(running.Id, 2, 4000, 1000));
        store.Update(cancelled.Id, job => job with { State = DownloadJobState.Cancelled });
        store.Update(deleted.Id, job => job with { State = DownloadJobState.Cancelled, DeleteRequested = true });

        var reopened = new DownloadQueueStore(_directory);
        reopened.Recover();
        var resumed = reopened.Find(running.Id, running.UserId)!;
        Assert.Equal(DownloadJobState.Queued, resumed.State);
        Assert.Equal(running.SourceId, resumed.SourceId);
        Assert.Equal(running.MediaSourceId, resumed.MediaSourceId);
        Assert.Equal(running.ProfileKey, resumed.ProfileKey);
        Assert.Equal(DownloadJobState.Cancelled, reopened.Find(cancelled.Id, cancelled.UserId)!.State);
        Assert.Null(reopened.TryStart(cancelled.Id, 2, 4000, 1000));
        Assert.Null(reopened.Find(deleted.Id, deleted.UserId));
        Assert.Null(reopened.TryStart(deleted.Id, 2, 4000, 1000));
        Assert.Null(reopened.Find(running.Id, Guid.NewGuid()));
    }

    [Fact]
    public async Task ConcurrentAdmissionsReserveCapacityAndRetainedFilesContinueToCount()
    {
        var store = new DownloadQueueStore(_directory);
        var jobs = Enumerable.Range(0, 12).Select(_ => store.Add(Job(), 20)).ToArray();
        var attempts = await Task.WhenAll(jobs.Select(job => Task.Run(() => store.TryStart(job.Id, 2, 200, 100))));
        var started = attempts.OfType<DownloadJob>().ToArray();
        Assert.Equal(2, started.Length);
        var pending = jobs.First(job => started.All(active => active.Id != job.Id));
        store.Update(started[0].Id, job => job with { State = DownloadJobState.Failed, ReservedBytes = 0, StoredBytes = 60 });
        Assert.Null(store.TryStart(pending.Id, 2, 200, 100));
        store.Update(started[1].Id, job => job with { State = DownloadJobState.Cancelled, ReservedBytes = 0 });
        Assert.NotNull(store.TryStart(pending.Id, 2, 200, 100));
        Assert.Equal(160, store.Snapshot().Sum(job => job.State == DownloadJobState.Running ? job.ReservedBytes : job.StoredBytes));
    }
    [Fact]
    public void UserQuotaCountsPausedPartialsAndStoppingReservations()
    {
        var store = new DownloadQueueStore(_directory);
        var owner = Guid.NewGuid();
        var paused = store.Add(Job() with { UserId = owner, State = DownloadJobState.Paused, StoredBytes = 30 }, 20);
        var running = store.Add(Job() with { UserId = owner, ItemKey = "movie:running" }, 20);
        var queued = store.Add(Job() with { UserId = owner, ItemKey = "movie:queued" }, 20);
        Assert.NotNull(store.TryStart(running.Id, 4, 1000, 100, 150));
        store.Update(running.Id, job => job with { State = DownloadJobState.Paused });
        Assert.Null(store.TryStart(queued.Id, 4, 1000, 100, 150));
        var stranger = store.Add(Job(), 20);
        Assert.NotNull(store.TryStart(stranger.Id, 4, 1000, 100, 150));
        store.Update(running.Id, job => job with { ReservedBytes = 0, StoredBytes = 20 });
        Assert.NotNull(store.TryStart(queued.Id, 4, 1000, 100, 150));
        var recovered = new DownloadQueueStore(_directory);
        recovered.Recover();
        Assert.Equal(DownloadJobState.Paused, recovered.Find(paused.Id, owner)!.State);
        Assert.Equal(DownloadJobState.Paused, recovered.Find(running.Id, owner)!.State);
        Assert.Equal(30, recovered.Find(paused.Id, owner)!.StoredBytes);
    }


    [Fact]
    public void FailedCommitDoesNotPublishAJobOrLoseItsPredecessor()
    {
        var store = new DownloadQueueStore(_directory);
        var first = store.Add(Job(), 20);
        var path = Path.Combine(_directory, "download-queue.json");
        var saved = File.ReadAllText(path);
        File.Delete(path);
        Directory.CreateDirectory(path);
        var failure = Record.Exception(() => store.Add(Job(), 20));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(first.Id, Assert.Single(store.Snapshot()).Id);
        Directory.Delete(path);
        File.WriteAllText(path, saved);
        Assert.Equal(first.Id, Assert.Single(new DownloadQueueStore(_directory).Snapshot()).Id);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Version\":1}")]
    [InlineData("{\"Version\":2,\"StorageKey\":\"bad\",\"Jobs\":[]}")]
    public void CorruptLedgerNeverBootstrapsReplacementOwnership(string content)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "download-queue.json");
        File.WriteAllText(path, content);
        var error = Record.Exception(() => new DownloadQueueStore(_directory));
        Assert.True(error is JsonException or InvalidDataException);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void UnownedDirectoryAndSymlinkAreNeverDeletedOrReadAsAnArtifact()
    {
        var store = new DownloadQueueStore(_directory);
        var owned = store.Add(Job(), 20);
        var ownedDirectory = store.Files.Prepare(owned);
        var other = store.Add(Job(), 20);
        var unowned = Path.Combine(_directory, "downloads", other.Id.ToString("N"));
        Directory.CreateDirectory(unowned);
        File.WriteAllText(Path.Combine(unowned, "keep.txt"), "unrelated");
        Assert.Throws<InvalidDataException>(() => store.Files.Delete(other));
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(unowned, "keep.txt")));

        var target = Path.Combine(_directory, "private.bin");
        File.WriteAllText(target, "private");
        File.CreateSymbolicLink(Path.Combine(ownedDirectory, "video.mp4"), target);
        Assert.Throws<InvalidDataException>(() => store.Files.Open(owned with { FileName = "video.mp4" }));
        Assert.Throws<InvalidDataException>(() => store.Files.Delete(owned));
        Assert.Equal("private", File.ReadAllText(target));
        File.Delete(Path.Combine(ownedDirectory, "video.mp4"));
        store.Files.Delete(owned);
        Assert.False(Directory.Exists(ownedDirectory));
        Assert.True(Directory.Exists(unowned));
    }

    [Fact]
    public void CompletionKeepsResumeStateUntilTheCompletedArtifactIsDurablyCommitted()
    {
        var store = new DownloadQueueStore(_directory);
        var job = store.Add(Job(), 20);
        var directory = store.Files.Prepare(job);
        File.WriteAllText(Path.Combine(directory, "resume.json"), "validated resume state");
        File.WriteAllBytes(Path.Combine(directory, "video.mp4"), [1, 2, 3, 4]);
        var running = store.TryStart(job.Id, 1, 1024, 1024)!;
        var stored = store.Files.Finish(running, new("video.mp4", "video/mp4", 4));
        var reopened = new DownloadQueueStore(_directory);
        reopened.Recover();
        Assert.Equal(DownloadJobState.Queued, reopened.Find(job.Id, job.UserId)!.State);
        Assert.Equal("validated resume state", File.ReadAllText(Path.Combine(directory, "resume.json")));
        var completed = reopened.Update(job.Id, current => current with
        {
            State = DownloadJobState.Completed,
            StoredBytes = stored,
            TotalBytes = 4,
            FileName = "video.mp4",
            ContentType = "video/mp4"
        })!;
        reopened.Files.RetainCompleted(completed);
        Assert.False(File.Exists(Path.Combine(directory, "resume.json")));
        using var file = new DownloadQueueStore(_directory).Files.Open(completed);
        Assert.Equal(4, file.Length);
    }

    internal static DownloadJob Job() => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        ItemId = Guid.NewGuid(),
        MediaSourceId = Guid.NewGuid(),
        ItemKey = "movie:fixture",
        SourceId = new string('A', 64),
        ProfileKey = new string('B', 64),
        TransportKey = new string('C', 64),
        StorageKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
        Name = "Fixture",
        CreatedUtc = DateTimeOffset.UtcNow,
        UpdatedUtc = DateTimeOffset.UtcNow
    };

    internal static string NewDirectory()
    {
        // System temp aliases (notably /var on macOS) are canonicalized by the fixture, not adopted by production storage.
        var temporary = Path.GetFullPath(Path.GetTempPath());
        var current = Path.GetPathRoot(temporary)!;
        foreach (var component in temporary[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            var directory = new DirectoryInfo(current);
            if (directory.LinkTarget is not null) current = directory.ResolveLinkTarget(returnFinalTarget: true)!.FullName;
        }
        return Path.Combine(current, "siphon-queue-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
