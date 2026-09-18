using System.Net;
using System.Net.Http.Headers;
using Jellyfin.Plugin.Siphon.Downloads;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Playback;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Downloads;

public sealed class DownloadTransferTests
{
    [Fact]
    public async Task RestartAppendsOnlyTheSameStronglyValidatedRepresentation()
    {
        using var directory = new TransferDirectory();
        var bytes = Enumerable.Range(0, 2048).Select(index => (byte)(index % 251)).ToArray();
        var calls = 0;
        var http = new TransferHttp((_, headers, _) =>
        {
            if (++calls == 1) return Response(new InterruptedStream(bytes, 512), bytes.Length, "\"version-one\"");
            Assert.Equal("bytes=512-", headers!["Range"]);
            Assert.Equal("\"version-one\"", headers["If-Range"]);
            var response = Response(new MemoryStream(bytes[512..]), bytes.Length - 512, "\"version-one\"", HttpStatusCode.PartialContent);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(512, bytes.Length - 1, bytes.Length);
            return response;
        });
        var service = new DownloadTransferService(http, null!, null!);
        await Assert.ThrowsAsync<IOException>(() => service.TransferAsync(Source(), directory.Path, 16384, new DownloadTransferOptions(), _ => Task.CompletedTask, default));
        var result = await service.TransferAsync(Source(), directory.Path, 16384, new DownloadTransferOptions(), _ => Task.CompletedTask, default);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(System.IO.Path.Combine(directory.Path, result.FileName)));
        Assert.Equal(bytes.Length, result.Bytes);
    }

    [Theory]
    [InlineData("\"changed\"", 2048)]
    [InlineData("\"version-one\"", 4096)]
    public async Task ChangedValidatorOrLengthRestartsInsteadOfSplicing(string returnedTag, int returnedTotal)
    {
        using var directory = new TransferDirectory();
        var oldBytes = Enumerable.Repeat((byte)'a', 2048).ToArray();
        var newBytes = Enumerable.Repeat((byte)'b', 2048).ToArray();
        var calls = 0;
        var http = new TransferHttp((_, headers, _) =>
        {
            if (++calls == 1) return Response(new InterruptedStream(oldBytes, 512), oldBytes.Length, "\"version-one\"");
            if (calls == 2)
            {
                var response = Response(new MemoryStream(newBytes[512..]), returnedTotal - 512, returnedTag, HttpStatusCode.PartialContent);
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(512, returnedTotal - 1, returnedTotal);
                return response;
            }
            Assert.False(headers!.ContainsKey("Range"));
            return Response(new MemoryStream(newBytes), newBytes.Length, returnedTag);
        });
        var service = new DownloadTransferService(http, null!, null!);
        await Assert.ThrowsAsync<IOException>(() => service.TransferAsync(Source(), directory.Path, 16384, new DownloadTransferOptions(), _ => Task.CompletedTask, default));
        var result = await service.TransferAsync(Source(), directory.Path, 16384, new DownloadTransferOptions(), _ => Task.CompletedTask, default);
        Assert.Equal(newBytes, await File.ReadAllBytesAsync(System.IO.Path.Combine(directory.Path, result.FileName)));
    }

    [Fact]
    public async Task WeakValidatorNeverReusesTheInterruptedPrefix()
    {
        using var directory = new TransferDirectory();
        var oldBytes = Enumerable.Repeat((byte)'a', 2048).ToArray();
        var newBytes = Enumerable.Repeat((byte)'b', 2048).ToArray();
        var calls = 0;
        var http = new TransferHttp((_, headers, _) =>
        {
            if (++calls == 1) return Response(new InterruptedStream(oldBytes, 512), oldBytes.Length, "W/\"version\"");
            Assert.False(headers!.ContainsKey("Range"));
            return Response(new MemoryStream(newBytes), newBytes.Length, "W/\"version\"");
        });
        var service = new DownloadTransferService(http, null!, null!);
        await Assert.ThrowsAsync<IOException>(() => service.TransferAsync(Source(), directory.Path, 16384, new DownloadTransferOptions(), _ => Task.CompletedTask, default));
        var result = await service.TransferAsync(Source(), directory.Path, 16384, new DownloadTransferOptions(), _ => Task.CompletedTask, default);
        Assert.Equal(newBytes, await File.ReadAllBytesAsync(System.IO.Path.Combine(directory.Path, result.FileName)));
    }

    [Fact]
    public async Task UnboundedBodyCannotExceedTheOwnedDirectoryBudget()
    {
        using var directory = new TransferDirectory();
        var http = new TransferHttp((_, _, _) => Response(new NonSeekableStream(new byte[4096]), null, "\"version\""));
        var service = new DownloadTransferService(http, null!, null!);
        await Assert.ThrowsAsync<IOException>(() => service.TransferAsync(Source(), directory.Path, 1024, new DownloadTransferOptions(), _ => Task.CompletedTask, default));
        Assert.True(Directory.EnumerateFiles(directory.Path).Sum(path => new FileInfo(path).Length) <= 1024);
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "media.mp4")));
    }

    [Fact]
    public async Task CancelledReadDoesNotLeaveACompletedArtifact()
    {
        using var directory = new TransferDirectory();
        using var cancelled = new CancellationTokenSource();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new TransferHttp((_, _, _) => Response(new WaitingStream(reading), null, null));
        var service = new DownloadTransferService(http, null!, null!);
        var transfer = service.TransferAsync(Source(), directory.Path, 16384, new DownloadTransferOptions(), _ => Task.CompletedTask, cancelled.Token);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "media.mp4")));
    }

    [Fact]
    public async Task RestartAfterInterruptedAacPreparationReplacesOnlyOwnedPartialArtifacts()
    {
        using var directory = new TransferDirectory();
        var interrupted = new DownloadTransferWorkspace(directory.Path, 8192, _ => Task.CompletedTask);
        await interrupted.WriteAsync("audio-probe.json", "{\"streams\":["u8.ToArray(), default);
        await interrupted.WriteAsync("audio-config.m4a", new byte[2048], default);
        await interrupted.WriteAsync("h00001.ts", new byte[512], default);
        await interrupted.WriteAsync("remux.part", new byte[256], default);
        var bytes = Enumerable.Repeat((byte)'v', 4096).ToArray();
        var http = new TransferHttp((_, _, _) => Response(new MemoryStream(bytes), bytes.Length, "\"replacement\""));
        var service = new DownloadTransferService(http, null!, null!);

        var result = await service.TransferAsync(Source(), directory.Path, 8192, new DownloadTransferOptions(), _ => Task.CompletedTask, default);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(System.IO.Path.Combine(directory.Path, result.FileName)));
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "remux.part")));
        Assert.True(Directory.EnumerateFiles(directory.Path).Sum(path => new FileInfo(path).Length) <= 8192);
    }

    [Fact]
    public void WorkspaceRefusesSymlinksInsteadOfOverwritingTheirTargets()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = new TransferDirectory();
        Directory.CreateDirectory(directory.Path);
        var external = directory.Path + ".external";
        File.WriteAllText(external, "keep");
        try
        {
            File.CreateSymbolicLink(System.IO.Path.Combine(directory.Path, "transfer.part"), external);
            Assert.Throws<IOException>(() => new DownloadTransferWorkspace(directory.Path, 16384, _ => Task.CompletedTask));
            Assert.Equal("keep", File.ReadAllText(external));
        }
        finally { File.Delete(external); }
    }

    internal static ResolvedStream Source(string url = "https://source.example/video.mp4")
        => new("exact-source", "Private video", new Uri(url), new Dictionary<string, string> { ["Authorization"] = "Bearer private", ["X-Addon-Token"] = "secret" }, "video.mp4", null);

    internal static HttpResponseMessage Response(Stream stream, long? length, string? etag, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentLength = length;
        if (etag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return response;
    }

    private sealed class InterruptedStream(byte[] bytes, int after) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= after) throw new IOException("Interrupted fixture.");
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, after - (int)Position)], cancellationToken);
        }
    }

    private class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class WaitingStream(TaskCompletionSource reading) : NonSeekableStream([])
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            reading.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}

internal sealed class TransferHttp(Func<Uri, IReadOnlyDictionary<string, string>?, CancellationToken, HttpResponseMessage> send) : ISafeHttpClient
{
    public Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        => Task.FromResult(send(uri, headers, cancellationToken));
}

internal sealed class TransferDirectory : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(Directory.GetCurrentDirectory(), ".transfer-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
