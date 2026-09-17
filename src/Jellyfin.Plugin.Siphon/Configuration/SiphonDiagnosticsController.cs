using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Downloads;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Metadata;
using Jellyfin.Plugin.Siphon.Notifications;
using Jellyfin.Plugin.Siphon.P2p;
using Jellyfin.Plugin.Siphon.Protocol;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Siphon.Configuration;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Siphon/Diagnostics")]
public sealed class SiphonDiagnosticsController(ConfigurationAccessor configuration, StremioClient client,
    SyncDiagnostics diagnostics, SelfConnectionProbe connection, IServerApplicationHost applicationHost) : ControllerBase
{
    private static readonly Lazy<string?> RepositoryVersion = new(ReadRepositoryVersion);

    /// <summary>Aggregates existing in-memory observations; does not probe providers or enumerate media storage.</summary>
    [HttpGet("Health")]
    public ActionResult<HealthResponse> GetHealth(
        [FromServices] MetadataProviderClient providers,
        [FromServices] DownloadQueueService downloads,
        [FromServices] P2pStreamService peers,
        [FromServices] NotificationWebhookService notifications,
        [FromServices] SiphonPaths paths)
    {
        Response.Headers.CacheControl = "no-store";
        var local = GetDiagnostics().Value!;
        P2pHealth? peerStatus = null;
        string? peerCode = null;
        try
        {
            var value = peers.GetStatus();
            peerStatus = new(value.Enabled, value.ActiveStreams, value.MaximumStreams, value.ReservedBytes,
                value.CacheBytes, value.MaximumCacheBytes, value.DownloadLimitBytesPerSecond, value.UploadLimitBytesPerSecond,
                value.DhtEnabled, value.CacheMeasuredAtUtc);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            peerCode = "StorageUnavailable";
        }
        long? available = null;
        string? storageCode = null;
        try { available = new DriveInfo(paths.DataDirectory).AvailableFreeSpace; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            storageCode = "StorageUnavailable";
        }
        return new HealthResponse(local.GeneratedAtUtc, local.ServerVersion, local.PluginVersion, RepositoryVersion.Value,
            local.StorageCode, local.Addons, diagnostics.GetRunStatus(), providers.GetStatus(),
            downloads.GetHealth(), peerStatus, notifications.GetHealth(), new StorageHealth(available, storageCode), peerCode);
    }

    private static string? ReadRepositoryVersion()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Siphon.RepositoryManifest.json");
        if (stream is null || stream.Length > 65536) return null;
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 12 });
        Version? latest = null;
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var plugin in document.RootElement.EnumerateArray())
        {
            if (!plugin.TryGetProperty("guid", out var guid) || guid.ValueKind != JsonValueKind.String
                || !Guid.TryParse(guid.GetString(), out var id) || id != Plugin.PluginId
                || !plugin.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array) continue;
            foreach (var release in versions.EnumerateArray())
            {
                if (release.TryGetProperty("version", out var text) && text.ValueKind == JsonValueKind.String
                    && Version.TryParse(text.GetString(), out var version) && (latest is null || version > latest)) latest = version;
            }
        }
        return latest?.ToString();
    }

    /// <summary>Reads local observations only. Saved names and upstream addresses never enter this report.</summary>
    [HttpGet]
    public ActionResult<DiagnosticsResponse> GetDiagnostics()
    {
        var snapshot = diagnostics.GetSnapshot();
        var observations = snapshot.Addons.ToDictionary(addon => addon.InstallationId, StringComparer.Ordinal);
        var addons = configuration.Current.Addons.Where(addon => Guid.TryParse(addon.Id, out _)).Take(256).Select(addon =>
        {
            var id = Guid.Parse(addon.Id).ToString("N");
            observations.TryGetValue(id, out var observation);
            return new AddonDiagnosticResponse(id, addon.Enabled, addon.Catalogs.Count, addon.Catalogs.Count(catalog => catalog.Enabled),
                observation?.LastSuccessUtc, observation?.LastFailureUtc, observation?.LastErrorCode, observation?.LastManifestCheck);
        }).ToArray();
        return new DiagnosticsResponse(DateTimeOffset.UtcNow, applicationHost.ApplicationVersion.ToString(),
            typeof(Plugin).Assembly.GetName().Version?.ToString(), snapshot.StorageCode, addons);
    }

    [HttpGet("~/Siphon/Sync/Status")]
    public ActionResult<SyncRunStatus> GetSyncStatus() => diagnostics.GetRunStatus();

    [HttpPost("Addons/{installationId:guid}")]
    public async Task<ActionResult<ManifestTestResponse>> TestAddon(Guid installationId, CancellationToken cancellationToken)
    {
        var addon = configuration.Current.Addons.FirstOrDefault(addon => Guid.TryParse(addon.Id, out var id) && id == installationId);
        if (addon is null) return NotFound(new ManifestTestResponse(false, "AddonNotFound", 0, null));
        var stopwatch = Stopwatch.StartNew();
        // Bound the entire probe; a transport-owned deadline may expire before this one.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.Current.AddonTimeoutSeconds, 1, 120)));
        ManifestCheckDiagnostic check;
        try
        {
            var manifest = await client.GetManifestAsync(addon.ManifestUrl, deadline.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            check = diagnostics.RecordManifestCheck(addon.Id, true, "Ok", stopwatch.ElapsedMilliseconds, manifest);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            diagnostics.RecordManifestCheck(addon.Id, false, "Cancelled", stopwatch.ElapsedMilliseconds, null);
            throw;
        }
        catch (OperationCanceledException)
        {
            check = diagnostics.RecordManifestCheck(addon.Id, false, "Timeout", stopwatch.ElapsedMilliseconds, null);
        }
        catch (Exception error)
        {
            check = diagnostics.RecordManifestCheck(addon.Id, false,
                deadline.IsCancellationRequested || error is StremioException { IsTimeout: true } ? "Timeout" : "ManifestUnavailable",
                stopwatch.ElapsedMilliseconds, null);
        }

        return new ManifestTestResponse(check.Success, check.Code, check.ElapsedMilliseconds, check);
    }

    [HttpPost("Connection")]
    public Task<ConnectionTestResponse> TestConnection(CancellationToken cancellationToken) => connection.ProbeAsync(cancellationToken);

    public sealed record DiagnosticsResponse(DateTimeOffset GeneratedAtUtc, string? ServerVersion, string? PluginVersion, string? StorageCode,
        IReadOnlyList<AddonDiagnosticResponse> Addons);
    public sealed record AddonDiagnosticResponse(string InstallationId, bool Enabled, int CatalogCount, int EnabledCatalogCount,
        DateTimeOffset? LastSuccessUtc, DateTimeOffset? LastFailureUtc, string? LastErrorCode, ManifestCheckDiagnostic? LastManifestCheck);
    public sealed record ManifestTestResponse(bool Success, string Code, long ElapsedMilliseconds, ManifestCheckDiagnostic? Check);
    public sealed record HealthResponse(DateTimeOffset GeneratedAtUtc, string? ServerVersion, string? SourceVersion,
        string? RepositoryVersionAtBuild, string? StorageCode, IReadOnlyList<AddonDiagnosticResponse> Addons,
        SyncRunStatus Sync, IReadOnlyList<MetadataProviderStatus> Providers, DownloadQueueHealth Downloads,
        P2pHealth? P2p, NotificationWebhookHealth Notifications, StorageHealth Storage, string? P2pCode);
    public sealed record StorageHealth(long? AvailableBytes, string? Code);
    public sealed record P2pHealth(bool Enabled, int ActiveStreams, int MaximumStreams, long ReservedBytes, long CacheBytes,
        long MaximumCacheBytes, long DownloadLimitBytesPerSecond, long UploadLimitBytesPerSecond, bool DhtEnabled,
        DateTimeOffset? CacheMeasuredAtUtc);
}

public sealed record ConnectionTestResponse(bool Success, string Code, long ElapsedMilliseconds);

/// <summary>
/// A narrow saved-server self-probe, not a general URL transport. Supports container loopback without changing addon SSRF policy.
/// No redirects, cookies, proxy credentials, authentication, or caller-supplied URLs are accepted.
/// </summary>
public sealed class SelfConnectionProbe(ConfigurationAccessor configuration, IServerApplicationHost applicationHost) : IDisposable
{
    private const int MaximumResponseBytes = 64 * 1024;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        Credentials = null,
        AutomaticDecompression = DecompressionMethods.None,
        MaxResponseHeadersLength = 16,
        MaxConnectionsPerServer = 1,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<ConnectionTestResponse> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var config = configuration.Current;
        var savedUrl = config.PublicBaseUrl;
        if (string.IsNullOrWhiteSpace(savedUrl)) return Result(false, "NotConfigured");
        if (savedUrl.Length > 16384 || !Uri.TryCreate(savedUrl, UriKind.Absolute, out var saved)
            || saved.Scheme is not ("http" or "https") || string.IsNullOrEmpty(saved.Host)
            || saved.UserInfo.Length != 0 || saved.Query.Length != 0 || saved.Fragment.Length != 0)
        {
            return Result(false, "InvalidConfiguredUrl");
        }

        if (string.IsNullOrWhiteSpace(applicationHost.SystemId)) return Result(false, "ServerIdentityUnavailable");
        var endpoint = new Uri(saved.AbsoluteUri.TrimEnd('/') + "/System/Info/Public");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.AddonTimeoutSeconds, 1, 120)));
        var acquired = false;
        try
        {
            await _requests.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400) return Result(false, "RedirectBlocked");
            if (!response.IsSuccessStatusCode) return Result(false, "HttpError");
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) return Result(false, "ResponseTooLarge");
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            int count;
            while ((count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > MaximumResponseBytes) return Result(false, "ResponseTooLarge");
                output.Write(buffer, 0, count);
            }

            using var document = JsonDocument.Parse(output.GetBuffer().AsMemory(0, (int)output.Length), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString()))
            {
                return Result(false, "InvalidServerResponse");
            }

            return string.Equals(id.GetString(), applicationHost.SystemId, StringComparison.Ordinal)
                ? Result(true, "Ok") : Result(false, "WrongServer");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Result(false, "Timeout"); }
        catch (JsonException) { return Result(false, "InvalidServerResponse"); }
        catch (Exception exception) when (exception is HttpRequestException or IOException) { return Result(false, "Unreachable"); }
        finally { if (acquired) _requests.Release(); }

        ConnectionTestResponse Result(bool success, string code) => new(success, code, stopwatch.ElapsedMilliseconds);
    }

    public void Dispose()
    {
        _http.Dispose();
        _requests.Dispose();
    }
}
