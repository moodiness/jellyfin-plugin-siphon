using Jellyfin.Plugin.Siphon.Downloads;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Downloads;

public sealed class DownloadControlsTests
{
    [Fact]
    public void PriorityOrdersOnlyWithinEachUsersRoundRobinTurn()
    {
        var firstUser = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var nextUser = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var lower = DownloadQueueStoreTests.Job() with { UserId = firstUser, Priority = -10 };
        var higher = DownloadQueueStoreTests.Job() with { UserId = firstUser, Priority = 10 };
        var other = DownloadQueueStoreTests.Job() with { UserId = nextUser, Priority = -10 };
        var paused = DownloadQueueStoreTests.Job() with { UserId = nextUser, Priority = 10, State = DownloadJobState.Paused };
        var scheduled = DownloadQueueService.ScheduleCandidates([lower, higher, other, paused], firstUser);
        Assert.Equal(new[] { other.Id, higher.Id }, scheduled.Select(job => job.Id));
        Assert.Equal(new[] { higher.Id, other.Id }, DownloadQueueService.ScheduleCandidates([lower, higher, other], nextUser).Select(job => job.Id));
    }

    [Fact]
    public async Task CancelledResumeReplacementPreservesThePreviouslyCommittedValidator()
    {
        using var directory = new TransferDirectory();
        var workspace = new DownloadTransferWorkspace(directory.Path, 4096, _ => Task.CompletedTask);
        var previous = new DownloadResumeState("http", "original", "\"stable\"", 1000, ".mp4");
        await workspace.SaveResumeAsync(previous, default);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.SaveResumeAsync(previous with { Fingerprint = "replacement" }, cancelled.Token));
        Assert.Equal(previous, new DownloadTransferWorkspace(directory.Path, 4096, _ => Task.CompletedTask).ReadResume());
        var replacement = previous with { Fingerprint = "next", ETag = "\"next\"" };
        await workspace.SaveResumeAsync(replacement, default);
        Assert.Equal(replacement, workspace.ReadResume());
        Assert.Equal(Directory.EnumerateFiles(directory.Path).Sum(path => new FileInfo(path).Length), workspace.StoredBytes);
    }

    [Fact]
    public void RatesRequireReceivedDeltasAndNeverCarryIntoAssemblyOrResume()
    {
        var rate = new DownloadRateEstimator();
        Assert.Null(rate.Observe(new(1000, 2000, 1000), 1000).Rate);
        var receiving = rate.Observe(new(1500, 2000, 1500), 2000);
        Assert.Equal(500d, receiving.Rate);
        Assert.Equal(1d, receiving.Remaining);
        Assert.Null(rate.Observe(new(1600, null, 1600), 3000).Remaining);
        var preparing = rate.Observe(new(1600, 1600, 3200, "PreparingAudio"), 4000);
        Assert.Null(preparing.Rate);
        Assert.Null(preparing.Remaining);
        Assert.Null(rate.Observe(new(1600, 2000, 1600), 5000).Rate);
        Assert.Null(rate.Observe(new(0, 2000, 0), 6000).Rate);
    }

    [Theory]
    [InlineData(23, 22, 6, true)]
    [InlineData(0, 22, 6, true)]
    [InlineData(6, 22, 6, false)]
    [InlineData(5, 0, 6, true)]
    [InlineData(6, 0, 6, false)]
    [InlineData(12, 12, 12, false)]
    public void UtcWindowsUseHalfOpenBoundariesIncludingOvernight(int hour, int start, int end, bool expected)
        => Assert.Equal(expected, DownloadQueueService.IsWindowOpen(new DateTimeOffset(2026, 1, 1, hour, 0, 0, TimeSpan.Zero), true, start, end));

    [Fact]
    public void HlsLanguageSelectionKeepsOnlyTheRequestedAdvertisedRenditions()
    {
        var manifest = Master();
        var selected = DownloadPreparationService.SelectVariant(manifest);
        var filtered = DownloadPreparationService.SelectRenditions(manifest, selected, new(["fr"], []));
        Assert.Equal("fr", Assert.Single(filtered.Audio).Language);
        Assert.Empty(filtered.Subtitles);
        var all = DownloadPreparationService.SelectRenditions(manifest, selected, new());
        Assert.Equal(new[] { "en", "fr" }, all.Audio.Select(value => value.Language));
        Assert.Single(all.Subtitles);
        Assert.Throws<DownloadSelectionException>(() => DownloadPreparationService.SelectRenditions(manifest, selected, new(["de"], null)));
    }

    [Fact]
    public async Task PreparationFetchesOnlyBoundedPlaylistMetadataAndAdvertisesSelections()
    {
        var fetched = new List<string>();
        var http = new TransferHttp((url, _, _) =>
        {
            fetched.Add(url.AbsolutePath);
            var text = url.AbsolutePath == "/master.m3u8" ? MasterText : "#EXTM3U\n#EXTINF:2,\nmedia.ts\n#EXT-X-ENDLIST\n";
            Assert.EndsWith(".m3u8", url.AbsolutePath);
            return DownloadTransferTests.Response(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)), null, null);
        });
        var result = await new DownloadPreparationService(http).PrepareAsync(DownloadTransferTests.Source("https://source.example/master.m3u8"), default);
        Assert.Equal("Hls", result.Kind);
        Assert.Equal(new[] { "en", "fr" }, result.AudioLanguages);
        Assert.Equal(new[] { "fr" }, result.SubtitleLanguages);
        Assert.Equal(5, fetched.Count);
        Assert.NotNull(result.EstimatedSourceBytes);
        Assert.True(result.EstimatedTemporaryBytes > result.EstimatedSourceBytes);
    }

    [Fact]
    public async Task ProgressiveSelectionCannotSilentlyKeepUnrequestedTracks()
    {
        using var directory = new TransferDirectory();
        var service = new DownloadTransferService(new TransferHttp((_, _, _) =>
            DownloadTransferTests.Response(new MemoryStream(new byte[2048]), 2048, "\"file\"")), null!, null!);
        await Assert.ThrowsAsync<DownloadSelectionException>(() => service.TransferAsync(DownloadTransferTests.Source(), directory.Path,
            4096, new([], null), _ => Task.CompletedTask, default));
        Assert.False(File.Exists(Path.Combine(directory.Path, "media.mp4")));
    }

    private static HlsOfflineManifest Master() => HlsOfflineManifest.Parse(MasterText, new Uri("https://source.example/master.m3u8"));
    private const string MasterText = "#EXTM3U\n"
        + "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",NAME=\"English\",LANGUAGE=\"en\",URI=\"en.m3u8\"\n"
        + "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",NAME=\"French\",LANGUAGE=\"fr\",URI=\"fr.m3u8\"\n"
        + "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"s\",NAME=\"French\",LANGUAGE=\"fr\",URI=\"sub.m3u8\"\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=640x360,AUDIO=\"a\",SUBTITLES=\"s\"\nvideo.m3u8\n";
}
