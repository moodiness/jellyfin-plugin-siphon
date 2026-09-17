using System.Net;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Playback;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Playback;

public sealed class DownloadRangeTests
{
    [Theory]
    [InlineData("bytes=-4", 10, 6, 9)]
    [InlineData("bytes=7-99", 10, 7, 9)]
    [InlineData("bytes=4-", 10, 4, 9)]
    [InlineData("bytes=-99", 10, 0, 9)]
    public void SuffixAndOpenRangesStayInsideChosenFile(string range, long length, long expectedStart, long expectedEnd)
    {
        Assert.True(PlaybackDownloadService.TryBounds(range, length, out var start, out var end));
        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedEnd, end);
    }

    [Theory]
    [InlineData("bytes=10-", 10)]
    [InlineData("bytes=-0", 10)]
    [InlineData("bytes=0-1,4-5", 10)]
    [InlineData("bytes=0-", 0)]
    public void UnsatisfiableOrMultipartRangesNeverReadAnotherRepresentation(string range, long length)
        => Assert.False(PlaybackDownloadService.TryBounds(range, length, out _, out _));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StalledClassificationAndDownloadBodiesDoNotRetainRequestsIndefinitely(bool classificationStalls)
    {
        var configuration = new ConfigurationAccessor(() => new PluginConfiguration { AddonTimeoutSeconds = 1 });
        var service = new PlaybackDownloadService(new StalledOrigin(classificationStalls), null!, configuration);
        var context = new DefaultHttpContext();
        context.Request.Method = classificationStalls ? "HEAD" : "GET";
        context.Response.Body = new MemoryStream();
        var source = new ResolvedStream("source", "Video", new Uri("https://origin.example/movie.mp4"), new Dictionary<string, string>(), null, null);
        await service.RelayAsync(context, source, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(502, context.Response.StatusCode);
        Assert.Empty(((MemoryStream)context.Response.Body).ToArray());
    }

    private sealed class StalledOrigin(bool classificationStalls) : ISafeHttpClient
    {
        public Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        {
            var classify = headers?.GetValueOrDefault("Range") == "bytes=0-511";
            Stream stream = classify == classificationStalls ? new StalledBody() : new MemoryStream(new byte[512]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        }
    }

    private sealed class StalledBody : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }
    }
}
