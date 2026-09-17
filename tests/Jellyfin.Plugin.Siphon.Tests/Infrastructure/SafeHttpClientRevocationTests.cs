using System.Net;
using System.Net.Sockets;
using System.Text;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Infrastructure;

public sealed class SafeHttpClientRevocationTests
{
    [Fact]
    public async Task RevokingPrivateHostCannotReuseAnAlreadyApprovedKeepAliveConnection()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var endpoint = (IPEndPoint)origin.LocalEndpoint;
        var settings = new PluginConfiguration { AllowedPrivateHosts = [endpoint.Address.ToString()] };
        var configuration = new ConfigurationAccessor(() => settings);
        using var client = new SafeHttpClient(configuration, new SsrfPolicy(configuration));
        var uri = new Uri("http://" + endpoint + "/representation");
        var served = ServeKeepAlive(origin, deadline.Token);
        try
        {
            using (var response = await client.SendAsync(uri, HttpMethod.Get, null, deadline.Token))
            {
                Assert.Equal("OK", await response.Content.ReadAsStringAsync(deadline.Token));
            }

            settings.AllowedPrivateHosts.Clear();

            await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(uri, HttpMethod.Get, null, deadline.Token));
            Assert.Equal(1, await served.WaitAsync(deadline.Token));
        }
        finally
        {
            await deadline.CancelAsync();
            try { await served; }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<int> ServeKeepAlive(TcpListener listener, CancellationToken cancellationToken)
    {
        using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var served = 0;
        while (await reader.ReadLineAsync(cancellationToken) is not null)
        {
            while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 }) { }
            served++;
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: keep-alive\r\n\r\nOK"), cancellationToken);
            if (served == 2) break;
        }
        return served;
    }
}
