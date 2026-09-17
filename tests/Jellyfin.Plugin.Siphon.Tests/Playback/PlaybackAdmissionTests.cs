using System.Net;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.P2p;
using Jellyfin.Plugin.Siphon.Playback;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Playback;

public sealed class PlaybackAdmissionTests
{
    [Fact]
    public void ClientBudgetBoundsBothRequestsAndClientChurnWithoutDebitingAnotherBudget()
    {
        long clock = 1000;
        var invalid = new PlaybackRequestBudget(2, 2, () => clock);
        var valid = new PlaybackRequestBudget(2, 2, () => clock);
        Assert.True(invalid.Allow("first"));
        Assert.True(invalid.Allow("first"));
        Assert.False(invalid.Allow("first"));
        Assert.True(invalid.Allow("second"));
        Assert.False(invalid.Allow("third"));
        Assert.True(valid.Allow("first"));
        Assert.True(valid.Allow("third"));
        clock += 1000;
        Assert.True(invalid.Allow("third"));
        Assert.True(invalid.Allow("first"));
    }

    [Fact]
    public void GlobalAdmissionAndUserAdmissionAreReleasedExactlyOnce()
    {
        var admission = new DirectTransferAdmission(3, 2);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        using var a = admission.TryAcquire(first, CancellationToken.None)!;
        using var b = admission.TryAcquire(first, CancellationToken.None)!;
        Assert.Null(admission.TryAcquire(first, CancellationToken.None));
        using var c = admission.TryAcquire(second, CancellationToken.None)!;
        Assert.Null(admission.TryAcquire(Guid.NewGuid(), CancellationToken.None));
        a.Dispose();
        a.Dispose();
        using var replacement = admission.TryAcquire(first, CancellationToken.None);
        Assert.NotNull(replacement);
        Assert.Null(admission.TryAcquire(Guid.NewGuid(), CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => admission.TryAcquire(second, cancelled.Token));
    }

    [Fact]
    public async Task HttpAndP2pDownloadsShareAdmissionAndCancellationAndErrorsReleaseIt()
    {
        var origin = new ControlledOrigin();
        var service = new PlaybackDownloadService(origin, null!, new ConfigurationAccessor(() => new PluginConfiguration()));
        var user = Guid.NewGuid();
        var source = new ResolvedStream("fixture", "Fixture", new Uri("https://origin.example/hold"), new Dictionary<string, string>(), "fixture.mp4", null) { UserId = user };
        using var cancellation = new CancellationTokenSource();
        var active = Enumerable.Range(0, 4).Select(_ => service.RelayAsync(Context(), source, cancellation.Token)).ToArray();
        try
        {
            await origin.FourOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var refusedHttp = Context();
            await service.RelayAsync(refusedHttp, source, CancellationToken.None);
            Assert.Equal(429, refusedHttp.Response.StatusCode);
            var refusedP2p = Context();
            await service.RelayAsync(refusedP2p, source with { P2p = new P2pSource("0123456789abcdef0123456789abcdef01234567", null, null, []) }, CancellationToken.None);
            Assert.Equal(429, refusedP2p.Response.StatusCode);
            Assert.Equal("2", refusedP2p.Response.Headers.RetryAfter.ToString());
            var otherUser = Context();
            await service.RelayAsync(otherUser, source with { UserId = Guid.NewGuid(), Url = new Uri("https://origin.example/ready") }, CancellationToken.None);
            Assert.Equal(200, otherUser.Response.StatusCode);
        }
        finally
        {
            cancellation.Cancel();
            await Task.WhenAll(active).WaitAsync(TimeSpan.FromSeconds(5));
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var failed = Context();
            await service.RelayAsync(failed, source with { Url = new Uri("https://origin.example/fail") }, CancellationToken.None);
            Assert.Equal(502, failed.Response.StatusCode);
        }
        var recovered = Context();
        await service.RelayAsync(recovered, source with { Url = new Uri("https://origin.example/ready") }, CancellationToken.None);
        Assert.Equal(200, recovered.Response.StatusCode);
        Assert.Equal(new byte[512], ((MemoryStream)recovered.Response.Body).ToArray());
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class ControlledOrigin : ISafeHttpClient
    {
        private int _opened;
        internal TaskCompletionSource FourOpened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            if (uri.AbsolutePath == "/hold")
            {
                if (Interlocked.Increment(ref _opened) == 4) FourOpened.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (uri.AbsolutePath == "/fail") throw new IOException("Owned fixture failure.");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[512]) };
        }
    }
}
