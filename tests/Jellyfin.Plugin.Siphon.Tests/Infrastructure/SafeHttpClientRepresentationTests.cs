using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Infrastructure;

public sealed class SafeHttpClientRepresentationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityRequestsKeepUnexpectedCompressionVisibleAcrossRedirects(bool preserveBytes)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        using var target = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        target.Start();
        var originEndpoint = (IPEndPoint)origin.LocalEndpoint;
        var targetEndpoint = (IPEndPoint)target.LocalEndpoint;
        var configuration = new ConfigurationAccessor(() => new PluginConfiguration
        {
            AllowedPrivateHosts = [originEndpoint.Address.ToString()]
        });
        using var client = new SafeHttpClient(configuration, new SsrfPolicy(configuration));
        var plain = Encoding.UTF8.GetBytes("An upstream representation");
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true)) gzip.Write(plain);
        var compressed = buffer.ToArray();
        var redirect = ServeOnce(origin,
            "HTTP/1.1 302 Found\r\nLocation: http://" + targetEndpoint + "/media\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", [], deadline.Token);
        var download = ServeOnce(target,
            "HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: " + compressed.Length + "\r\nConnection: close\r\n\r\n", compressed, deadline.Token);
        try
        {
            using var response = await client.SendAsync(new Uri("http://" + originEndpoint + "/start"), HttpMethod.Get,
                preserveBytes ? new Dictionary<string, string> { ["Accept-Encoding"] = "identity" } : null, deadline.Token);
            var received = await response.Content.ReadAsByteArrayAsync(deadline.Token);

            Assert.Equal(preserveBytes ? compressed : plain, received);
            if (preserveBytes) Assert.Contains("gzip", response.Content.Headers.ContentEncoding);
            else Assert.Empty(response.Content.Headers.ContentEncoding);
            await Task.WhenAll(redirect, download);
        }
        finally
        {
            await deadline.CancelAsync();
            try { await Task.WhenAll(redirect, download); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task ChangedRedirectedRepresentationRestartsInsteadOfAppendingResumedBytes()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        using var target = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        target.Start();
        var originEndpoint = (IPEndPoint)origin.LocalEndpoint;
        var targetEndpoint = (IPEndPoint)target.LocalEndpoint;
        var configuration = new ConfigurationAccessor(() => new PluginConfiguration { AllowedPrivateHosts = ["127.0.0.1"] });
        using var client = new SafeHttpClient(configuration, new SsrfPolicy(configuration));
        var redirect = ServeOnce(origin, "HTTP/1.1 302 Found\r\nLocation: http://" + targetEndpoint
            + "/new-representation\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", [], deadline.Token);
        var representation = ServeChangedRepresentationAsync(target, deadline.Token);
        try
        {
            using var response = await client.SendAsync(new Uri("http://" + originEndpoint + "/start"), HttpMethod.Get,
                new Dictionary<string, string> { ["Range"] = "bytes=3-", ["If-Range"] = "\"old\"", ["Authorization"] = "Bearer private-fixture" }, deadline.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("NEW-CONTENT", await response.Content.ReadAsStringAsync(deadline.Token));
            await Task.WhenAll(redirect, representation);
        }
        finally
        {
            await deadline.CancelAsync();
            try { await Task.WhenAll(redirect, representation); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task ServeChangedRepresentationAsync(TcpListener listener, CancellationToken ct)
    {
        using var connection = await listener.AcceptTcpClientAsync(ct);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var lines = new List<string>();
        while (await reader.ReadLineAsync(ct) is { Length: > 0 } line) lines.Add(line);
        Assert.DoesNotContain(lines, line => line.Contains("private-fixture", StringComparison.Ordinal));
        var restart = lines.Any(line => line.Equals("If-Range: \"old\"", StringComparison.OrdinalIgnoreCase));
        var body = Encoding.ASCII.GetBytes(restart ? "NEW-CONTENT" : "-CONTENT");
        await stream.WriteAsync(Encoding.ASCII.GetBytes((restart ? "HTTP/1.1 200 OK" : "HTTP/1.1 206 Partial Content")
            + "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n"), ct);
        await stream.WriteAsync(body, ct);
    }

    private static async Task ServeOnce(TcpListener listener, string headers, byte[] body, CancellationToken cancellationToken)
    {
        using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 }) { }
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }
}
