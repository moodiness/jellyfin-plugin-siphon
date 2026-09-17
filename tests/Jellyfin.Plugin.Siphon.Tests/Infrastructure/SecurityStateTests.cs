using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Common.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Infrastructure;

public sealed class SecurityStateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "siphon-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.100.100.200")]
    [InlineData("10.0.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("198.19.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("2002:7f00:1::1")]
    [InlineData("2001:db8::1")]
    [InlineData("64:ff9b::7f00:1")]
    public void UnsafeAddressesAreRejected(string address) => Assert.False(SsrfPolicy.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void PublicAddressesAreAccepted(string address) => Assert.True(SsrfPolicy.IsPublicAddress(IPAddress.Parse(address)));

    [Fact]
    public void PrivateExceptionsAreExactHosts()
    {
        var policy = new SsrfPolicy(new ConfigurationAccessor(() => new PluginConfiguration { AllowedPrivateHosts = ["addon.local"] }));
        Assert.True(policy.IsAllowed("addon.local", IPAddress.Loopback));
        Assert.False(policy.IsAllowed("evil.addon.local", IPAddress.Loopback));
        Assert.False(policy.IsAllowed("addon.local.evil", IPAddress.Loopback));
    }
    [Fact]
    public void PrivateIpExceptionRequiresExactConfiguredAddress()
    {
        var policy = new SsrfPolicy(new ConfigurationAccessor(() => new PluginConfiguration { AllowedPrivateHosts = ["10.23.45.67"] }));
        Assert.True(policy.IsAllowed("10.23.45.67", IPAddress.Parse("10.23.45.67")));
        Assert.False(policy.IsAllowed("10.23.45.68", IPAddress.Parse("10.23.45.68")));
    }

    [Fact]
    public void CapabilitiesSurviveRestartAndRejectTampering()
    {
        var first = new CapabilityTokenService(new SiphonSecretStore(Paths()));
        var token = first.SignItem("movie:tt1234567");
        var restarted = new CapabilityTokenService(new SiphonSecretStore(Paths()));
        Assert.True(restarted.TryReadItem(token, out var key));
        Assert.Equal("movie:tt1234567", key);
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("1:movie:tt9999999")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.False(restarted.TryReadItem(payload + token[token.IndexOf('.')..], out _));
        Assert.False(restarted.TryReadItem(token + "=", out _));
    }

    [Fact]
    public void OversizedProviderIdsCannotPoisonPlaybackCapabilities()
    {
        var key = ContentIdentity.Key("movie", "opaque", "installation", new Dictionary<string, string>
        {
            ["Tmdb"] = new string('9', 1024),
            ["Tvdb"] = "42"
        });
        var tokens = new CapabilityTokenService(new SiphonSecretStore(Paths()));
        var capability = tokens.SignSource(key, new string('A', 64));
        Assert.True(tokens.TryReadSource(capability, out var restored, out _));
        Assert.Equal("movie:tvdb:42", restored);
    }

    [Fact]
    public void CorruptKeyIsNeverRegenerated()
    {
        var paths = Paths();
        Directory.CreateDirectory(paths.DataDirectory);
        var keyPath = Path.Combine(paths.DataDirectory, "signing.key");
        File.WriteAllText(keyPath, "corrupt");
        Assert.Throws<InvalidDataException>(() => new SiphonSecretStore(paths).GetSigningKey());
        Assert.Equal("corrupt", File.ReadAllText(keyPath));
    }

    [Fact]
    public void CorruptStateCannotBeOverwrittenByEmptyFallback()
    {
        var paths = Paths();
        Directory.CreateDirectory(paths.DataDirectory);
        var statePath = Path.Combine(paths.DataDirectory, "state.json");
        File.WriteAllText(statePath, "{}");
        Assert.Throws<InvalidDataException>(() => new SiphonStateStore(paths));
        Assert.Equal("{}", File.ReadAllText(statePath));
    }

    [Fact]
    public async Task CanceledSaveKeepsDurableAndCachedIdentityAndSnapshotsAreIsolated()
    {
        var paths = Paths();
        using var store = new SiphonStateStore(paths);
        var item = new ManagedItem
        {
            Key = "movie:tt1234567",
            Type = "movie",
            ContentId = "tt1234567",
            ContentKey = "movie:tt1234567",
            ProviderIds = new() { ["Imdb"] = "tt1234567" },
            VideoId = "tt1234567",
            Name = "Film",
            Path = Path.Combine(_directory, "Film.strm"),
            Owners = ["catalog-a"]
        };
        await store.SaveAsync([item], CancellationToken.None);
        item.Owners[0] = "mutated-input";
        store.GetItems()[0].Owners[0] = "mutated-output";
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync([], canceled.Token));
        using var restarted = new SiphonStateStore(paths);
        Assert.Equal("catalog-a", Assert.Single(store.GetItems()).Owners[0]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync([item, item], CancellationToken.None));
        using var afterRejectedSave = new SiphonStateStore(paths);
        Assert.Equal("catalog-a", Assert.Single(afterRejectedSave.GetItems()).Owners[0]);
        Assert.Equal("catalog-a", Assert.Single(restarted.GetItems()).Owners[0]);
        Assert.Equal(item.Key, restarted.FindByPath(item.Path)?.Key);
        Assert.Equal(item.Path, restarted.FindByKey(item.Key)?.Path);
    }

    [Fact]
    public async Task LegacyImdbStateMigratesWithoutChangingPathsOrKeys()
    {
        var paths = Paths();
        Directory.CreateDirectory(paths.DataDirectory);
        var path = Path.Combine(_directory, "old", "episode.strm");
        var legacy = new { Version = 1, Items = new[] { new { Key = "series:tt1234567:1:2", Type = "series", ImdbId = "tt1234567", VideoId = "tt1234567:1:2", Name = "Episode", Path = path, Owners = new[] { "owner" } } } };
        await File.WriteAllTextAsync(Path.Combine(paths.DataDirectory, "state.json"), System.Text.Json.JsonSerializer.Serialize(legacy));
        using var state = new SiphonStateStore(paths);
        var item = Assert.Single(state.GetItems());
        Assert.Equal("series:tt1234567:1:2", item.Key);
        Assert.Equal(path, item.Path);
        Assert.Equal("tt1234567", item.ProviderIds["Imdb"]);
        Assert.Equal("series:tt1234567", item.ContentKey);
        Assert.Equal("tt1234567:1:2", Assert.Single(item.StreamIdentities).VideoId);
        await state.SaveAsync(state.GetItems(), CancellationToken.None);
        using var restarted = new SiphonStateStore(paths);
        Assert.Equal(item.Key, restarted.FindByPath(path)?.Key);
    }

    [Fact]
    public async Task NativePlaybackUrlResolvesSignedIdentityWithoutFilesystemNormalization()
    {
        var paths = Paths();
        using var state = new SiphonStateStore(paths);
        var item = new ManagedItem
        {
            Key = "movie:tt1234567",
            Type = "movie",
            ContentId = "tt1234567",
            ContentKey = "movie:tt1234567",
            VideoId = "tt1234567",
            Name = "Film",
            Path = Path.Combine(_directory, "Film.strm")
        };
        await state.SaveAsync([item], CancellationToken.None);
        var tokens = new CapabilityTokenService(new SiphonSecretStore(paths));
        var locator = new SiphonItemLocator(state, tokens);
        var url = "https://jellyfin.example/base/Siphon/s/" + tokens.SignItem(item.Key);

        Assert.Equal(item.Key, locator.Find(url)?.Key);
        Assert.Null(locator.Find(url + "invalid"));
    }

    private SiphonPaths Paths()
    {
        var proxy = DispatchProxy.Create<IApplicationPaths, PathsProxy>();
        ((PathsProxy)(object)proxy).DataPath = _directory;
        return new SiphonPaths(proxy);
    }

    public class PathsProxy : DispatchProxy
    {
        public string DataPath { get; set; } = string.Empty;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name == "get_DataPath"
            ? DataPath : throw new NotSupportedException();
    }

    [Fact]
    public async Task RedirectsRevalidateDestinationAndStripCrossOriginSecrets()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        using var target = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        target.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var first = ServeOnce(origin, $"HTTP/1.1 302 Found\r\nLocation: http://localhost:{targetPort}/next\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", deadline.Token);
        var second = ServeOnce(target, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK", deadline.Token);
        var config = new ConfigurationAccessor(() => new PluginConfiguration { AllowedPrivateHosts = ["localhost"] });
        using var client = new SafeHttpClient(config, new SsrfPolicy(config));
        using var response = await client.SendAsync(new Uri($"http://localhost:{originPort}/start"), HttpMethod.Get,
            new Dictionary<string, string> { ["Authorization"] = "Bearer secret", ["Cookie"] = "secret=value", ["X-Api-Key"] = "secret" }, deadline.Token);
        Assert.Equal("OK", await response.Content.ReadAsStringAsync(deadline.Token));
        Assert.Contains("Authorization: Bearer secret", await first);
        var redirectedHeaders = await second;
        Assert.DoesNotContain("secret", redirectedHeaders);

        var blocked = ServeOnce(origin, $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{targetPort}/private\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", deadline.Token);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(new Uri($"http://localhost:{originPort}/start"), HttpMethod.Get, null, deadline.Token));
        await blocked;
    }

    [Fact]
    public async Task AuthenticationRedirectsCannotReplayCredentials()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        using var target = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        target.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var served = ServeOnce(origin,
            $"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://localhost:{targetPort}/collect\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            deadline.Token, readBody: true);
        var configuration = new ConfigurationAccessor(() => new PluginConfiguration { AllowedPrivateHosts = ["localhost"] });
        using var client = new SafeHttpClient(configuration, new SsrfPolicy(configuration));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.PostJsonAsync(
            new Uri($"http://localhost:{originPort}/login"), "{\"apikey\":\"fixture-secret\"}", null, deadline.Token));
        await served;
        Assert.False(target.Pending());
    }

    private static async Task<string> ServeOnce(TcpListener listener, string response, CancellationToken cancellationToken, bool readBody = false)
    {
        using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var headers = new StringBuilder();
        var contentLength = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } line)
        {
            headers.AppendLine(line);
            if (readBody && line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line["Content-Length:".Length..], System.Globalization.CultureInfo.InvariantCulture);
        }
        if (contentLength > 0)
        {
            var body = new char[contentLength];
            var received = await reader.ReadBlockAsync(body.AsMemory(), cancellationToken);
            if (received != contentLength) throw new EndOfStreamException();
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
        return headers.ToString();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
