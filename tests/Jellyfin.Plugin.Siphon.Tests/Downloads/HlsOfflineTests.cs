using System.Diagnostics;
using System.Reflection;
using System.Text;
using Jellyfin.Plugin.Siphon.Downloads;
using MediaBrowser.Controller.MediaEncoding;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Downloads;

public sealed class HlsOfflineTests
{
    [Theory]
    [InlineData("#EXTM3U\n#EXTINF:5,\nsegment.ts\n")]
    [InlineData("#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"key\"\n#EXTINF:5,\nsegment.ts\n#EXT-X-ENDLIST\n")]
    [InlineData("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,KEYFORMAT=\"com.apple.streamingkeydelivery\",URI=\"key\"\n#EXTINF:5,\nsegment.ts\n#EXT-X-ENDLIST\n")]
    [InlineData("#EXTM3U\n#EXTINF:5,\nfile:///etc/passwd\n#EXT-X-ENDLIST\n")]
    [InlineData("#EXTM3U\n#EXT-X-MAP:URI=\"data:text/plain,unsafe\"\n#EXTINF:5,\nsegment.ts\n#EXT-X-ENDLIST\n")]
    [InlineData("#EXTM3U\n#EXT-X-PRELOAD-HINT:TYPE=PART,URI=\"next.ts\"\n#EXTINF:5,\nsegment.ts\n#EXT-X-ENDLIST\n")]
    [InlineData("#EXTM3U\n#EXTINF:NaN,\nsegment.ts\n#EXT-X-ENDLIST\n")]
    [InlineData("#EXTM3U\n#EXTINF:5,\n#EXT-X-BYTERANGE:100\nsegment.ts\n#EXT-X-ENDLIST\n")]
    public void RejectsUnsafeUnboundedOrUnsupportedMediaBeforeStaging(string playlist)
    {
        Assert.Throws<InvalidDataException>(() => HlsOfflineManifest.Parse(playlist, new Uri("https://origin.example/video.m3u8")));
    }

    [Fact]
    public void RejectsExcessiveResourcesBeforeAnyNetworkWork()
    {
        var manifest = "#EXTM3U\n" + string.Concat(Enumerable.Repeat("#EXTINF:1,\nsegment.ts\n", HlsOfflineManifest.MaximumResources + 1)) + "#EXT-X-ENDLIST\n";
        Assert.Throws<InvalidDataException>(() => HlsOfflineManifest.Parse(manifest, new Uri("https://origin.example/video.m3u8")));
    }

    [Fact]
    public async Task RedirectedPlaylistResourcesNeverReceiveOriginalOriginCredentials()
    {
        using var directory = new TransferDirectory();
        var source = DownloadTransferTests.Source("https://source.example/video.m3u8");
        const string playlist = "#EXTM3U\n#EXT-X-TARGETDURATION:5\n#EXTINF:5,\n../segment.ts\n#EXT-X-ENDLIST\n";
        var calls = 0;
        var http = new TransferHttp((uri, headers, _) =>
        {
            calls++;
            if (uri == source.Url)
            {
                var response = DownloadTransferTests.Response(new MemoryStream(Encoding.UTF8.GetBytes(playlist)), Encoding.UTF8.GetByteCount(playlist), null);
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://cdn.example/redirect/video.m3u8");
                return response;
            }
            Assert.Equal("https://cdn.example/segment.ts", uri.AbsoluteUri);
            Assert.False(headers!.ContainsKey("Authorization"));
            Assert.False(headers.ContainsKey("X-Addon-Token"));
            throw new IOException("Stop before remuxing.");
        });
        var service = new DownloadTransferService(http, null!, Encoder());
        await Assert.ThrowsAsync<IOException>(() => service.TransferAsync(source, directory.Path, 16384, new DownloadTransferOptions(), _ => Task.CompletedTask, default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task StagingRejectsNestedMediaResourcesRatherThanPassingThemToFfmpeg()
    {
        using var directory = new TransferDirectory();
        const string playlist = "#EXTM3U\n#EXTINF:5,\nsegment.ts\n#EXT-X-ENDLIST\n";
        var http = new TransferHttp((uri, _, _) =>
        {
            var text = uri.AbsolutePath.EndsWith(".m3u8", StringComparison.Ordinal) ? playlist : "#EXTM3U\nfile:///etc/passwd\n";
            var bytes = Encoding.UTF8.GetBytes(text);
            return DownloadTransferTests.Response(new MemoryStream(bytes), bytes.Length, null);
        });
        var service = new DownloadTransferService(http, null!, Encoder());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.TransferAsync(DownloadTransferTests.Source("https://source.example/video.m3u8"), directory.Path, 16384, new DownloadTransferOptions(), _ => Task.CompletedTask, default));
        Assert.False(File.Exists(Path.Combine(directory.Path, "media.mkv")));
    }

    [Fact]
    public async Task StagedResourcesAndPlaylistsShareOneStrictDiskBudget()
    {
        using var directory = new TransferDirectory();
        const string playlist = "#EXTM3U\n#EXTINF:5,\nfirst.ts\n#EXTINF:5,\nsecond.ts\n#EXT-X-ENDLIST\n";
        var http = new TransferHttp((uri, _, _) =>
        {
            var bytes = uri.AbsolutePath.EndsWith(".m3u8", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(playlist) : new byte[600];
            return DownloadTransferTests.Response(new MemoryStream(bytes), bytes.Length, null);
        });
        var service = new DownloadTransferService(http, null!, Encoder());
        await Assert.ThrowsAsync<IOException>(() => service.TransferAsync(DownloadTransferTests.Source("https://source.example/video.m3u8"), directory.Path, 1024, new DownloadTransferOptions(), _ => Task.CompletedTask, default));
        Assert.True(Directory.EnumerateFiles(directory.Path).Sum(path => new FileInfo(path).Length) <= 1024);
    }

    [Fact]
    public void SubtitleAssemblyPreservesTransportClockAndDeduplicatesBoundaryCues()
    {
        var assembler = new HlsSubtitleAssembler();
        var first = Encoding.UTF8.GetBytes("WEBVTT\nX-TIMESTAMP-MAP=LOCAL:00:00:00.000,MPEGTS:900000\n\ncue\n00:00:01.000 --> 00:00:06.000 align:start\nAcross the segment boundary\n\n");
        assembler.Add(first);
        assembler.Add(first);
        assembler.Add(Encoding.UTF8.GetBytes("WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:1350000,LOCAL:00:00:00.000\n\n00:00:02.000 --> 00:00:03.000\nNext cue\n\n"));
        Assert.Equal("WEBVTT\n\ncue\n00:00:11.000 --> 00:00:16.000 align:start\nAcross the segment boundary\n\n00:00:17.000 --> 00:00:18.000\nNext cue\n\n",
            Encoding.UTF8.GetString(assembler.Finish()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemuxCancellationAndBudgetFailureKillAndReapTheChild(bool exhaustBudget)
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = new TransferDirectory();
        Directory.CreateDirectory(directory.Path);
        var encoder = Path.Combine(directory.Path, "encoder");
        var pidFile = Path.Combine(directory.Path, "pid");
        var quotedPid = "'" + pidFile.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        var activity = exhaustBudget ? "while :; do printf '0123456789012345678901234567890123456789012345678901234567890123456789'; done" : "while :; do sleep 1; done";
        await File.WriteAllTextAsync(encoder, "#!/bin/sh\nprintf '%s' \"$$\" > " + quotedPid + "\n" + activity + "\n");
        File.SetUnixFileMode(encoder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var workspace = new DownloadTransferWorkspace(Path.Combine(directory.Path, "job"), 1024, _ => Task.CompletedTask);
        await workspace.WriteAsync("h00001.m3u8", "#EXTM3U\n"u8.ToArray(), default);
        using var cancellation = new CancellationTokenSource();
        var task = new HlsRemuxer(encoder).RemuxAsync(workspace, "h00001.m3u8", [], [], 0, true, cancellation.Token);
        for (var attempt = 0; attempt < 100 && !File.Exists(pidFile); attempt++) await Task.Delay(25);
        Assert.True(File.Exists(pidFile));
        if (exhaustBudget) await Assert.ThrowsAsync<IOException>(() => task);
        else
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
        var pid = int.Parse(await File.ReadAllTextAsync(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
        Assert.True(Directory.EnumerateFiles(workspace.DirectoryPath).Sum(path => new FileInfo(path).Length) <= 1024);
    }

    [Fact]
    public async Task AudioOnlyHlsIsRefusedRatherThanProducingAnIncompleteVideoExport()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = new TransferDirectory();
        Directory.CreateDirectory(directory.Path);
        var probe = Path.Combine(directory.Path, "probe");
        await File.WriteAllTextAsync(probe, "#!/bin/sh\nprintf '%s' '{\"streams\":[{\"index\":0,\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"extradata_size\":2}]}'\n");
        File.SetUnixFileMode(probe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var workspace = new DownloadTransferWorkspace(Path.Combine(directory.Path, "job"), 4096, _ => Task.CompletedTask);
        await workspace.WriteAsync("h00001.m3u8", "#EXTM3U\n"u8.ToArray(), default);
        await Assert.ThrowsAsync<DownloadSelectionException>(() => new HlsRemuxer(probe)
            .PrepareAudioAsync(workspace, "h00001.m3u8", probe, 0, null, default));
        Assert.False(File.Exists(workspace.PathFor("remux.part")));
    }

    [Fact]
    public async Task StagedAudioMustIdentifyEveryRequestedLanguageOrFailExplicitly()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = new TransferDirectory();
        Directory.CreateDirectory(directory.Path);
        var probe = Path.Combine(directory.Path, "probe");
        await File.WriteAllTextAsync(probe, "#!/bin/sh\nprintf '%s' '{\"streams\":[{\"index\":0,\"codec_type\":\"video\"},{\"index\":1,\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"extradata_size\":2,\"tags\":{\"language\":\"fr\"}}]}'\n");
        File.SetUnixFileMode(probe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var workspace = new DownloadTransferWorkspace(Path.Combine(directory.Path, "job"), 4096, _ => Task.CompletedTask);
        await workspace.WriteAsync("h00001.m3u8", "#EXTM3U\n"u8.ToArray(), default);
        var remuxer = new HlsRemuxer(probe);
        await Assert.ThrowsAsync<DownloadSelectionException>(() => remuxer.PrepareAudioAsync(workspace, "h00001.m3u8", probe, 0, ["en"], default));
        Assert.Empty(await remuxer.PrepareAudioAsync(workspace, "h00001.m3u8", probe, 0, [], default));
        Assert.Equal(1, Assert.Single(await remuxer.PrepareAudioAsync(workspace, "h00001.m3u8", probe, 0, ["fr"], default)).SourceIndex);
    }

    private static IMediaEncoder Encoder() => DispatchProxy.Create<IMediaEncoder, EncoderPathProxy>();
    public class EncoderPathProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == "get_EncoderPath" ? Environment.ProcessPath! : throw new NotSupportedException();
    }
}
