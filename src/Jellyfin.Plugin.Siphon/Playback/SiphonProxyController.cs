using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Siphon.Playback;

[ApiController]
[AllowAnonymous]
[Route("Siphon")]
public sealed class SiphonProxyController(
    CapabilityTokenService tokens,
    ISiphonStateStore state,
    StreamResolver resolver,
    ProxySessionStore sessions,
    ISafeHttpClient http) : ControllerBase
{
    private static readonly SemaphoreSlim GlobalSlots = new(64, 64);
    private static readonly SemaphoreSlim ResolutionGate = new(4, 4);
    private static readonly object RateGate = new();
    private static long _rateWindow;
    private static int _rateCount;

    [HttpGet("s/{token}")]
    [HttpHead("s/{token}")]
    public Task Item(string token) => ExecuteAsync(token, 1);

    [HttpGet("media/{token}/stream{extension}")]
    [HttpHead("media/{token}/stream{extension}")]
    public Task Media(string token) => ExecuteAsync(token, 0);

    [HttpGet("source/{token}")]
    [HttpHead("source/{token}")]
    public Task Source(string token) => ExecuteAsync(token, 2);

    private async Task ExecuteAsync(string token, int tokenKind)
    {
        var ct = HttpContext.RequestAborted;
        if (!AllowRequest() || !GlobalSlots.Wait(0))
        {
            Reject();
            return;
        }

        ProxySession? session = null;
        var acquired = false;
        try
        {
            if (token.Length > 4096)
            {
                Response.StatusCode = 404;
                return;
            }

            if (tokenKind != 0)
            {
                string key;
                string? sourceId = null;
                var valid = tokenKind == 2
                    ? tokens.TryReadSource(token, out key, out sourceId)
                    : tokens.TryReadItem(token, out key);
                if (!valid || state.FindByKey(key) is not { } item)
                {
                    Response.StatusCode = 404;
                    return;
                }

                var selectionKey = sourceId is null ? key : key + "|" + sourceId;

                session = sessions.GetDefault(selectionKey);
                if (session is null)
                {
                    if (!await ResolutionGate.WaitAsync(0, ct).ConfigureAwait(false))
                    {
                        Reject();
                        return;
                    }

                    try
                    {
                        session = sessions.GetDefault(selectionKey);
                        if (session is null)
                        {
                            var sources = await resolver.GetSourcesAsync(item, ct).ConfigureAwait(false);
                            var selected = sourceId is null ? sources.FirstOrDefault() : sources.FirstOrDefault(source => source.Id == sourceId);
                            if (selected is null)
                            {
                                Response.StatusCode = 404;
                                return;
                            }

                            session = sessions.Create(item, selected);
                            sessions.SetDefault(session, selectionKey);
                        }
                    }
                    finally
                    {
                        ResolutionGate.Release();
                    }
                }
            }
            else
            {
                session = sessions.Get(token);
            }

            if (session is null || state.FindByKey(session.ItemKey) is null)
            {
                Response.StatusCode = 404;
                return;
            }

            if (!sessions.TryAcquire(session))
            {
                Reject();
                return;
            }

            acquired = true;
            await ProxyAsync(session, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            HttpContext.Abort();
        }
        catch (Exception)
        {
            if (Response.HasStarted)
            {
                HttpContext.Abort();
            }
            else
            {
                Response.Clear();
                Response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }
        finally
        {
            if (acquired && session is not null)
            {
                sessions.Complete(session);
            }

            GlobalSlots.Release();
        }
    }

    private async Task ProxyAsync(ProxySession session, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(session.Source.RequestHeaders, StringComparer.OrdinalIgnoreCase);
        headers.Remove("Range");
        headers.Remove("If-Range");
        headers["Accept-Encoding"] = "identity";
        var range = Request.Headers.Range.ToString();
        if (range.Length != 0)
        {
            if (range.Length > 256 || !RangeHeaderValue.TryParse(range, out var parsed) || parsed.Unit != "bytes" || parsed.Ranges.Count != 1)
            {
                Response.StatusCode = 416;
                return;
            }

            headers["Range"] = parsed.ToString();
            var ifRange = Request.Headers.IfRange.ToString();
            if (ifRange.Length <= 1024 && RangeConditionHeaderValue.TryParse(ifRange, out var condition))
            {
                headers["If-Range"] = condition.ToString();
            }
        }

        // GET is intentional for HEAD: determining rewritten HLS length requires its representation.
        using var upstream = await http.SendAsync(session.Source.Url, HttpMethod.Get, headers, ct).ConfigureAwait(false);
        if (upstream.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            Response.StatusCode = 416;
            if (upstream.Content.Headers.ContentRange is { } unsatisfied)
            {
                Response.Headers.ContentRange = unsatisfied.ToString();
            }

            return;
        }

        if (upstream.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
        {
            Response.StatusCode = upstream.StatusCode == HttpStatusCode.NotFound ? 404 : 502;
            return;
        }

        if (upstream.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Unexpected content encoding.");
        }

        var finalUrl = upstream.RequestMessage?.RequestUri ?? session.Source.Url;
        // A ranged extensionless playlist may not include #EXTM3U. Classify from byte zero before forwarding it.
        if (upstream.StatusCode == HttpStatusCode.PartialContent && upstream.Content.Headers.ContentRange?.From > 0
            && !IsKnownBinary(finalUrl.AbsolutePath))
        {
            var classificationHeaders = new Dictionary<string, string>(session.Source.RequestHeaders, StringComparer.OrdinalIgnoreCase)
            {
                ["Range"] = "bytes=0-511",
                ["Accept-Encoding"] = "identity"
            };
            using var classification = await http.SendAsync(session.Source.Url, HttpMethod.Get, classificationHeaders, ct).ConfigureAwait(false);
            await using var classificationBody = await classification.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var start = new byte[512];
            var length = await classificationBody.ReadAtLeastAsync(start, start.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
            if (Encoding.UTF8.GetString(start, 0, length).TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                classificationHeaders.Remove("Range");
                using var full = await http.SendAsync(session.Source.Url, HttpMethod.Get, classificationHeaders, ct).ConfigureAwait(false);
                if (full.StatusCode != HttpStatusCode.OK)
                {
                    throw new InvalidDataException("Invalid HLS response.");
                }

                await using var fullBody = await full.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await WritePlaylistAsync(session, fullBody, ReadOnlyMemory<byte>.Empty, full.RequestMessage?.RequestUri ?? finalUrl, ct).ConfigureAwait(false);
                return;
            }
        }
        var mediaType = upstream.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        await using var body = await upstream.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var prefix = new byte[512];
        var prefixLength = 0;
        while (prefixLength < prefix.Length)
        {
            var read = await body.ReadAsync(prefix.AsMemory(prefixLength), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            prefixLength += read;
        }

        var hls = finalUrl.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/x-mpegurl", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("audio/mpegurl", StringComparison.OrdinalIgnoreCase)
            || Encoding.UTF8.GetString(prefix, 0, prefixLength).TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("#EXTM3U", StringComparison.Ordinal);

        if (hls)
        {
            if (upstream.StatusCode == HttpStatusCode.PartialContent)
            {
                // A ranged playlist is not a complete parseable representation.
                headers.Remove("Range");
                headers.Remove("If-Range");
                using var full = await http.SendAsync(session.Source.Url, HttpMethod.Get, headers, ct).ConfigureAwait(false);
                if (full.StatusCode != HttpStatusCode.OK || full.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException("Invalid HLS response.");
                }

                await using var completeBody = await full.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await WritePlaylistAsync(session, completeBody, ReadOnlyMemory<byte>.Empty, full.RequestMessage?.RequestUri ?? finalUrl, ct).ConfigureAwait(false);
            }
            else
            {
                await WritePlaylistAsync(session, body, prefix.AsMemory(0, prefixLength), finalUrl, ct).ConfigureAwait(false);
            }

            return;
        }

        Response.StatusCode = (int)upstream.StatusCode;
        Response.ContentType = mediaType;
        Response.ContentLength = upstream.Content.Headers.ContentLength;
        ProtectBytes();
        if (upstream.Content.Headers.ContentRange is { } contentRange)
        {
            Response.Headers.ContentRange = contentRange.ToString();
        }

        if (upstream.Headers.AcceptRanges.Contains("bytes"))
        {
            Response.Headers.AcceptRanges = "bytes";
        }

        if (!HttpMethods.IsHead(Request.Method))
        {
            await Response.Body.WriteAsync(prefix.AsMemory(0, prefixLength), ct).ConfigureAwait(false);
            await body.CopyToAsync(Response.Body, 64 * 1024, ct).ConfigureAwait(false);
        }
    }

    private async Task WritePlaylistAsync(ProxySession session, Stream body, ReadOnlyMemory<byte> prefix, Uri finalUrl, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await buffer.WriteAsync(prefix, ct).ConfigureAwait(false);
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var count = await body.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            if (buffer.Length + count > HlsPlaylistRewriter.MaximumPlaylistBytes)
            {
                throw new InvalidDataException("Playlist exceeds size limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, count), ct).ConfigureAwait(false);
        }

        var text = new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        // Validate all URI syntax before creating any child leases; malformed playlists leave no partial cache.
        _ = HlsPlaylistRewriter.Rewrite(text, finalUrl, _ => "validated");
        var rewritten = HlsPlaylistRewriter.Rewrite(text, finalUrl, uri => sessions.GetUrl(sessions.CreateChild(session, uri)));
        var bytes = Encoding.UTF8.GetBytes(rewritten);
        Response.StatusCode = 200;
        Response.ContentType = "application/vnd.apple.mpegurl";
        Response.ContentLength = bytes.Length;
        ProtectBytes();
        if (!HttpMethods.IsHead(Request.Method))
        {
            await Response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
    }

    private static bool IsKnownBinary(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".ts" or ".m4s" or ".mp4" or ".m4a" or ".aac" or ".mp3" or ".webm" or ".mkv" or ".key";

    private void ProtectBytes()
    {
        Response.Headers.ContentEncoding = "identity";
        Response.Headers.CacheControl = "private, no-store, no-transform";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    private void Reject()
    {
        Response.StatusCode = 429;
        Response.Headers.RetryAfter = "2";
    }

    private static bool AllowRequest()
    {
        lock (RateGate)
        {
            var now = Environment.TickCount64 / 1000;
            if (_rateWindow != now)
            {
                _rateWindow = now;
                _rateCount = 0;
            }

            return ++_rateCount <= 128;
        }
    }
}
