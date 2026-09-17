using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;

namespace Jellyfin.Plugin.Siphon.Metadata;

public sealed record MetadataProviderResult(string Provider, bool Success, string Code, long ElapsedMilliseconds);
public sealed record MetadataProviderStatus(string Provider, bool Configured, bool Enabled, MetadataProviderResult? LastResult,
    DateTimeOffset? PausedUntilUtc = null, string? PauseReason = null, int ConsecutiveFailures = 0);

/// <summary>Fixed documented API origins, bounded typed responses and credential-free outcomes.</summary>
public sealed class MetadataProviderClient(ConfigurationAccessor configuration, ISafeHttpClient http, SafeHttpClient post,
    MetadataResponseCache? cache = null, TimeProvider? timeProvider = null) : IDisposable
{
    public static readonly IReadOnlyList<string> ProviderNames = Array.AsReadOnly(new[] { "Tmdb", "Tvdb", "Fanart", "MdbList" });
    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, MaxDepth = 48, RespectNullableAnnotations = true };
    private static readonly JsonSerializerOptions Camel = new() { PropertyNameCaseInsensitive = true, MaxDepth = 48, RespectNullableAnnotations = true };
    private readonly ConcurrentDictionary<string, MetadataProviderResult> _results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RequestGate> _gates = ProviderNames.ToDictionary(name => name, _ => new RequestGate(), StringComparer.Ordinal);
    private readonly AsyncLocal<RequestScope?> _scope = new();
    private readonly AsyncLocal<bool> _probe = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _tvdbToken;
    private string? _tvdbCredential;
    private DateTimeOffset _tokenExpires;

    internal IDisposable BeginScope(PluginConfiguration config, string? language, string identity, string episodeSet, bool force, SyncDiagnostics? diagnostics)
    {
        var previous = _scope.Value;
        var policy = Digest(JsonSerializer.Serialize(new
        {
            Schema = 1,
            language,
            identity,
            episodeSet,
            config.MetadataUpdateMode,
            Fields = config.MetadataRefreshFields.Order(StringComparer.Ordinal),
            config.EnableTmdbMetadata,
            config.EnableTvdbMetadata,
            config.EnableFanartMetadata,
            config.EnableMdbListMetadata
        }));
        _scope.Value = new(config, policy, force, diagnostics);
        return new ScopeLease(() => _scope.Value = previous);
    }

    public IReadOnlyList<MetadataProviderStatus> GetStatus()
    {
        var config = configuration.Current;
        return ProviderNames.Select(name =>
        {
            var gate = Gate(name, config);
            lock (gate)
            {
                var paused = gate.BlockedUntil > _clock.GetUtcNow();
                return new MetadataProviderStatus(name, !string.IsNullOrWhiteSpace(Key(config, name)), Enabled(config, name),
                    _results.GetValueOrDefault(name), paused ? gate.BlockedUntil : null, paused ? gate.BlockCode : null, gate.Failures);
            }
        }).ToArray();
    }

    public async Task<MetadataProviderResult> TestAsync(string provider, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ProviderNames.Contains(provider, StringComparer.Ordinal)) return new("Unknown", false, "UnknownProvider", 0);
        var previous = _scope.Value;
        var previousProbe = _probe.Value;
        _probe.Value = true;
        _scope.Value = null; // Explicit tests never consume cached successes or synchronization counters.
        try
        {
            var config = configuration.Current;
            var result = await TryAsync(provider, config, async () =>
            {
                switch (provider)
                {
                    case "Tmdb":
                        await GetAsync<TmdbConfiguration>(provider, "https://api.themoviedb.org/3/configuration", Bearer(config.TmdbReadAccessToken), true, ct,
                            value => ValidImageBase(value.Images?.SecureBaseUrl)).ConfigureAwait(false);
                        break;
                    case "Tvdb":
                        await TvdbTokenAsync(config, true, ct).ConfigureAwait(false);
                        break;
                    case "Fanart":
                        await GetAsync<FanartRecord>(provider, FanartUrl(config, "movies/550"), null, false, ct, value => value.TmdbId == "550").ConfigureAwait(false);
                        break;
                    case "MdbList":
                        await GetAsync<MdbUser>(provider, MdbUrl(config, "user"), null, false, ct,
                            value => !string.IsNullOrWhiteSpace(value.Username) || value.UserId is > 0).ConfigureAwait(false);
                        break;
                }
                return true;
            }, ct).ConfigureAwait(false);
            return result.Result;
        }
        finally { _scope.Value = previous; _probe.Value = previousProbe; }
    }

    internal async Task<(T? Value, MetadataProviderResult Result)> TryAsync<T>(string provider, PluginConfiguration config, Func<Task<T>> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = Stopwatch.GetTimestamp();
        T? value = default;
        var code = "Success";
        if (string.IsNullOrWhiteSpace(Key(config, provider))) code = "MissingCredentials";
        else
        {
            try { value = await action().ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { ct.ThrowIfCancellationRequested(); code = FailureCode(ex); }
        }
        var result = new MetadataProviderResult(provider, code == "Success", code, (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        _results[provider] = result;
        if (!result.Success) _scope.Value?.Diagnostics?.RecordProviderError(provider, code);
        return (value, result);
    }

    internal async Task<T> GetAsync<T>(string provider, string url, IReadOnlyDictionary<string, string>? headers, bool snake, CancellationToken ct,
        Func<T, bool> valid, Func<Task<IReadOnlyDictionary<string, string>>>? authorize = null) where T : class
    {
        ct.ThrowIfCancellationRequested();
        var scope = _scope.Value;
        var config = scope?.Config ?? configuration.Current;
        var credential = Credential(config, provider);
        var gate = Gate(provider, config);
        var key = Digest(JsonSerializer.Serialize(new { provider, url, Type = typeof(T).FullName, Policy = scope?.Policy, credential }));
        if (cache is not null && scope is { Force: false })
        {
            var saved = await cache.ReadAsync<T>(key, _clock.GetUtcNow(), TimeSpan.FromHours(Math.Clamp(config.MetadataCacheHours, 1, 168)), ct).ConfigureAwait(false);
            if (saved is not null && valid(saved))
            {
                ct.ThrowIfCancellationRequested();
                scope.Diagnostics?.RecordProviderRequest(provider, true);
                return saved;
            }
        }
        CheckPause(gate);
        if (authorize is not null) headers = await authorize().ConfigureAwait(false);
        await gate.Mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CheckPause(gate);
            await PaceAsync(gate, ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.AddonTimeoutSeconds, 1, 120)));
            try
            {
                scope?.Diagnostics?.RecordProviderRequest(provider, false);
                using var response = await http.SendAsync(new Uri(url), HttpMethod.Get, headers, timeout.Token).ConfigureAwait(false);
                CheckStatus(response, gate, credential);
                var value = await ReadAsync<T>(response, snake, timeout.Token).ConfigureAwait(false);
                if (!valid(value)) throw new ProviderFailure("InvalidResponse");
                ct.ThrowIfCancellationRequested();
                Recover(gate, credential);
                if (cache is not null && scope is not null) await cache.WriteAsync(key, value, _clock.GetUtcNow(), ct).ConfigureAwait(false);
                return value;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { ct.ThrowIfCancellationRequested(); Fail(gate, credential, FailureCode(ex)); throw; }
        }
        finally { gate.NextRequest = _clock.GetUtcNow().AddMilliseconds(150); gate.Mutex.Release(); }
    }

    internal async Task<string> TmdbImageBaseAsync(PluginConfiguration config, CancellationToken ct)
    {
        var result = await GetAsync<TmdbConfiguration>("Tmdb", "https://api.themoviedb.org/3/configuration", Bearer(config.TmdbReadAccessToken), true, ct,
            value => ValidImageBase(value.Images?.SecureBaseUrl)).ConfigureAwait(false);
        return result.Images!.SecureBaseUrl!;
    }

    internal async Task<string> TvdbTokenAsync(PluginConfiguration config, bool force, CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var credential = Credential(config, "Tvdb");
            if (!force && _tvdbToken is not null && _tvdbCredential == credential && _tokenExpires > _clock.GetUtcNow()) return _tvdbToken;
            var gate = Gate("Tvdb", config);
            await gate.Mutex.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                CheckPause(gate);
                await PaceAsync(gate, ct).ConfigureAwait(false);
                var payload = new Dictionary<string, string> { ["apikey"] = config.TvdbApiKey.Trim() };
                if (!string.IsNullOrWhiteSpace(config.TvdbSubscriberPin)) payload["pin"] = config.TvdbSubscriberPin.Trim();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.AddonTimeoutSeconds, 1, 120)));
                try
                {
                    _scope.Value?.Diagnostics?.RecordProviderRequest("Tvdb", false);
                    using var response = await post.PostJsonAsync(new Uri("https://api4.thetvdb.com/v4/login"), JsonSerializer.Serialize(payload), null, timeout.Token).ConfigureAwait(false);
                    CheckStatus(response, gate, credential);
                    var login = await ReadAsync<TvdbEnvelope<TvdbLogin>>(response, false, timeout.Token).ConfigureAwait(false);
                    if (login.Status != "success" || string.IsNullOrWhiteSpace(login.Data?.Token)) throw new ProviderFailure("AuthenticationFailed");
                    ct.ThrowIfCancellationRequested();
                    Recover(gate, credential);
                    _tvdbCredential = credential;
                    _tokenExpires = _clock.GetUtcNow().AddDays(25);
                    return _tvdbToken = login.Data.Token;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { ct.ThrowIfCancellationRequested(); Fail(gate, credential, FailureCode(ex)); throw; }
            }
            finally { gate.NextRequest = _clock.GetUtcNow().AddMilliseconds(150); gate.Mutex.Release(); }
        }
        finally { _tokenGate.Release(); }
    }

    internal async Task<T> TvdbAsync<T>(PluginConfiguration config, string path, CancellationToken ct, Func<T, bool> valid) where T : class
    {
        try
        {
            var response = await GetAsync<TvdbEnvelope<T>>("Tvdb", "https://api4.thetvdb.com/v4/" + path, null, false, ct,
                value => value.Status == "success" && value.Data is not null && valid(value.Data),
                async () => Bearer(await TvdbTokenAsync(config, false, ct).ConfigureAwait(false))).ConfigureAwait(false);
            return response.Data!;
        }
        catch (ProviderFailure ex) when (ex.Code == "AuthenticationFailed") { _tvdbToken = null; throw; }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, bool snake, CancellationToken ct)
    {
        const int limit = 4 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new ProviderFailure("ResponseTooLarge");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (body.Length + count > limit) throw new ProviderFailure("ResponseTooLarge");
            body.Write(buffer, 0, count);
        }
        body.Position = 0;
        return await JsonSerializer.DeserializeAsync<T>(body, snake ? Snake : Camel, ct).ConfigureAwait(false) ?? throw new ProviderFailure("InvalidResponse");
    }

    private void CheckStatus(HttpResponseMessage response, RequestGate gate, string credential)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var now = _clock.GetUtcNow();
            var until = response.Headers.RetryAfter?.Date ?? now.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5));
            lock (gate)
            {
                if (gate.Credential == credential)
                {
                    gate.BlockCode = "RateLimited";
                    gate.BlockedUntil = until > now ? until : now.AddSeconds(1);
                }
            }
            throw new ProviderFailure("RateLimited");
        }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new ProviderFailure("AuthenticationFailed");
        if (response.StatusCode == HttpStatusCode.NotFound) throw new ProviderFailure("NotFound");
        if (!response.IsSuccessStatusCode) throw new ProviderFailure("ProviderUnavailable");
    }

    private RequestGate Gate(string provider, PluginConfiguration config)
    {
        var gate = _gates[provider];
        var credential = Credential(config, provider);
        lock (gate)
        {
            if (gate.Credential != credential)
            {
                gate.Credential = credential;
                gate.Failures = 0;
                gate.BlockedUntil = default;
                gate.BlockCode = "ProviderUnavailable";
                _results.TryRemove(provider, out _);
            }
        }
        return gate;
    }

    private void CheckPause(RequestGate gate)
    {
        lock (gate)
        {
            // An administrator may probe recovery, but never circumvent a provider's quota deadline.
            if (gate.BlockedUntil > _clock.GetUtcNow() && (!_probe.Value || gate.BlockCode == "RateLimited"))
                throw new ProviderFailure(gate.BlockCode);
        }
    }
    private async Task PaceAsync(RequestGate gate, CancellationToken ct)
    {
        var delay = gate.NextRequest - _clock.GetUtcNow();
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
    }
    private static void Recover(RequestGate gate, string credential)
    {
        lock (gate)
        {
            if (gate.Credential != credential) return;
            gate.Failures = 0;
            gate.BlockedUntil = default;
        }
    }
    private void Fail(RequestGate gate, string credential, string code)
    {
        lock (gate)
        {
            if (gate.Credential != credential) return;
            if (code is "Timeout" or "NetworkError" or "ProviderUnavailable") gate.Failures = Math.Min(3, gate.Failures + 1);
            else if (code != "RateLimited") gate.Failures = 0;
            if (code == "AuthenticationFailed" || ((code is "Timeout" or "NetworkError" or "ProviderUnavailable") && gate.Failures >= 3))
            {
                gate.BlockCode = code;
                gate.BlockedUntil = _clock.GetUtcNow().AddMinutes(5);
            }
        }
    }
    private static string FailureCode(Exception ex) => ex switch
    {
        OperationCanceledException => "Timeout",
        ProviderFailure failure => failure.Code,
        JsonException => "InvalidResponse",
        _ => "NetworkError"
    };
    private static string Credential(PluginConfiguration config, string provider)
        => Digest(JsonSerializer.Serialize(new[] { Key(config, provider).Trim(), provider == "Tvdb" ? config.TvdbSubscriberPin.Trim() : "" }));
    private static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static Dictionary<string, string> Bearer(string token) => new() { ["Authorization"] = "Bearer " + token.Trim() };
    internal static string FanartUrl(PluginConfiguration config, string path) => "https://webservice.fanart.tv/v3.2/" + path + "?api_key=" + Uri.EscapeDataString(config.FanartApiKey.Trim());
    internal static string MdbUrl(PluginConfiguration config, string path) => "https://api.mdblist.com/" + path + "?apikey=" + Uri.EscapeDataString(config.MdbListApiKey.Trim());
    internal static bool Enabled(PluginConfiguration config, string provider) => provider switch { "Tmdb" => config.EnableTmdbMetadata, "Tvdb" => config.EnableTvdbMetadata, "Fanart" => config.EnableFanartMetadata, "MdbList" => config.EnableMdbListMetadata, _ => false };
    private static string Key(PluginConfiguration config, string provider) => provider switch { "Tmdb" => config.TmdbReadAccessToken, "Tvdb" => config.TvdbApiKey, "Fanart" => config.FanartApiKey, "MdbList" => config.MdbListApiKey, _ => "" };
    private static bool ValidImageBase(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "image.tmdb.org" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.UserInfo);
    public void Dispose() { _tokenGate.Dispose(); foreach (var gate in _gates.Values) gate.Mutex.Dispose(); }
    private sealed class RequestGate
    {
        public SemaphoreSlim Mutex { get; } = new(1, 1);
        public DateTimeOffset NextRequest { get; set; }
        public DateTimeOffset BlockedUntil { get; set; }
        public string BlockCode { get; set; } = "ProviderUnavailable";
        public string? Credential { get; set; }
        public int Failures { get; set; }
    }
    private sealed record RequestScope(PluginConfiguration Config, string Policy, bool Force, SyncDiagnostics? Diagnostics);
    private sealed class ScopeLease(Action restore) : IDisposable { public void Dispose() => restore(); }
    internal sealed class ProviderFailure(string code) : Exception(code) { public string Code { get; } = code; }
}
