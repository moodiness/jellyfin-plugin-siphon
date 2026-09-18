using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Siphon.Calendar;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Notifications;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Notifications;

public sealed class NotificationWebhookDeliveryTests
{
    [Fact]
    public async Task RealTestIsLabeledSignedAndRateLimitedAndCannotUseAnotherUsersEndpoint()
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var url = "http://" + origin.LocalEndpoint + "/secret-path?token=private";
        await fixture.Preferences.SaveAsync(fixture.User.Id, new() { NotificationsEnabled = true }, deadline.Token);
        await fixture.Service.ConfigureAsync(fixture.User.Id, new(true, url), deadline.Token);
        var secret = await fixture.Service.RotateAsync(fixture.User.Id, deadline.Token);
        var served = Receive(origin, "204 No Content", deadline.Token);
        var result = await fixture.Service.TestAsync(fixture.User.Id, deadline.Token);
        var request = await served;
        Assert.True(result.Success);
        Assert.StartsWith("POST /secret-path?token=private HTTP/1.1", request.StartLine);
        using var document = JsonDocument.Parse(request.Body);
        Assert.True(document.RootElement.GetProperty("Test").GetBoolean());
        Assert.Equal("webhook.test", document.RootElement.GetProperty("Type").GetString());
        Assert.Equal(document.RootElement.GetProperty("EventId").GetGuid().ToString("D"), request.Headers["X-Siphon-Event-Id"]);
        Assert.Equal("sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Convert.FromBase64String(secret), Encoding.UTF8.GetBytes(request.Body))),
            request.Headers["X-Siphon-Signature"]);
        await Assert.ThrowsAsync<WebhookTestRateLimitException>(() => fixture.Service.TestAsync(fixture.User.Id, deadline.Token));
        Assert.Empty(fixture.Service.GetStatus(Guid.NewGuid()).Url);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.TestAsync(Guid.NewGuid(), deadline.Token));
    }

    [Fact]
    public async Task RedirectsAreNotFollowedAndRevokedHttpDestinationCanStillBeDisabled()
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var url = "http://" + origin.LocalEndpoint + "/private-token";
        await fixture.Preferences.SaveAsync(fixture.User.Id, new() { NotificationsEnabled = true }, deadline.Token);
        await fixture.Service.ConfigureAsync(fixture.User.Id, new(true, url), deadline.Token);
        await fixture.Service.RotateAsync(fixture.User.Id, deadline.Token);
        var served = Receive(origin, "302 Found\r\nLocation: " + url, deadline.Token);
        var result = await fixture.Service.TestAsync(fixture.User.Id, deadline.Token);
        await served;
        Assert.False(result.Success);
        Assert.DoesNotContain("private-token", result.Message);
        Assert.False(origin.Pending());
        fixture.Settings.AllowedPrivateHosts.Clear();
        await fixture.Service.ConfigureAsync(fixture.User.Id, new(false, url), deadline.Token);
        Assert.False(fixture.Service.GetStatus(fixture.User.Id).Enabled);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.ConfigureAsync(fixture.User.Id, new(true, url), deadline.Token));
    }

    [Fact]
    public async Task AllOptInsAndCurrentAccountPermissionAreRequired()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConfigureAsync(fixture.User.Id, new(true, "https://example.com/hook"), default));
        await fixture.Preferences.SaveAsync(fixture.User.Id, new() { NotificationsEnabled = true }, default);
        await fixture.Service.ConfigureAsync(fixture.User.Id, new(true, "https://example.com/hook"), default);
        await fixture.Service.RotateAsync(fixture.User.Id, default);
        fixture.Settings.EnableNotificationWebhooks = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.TestAsync(fixture.User.Id, default));
        fixture.Settings.EnableNotificationWebhooks = true;
        fixture.User.SetPermission(PermissionKind.IsDisabled, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.TestAsync(fixture.User.Id, default));
    }

    [Theory]
    [InlineData("Discord", "/private-topic?wait=true", "content")]
    [InlineData("Slack", "/private-topic", "blocks")]
    [InlineData("Ntfy", "/", "topic")]
    public async Task VendorTestsUseCheckedPostWithoutManualSecretExchange(string adapter, string path, string requiredField)
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var url = "http://" + origin.LocalEndpoint + "/private-topic";
        await fixture.Preferences.SaveAsync(fixture.User.Id, new() { NotificationsEnabled = true }, deadline.Token);
        await fixture.Service.ConfigureAsync(fixture.User.Id, new(true, url) { Adapter = adapter }, deadline.Token);
        var served = Receive(origin, "204 No Content", deadline.Token);
        var result = await fixture.Service.TestAsync(fixture.User.Id, deadline.Token);
        var request = await served;
        Assert.True(result.Success);
        Assert.Equal("POST " + path + " HTTP/1.1", request.StartLine);
        using var document = JsonDocument.Parse(request.Body);
        Assert.True(document.RootElement.TryGetProperty(requiredField, out _));
        Assert.StartsWith("sha256=", request.Headers["X-Siphon-Signature"]);
        var status = fixture.Service.GetStatus(fixture.User.Id);
        Assert.Equal("Delivered", status.LastOutcome);
        Assert.Equal(1, status.LastAttemptCount);
        Assert.NotNull(status.LastAttemptUtc);
        Assert.Null(status.NextAttemptUtc);
        Assert.Equal(1, fixture.Service.GetHealth().ConfiguredUsers);
    }

    [Fact]
    public async Task FailedTestIsExplicitlyAbandonedAndStatusNeverIncludesReceiverResponse()
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        await fixture.Preferences.SaveAsync(fixture.User.Id, new() { NotificationsEnabled = true }, deadline.Token);
        await fixture.Service.ConfigureAsync(fixture.User.Id,
            new(true, "http://" + origin.LocalEndpoint + "/private-topic") { Adapter = "Slack" }, deadline.Token);
        var served = Receive(origin, "503 secret-receiver-error", deadline.Token);
        var result = await fixture.Service.TestAsync(fixture.User.Id, deadline.Token);
        await served;
        Assert.False(result.Success);
        Assert.Equal("WebhookDeliveryFailed", result.Code);
        var status = fixture.Service.GetStatus(fixture.User.Id);
        Assert.Equal("Abandoned", status.LastOutcome);
        Assert.Equal(1, status.AbandonedCount);
        Assert.Equal(1, fixture.Service.GetHealth().Abandoned);
        Assert.DoesNotContain("secret-receiver-error", JsonSerializer.Serialize(status));
        Assert.Null(status.NextAttemptUtc);
    }

    private static async Task<Request> Receive(TcpListener origin, string response, CancellationToken ct)
    {
        using var connection = await origin.AcceptTcpClientAsync(ct);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var start = await reader.ReadLineAsync(ct) ?? throw new IOException("Missing request.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadLineAsync(ct) is { Length: > 0 } line)
        {
            var separator = line.IndexOf(':');
            headers[line[..separator]] = line[(separator + 1)..].Trim();
        }
        var body = new char[int.Parse(headers["Content-Length"], System.Globalization.CultureInfo.InvariantCulture)];
        if (await reader.ReadBlockAsync(body, ct) != body.Length) throw new IOException("Incomplete test payload.");
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 " + response + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), ct);
        return new(start, headers, new string(body));
    }

    private sealed record Request(string StartLine, Dictionary<string, string> Headers, string Body);

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "siphon-webhook-delivery-" + Guid.NewGuid().ToString("N"));
        private readonly NotificationWebhookStore _store;
        private readonly CalendarNotificationStore _inbox;
        private readonly SafeHttpClient _http;
        public PluginConfiguration Settings { get; } = new() { EnableCalendarNotifications = true, EnableNotificationWebhooks = true, AllowedPrivateHosts = ["127.0.0.1"] };
        public User User { get; } = new("viewer", "authentication", "password-reset") { Id = Guid.NewGuid() };
        public UserPreferenceStore Preferences { get; }
        public NotificationWebhookService Service { get; }

        public Fixture()
        {
            User.SetPermission(PermissionKind.IsAdministrator, true);
            var paths = DispatchProxy.Create<IApplicationPaths, Boundary>();
            ((Boundary)(object)paths).Call = (method, _) => method.Name == "get_DataPath" ? _directory : throw new NotSupportedException();
            var users = DispatchProxy.Create<IUserManager, Boundary>();
            ((Boundary)(object)users).Call = (method, args) => method.Name == "GetUserById"
                ? (Guid)args![0]! == User.Id ? User : null : throw new NotSupportedException();
            var configuration = new ConfigurationAccessor(() => Settings);
            var siphonPaths = new SiphonPaths(paths);
            var policy = new SsrfPolicy(configuration);
            Preferences = new(siphonPaths, configuration);
            _store = new(siphonPaths, new SiphonSecretStore(siphonPaths));
            _inbox = new(siphonPaths);
            _http = new(configuration, policy);
            Service = new(_store, _inbox, null!, Preferences, configuration, users, _http, policy, NullLogger<NotificationWebhookService>.Instance);
        }

        public void Dispose()
        {
            Service.Dispose();
            Preferences.Dispose();
            _store.Dispose();
            _inbox.Dispose();
            _http.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    public class Boundary : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args);
    }
}
