using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;

namespace Jellyfin.Plugin.Siphon.MediaSegments;

/// <summary>Public read-only IntroDB access, with a bounded cache and one global request lane.</summary>
public sealed class IntroDbClient(ISafeHttpClient http, ConfigurationAccessor configuration) : IDisposable
{
    private const int MaximumEntries = 2048;
    private const int MaximumBodyBytes = 64 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<IntroDbIdentity, IntroDbRead> _cache = [];
    private readonly SemaphoreSlim _request = new(1, 1);
    private DateTimeOffset _nextRequestUtc;
    private int _waiting;

    internal async Task<IntroDbRead> ReadAsync(IntroDbIdentity identity, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_cache.TryGetValue(identity, out var cached) && cached.ExpiresUtc > now) return cached;
            if (_nextRequestUtc > now.AddSeconds(5)) return Unavailable("RateLimited", now, _nextRequestUtc);
        }
        if (Interlocked.Increment(ref _waiting) > 64)
        {
            Interlocked.Decrement(ref _waiting);
            return Unavailable("Busy", now, now.AddSeconds(5));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var acquired = false;
        IntroDbRead result;
        try
        {
            await _request.WaitAsync(timeout.Token).ConfigureAwait(false);
            acquired = true;
            TimeSpan delay;
            lock (_gate)
            {
                now = DateTimeOffset.UtcNow;
                if (_cache.TryGetValue(identity, out var cached) && cached.ExpiresUtc > now) return cached;
                if (_nextRequestUtc > now.AddSeconds(5)) return Unavailable("RateLimited", now, _nextRequestUtc);
                delay = _nextRequestUtc - now;
            }
            if (delay > TimeSpan.Zero) await Task.Delay(delay, timeout.Token).ConfigureAwait(false);
            lock (_gate) _nextRequestUtc = DateTimeOffset.UtcNow.AddSeconds(1);
            using var response = await http.SendAsync(identity.RequestUri, HttpMethod.Get, null, timeout.Token).ConfigureAwait(false);
            now = DateTimeOffset.UtcNow;
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var retry = response.Headers.RetryAfter;
                var wait = retry?.Delta ?? (retry?.Date - now) ?? TimeSpan.FromMinutes(1);
                var next = now.AddSeconds(Math.Clamp(wait.TotalSeconds, 1, 3600));
                lock (_gate) _nextRequestUtc = next;
                result = Unavailable("RateLimited", now, next);
            }
            else if (response.StatusCode == HttpStatusCode.NotFound)
                result = Unavailable("NotFound", now, now.AddMinutes(5));
            else if (!response.IsSuccessStatusCode)
                result = Unavailable("Unavailable", now, now.AddMinutes(1));
            else
            {
                if (response.Content.Headers.ContentLength is > MaximumBodyBytes) throw new JsonException("Segment response is too large.");
                using var output = new MemoryStream();
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                var buffer = new byte[4096];
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    if (output.Length + count > MaximumBodyBytes) throw new JsonException("Segment response is too large.");
                    output.Write(buffer, 0, count);
                }
                using var document = JsonDocument.Parse(output.GetBuffer().AsMemory(0, (int)output.Length), new JsonDocumentOptions { MaxDepth = 16 });
                var data = IntroDbSegments.Parse(document.RootElement, identity);
                var found = data.Intro is not null || data.Recap is not null || data.Outro is not null || data.PostCredits is not null;
                result = new(data, found ? "Available" : "NotFound", now,
                    found ? now.AddHours(Math.Clamp(configuration.Current.IntroDbCacheHours, 1, 168)) : now.AddMinutes(5));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            now = DateTimeOffset.UtcNow;
            result = Unavailable("TimedOut", now, now.AddMinutes(1));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            now = DateTimeOffset.UtcNow;
            result = Unavailable("Unavailable", now, now.AddMinutes(1));
        }
        finally
        {
            if (acquired) _request.Release();
            Interlocked.Decrement(ref _waiting);
        }
        lock (_gate)
        {
            // Do not let a timed-out queued waiter replace a successful response.
            if (_cache.TryGetValue(identity, out var existing) && existing.ExpiresUtc > now && existing.Data is not null)
                return existing;
            if (_cache.Count >= MaximumEntries && !_cache.ContainsKey(identity))
            {
                var oldest = _cache.MinBy(entry => entry.Value.ExpiresUtc).Key;
                _cache.Remove(oldest);
            }
            _cache[identity] = result;
        }
        return result;
    }

    internal IntroDbRead? Peek(IntroDbIdentity identity)
    {
        lock (_gate) return _cache.TryGetValue(identity, out var cached) && cached.ExpiresUtc > DateTimeOffset.UtcNow ? cached : null;
    }

    internal void Forget(IntroDbIdentity identity)
    {
        lock (_gate) _cache.Remove(identity);
    }

    private static IntroDbRead Unavailable(string status, DateTimeOffset now, DateTimeOffset expires) => new(null, status, now, expires);
    public void Dispose() => _request.Dispose();
}
