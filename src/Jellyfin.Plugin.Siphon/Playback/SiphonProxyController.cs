using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
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
    ISafeHttpClient http,
    ConfigurationAccessor configuration) : ControllerBase
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
    [HttpGet("image/{token}")]
    public Task Image(string token, [FromQuery] string? type = null) => ExecuteImageAsync(token, type);


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
    private async Task ExecuteImageAsync(string token, string? type)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.Current.AddonTimeoutSeconds, 1, 120)));
        var ct = deadline.Token;
        if (!AllowRequest() || !GlobalSlots.Wait(0))
        {
            Reject();
            return;
        }

        try
        {
            if (token.Length > 4096 || type is { Length: > 32 } || !tokens.TryReadItem(token, out var key) || state.FindByKey(key) is not { } item)
            {
                Response.StatusCode = 404;
                return;
            }

            var variant = type?.ToLowerInvariant();
            var image = variant switch
            {
                null or "" or "poster" => item.PosterUrl,
                "season" => item.SeasonPosterUrl ?? item.PosterUrl,
                "primary" => item.Type == "series" ? item.ThumbnailUrl : item.PosterUrl,
                "thumbnail" => item.ThumbnailUrl,
                "backdrop" => item.BackdropUrl,
                "logo" => item.LogoUrl,
                "episode-backdrop" => item.EpisodeBackdropUrl,
                "episode-logo" => item.EpisodeLogoUrl,
                { } value when value.StartsWith("person-", StringComparison.Ordinal) => CreditPhoto(item.People, value.AsSpan(7)),
                { } value when value.StartsWith("episode-person-", StringComparison.Ordinal) => CreditPhoto(item.EpisodePeople, value.AsSpan(15)),
                _ => null
            };
            if (!Uri.TryCreate(image, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                Response.StatusCode = 404;
                return;
            }

            using var upstream = await http.SendAsync(uri, HttpMethod.Get, null, ct).ConfigureAwait(false);
            if (!upstream.IsSuccessStatusCode || upstream.Content.Headers.ContentLength is > 8 * 1024 * 1024)
            {
                Response.StatusCode = upstream.StatusCode == HttpStatusCode.NotFound ? 404 : 502;
                return;
            }

            var mediaType = upstream.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (mediaType is not ("image/jpeg" or "image/png" or "image/webp" or "image/gif"))
            {
                Response.StatusCode = 502;
                return;
            }

            await using var body = await upstream.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            while (true)
            {
                var count = await body.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
                if (count == 0) break;
                if (buffer.Length + count > 8 * 1024 * 1024)
                {
                    Response.StatusCode = 502;
                    return;
                }

                await buffer.WriteAsync(chunk.AsMemory(0, count), ct).ConfigureAwait(false);
            }

            Response.StatusCode = 200;
            Response.ContentType = mediaType;
            Response.ContentLength = buffer.Length;
            ProtectBytes();
            await Response.Body.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            HttpContext.Abort();
        }
        catch
        {
            if (!Response.HasStarted) Response.StatusCode = 502;
        }
        finally
        {
            GlobalSlots.Release();
        }
    }

    private static string? CreditPhoto(ManagedPerson[] people, ReadOnlySpan<char> index)
        => int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var position)
            && (uint)position < (uint)people.Length ? people[position].PhotoUrl : null;


    private async Task ProxyAsync(ProxySession session, CancellationToken ct)
    {
        using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
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
        if (upstream.StatusCode == HttpStatusCode.PartialContent
            && upstream.Content.Headers.ContentRange is not { Unit: "bytes", From: not null, To: not null })
        {
            throw new InvalidDataException("Invalid upstream byte range.");
        }

        var finalUrl = upstream.RequestMessage?.RequestUri ?? session.Source.Url;
        // An upstream extension is not proof of binary content. Classify nonzero ranges
        // from byte zero so a mislabeled playlist cannot expose unrewritten resource URLs.
        if (upstream.StatusCode == HttpStatusCode.PartialContent && upstream.Content.Headers.ContentRange?.From > 0)
        {
            var classificationHeaders = new Dictionary<string, string>(session.Source.RequestHeaders, StringComparer.OrdinalIgnoreCase)
            {
                ["Range"] = "bytes=0-511",
                ["Accept-Encoding"] = "identity"
            };
            classificationHeaders.Remove("If-Range");
            using var classification = await http.SendAsync(session.Source.Url, HttpMethod.Get, classificationHeaders, ct).ConfigureAwait(false);
            if (classification.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent)
                || (classification.StatusCode == HttpStatusCode.PartialContent
                    && classification.Content.Headers.ContentRange is not { Unit: "bytes", From: 0, To: not null })
                || classification.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("Unable to classify upstream representation.");
            }
            await using var classificationBody = await classification.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var start = new byte[512];
            var length = await ReadPrefixAsync(classificationBody, start, readDeadline).ConfigureAwait(false);
            if (Encoding.UTF8.GetString(start, 0, length).TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                classificationHeaders.Remove("Range");
                using var full = await http.SendAsync(session.Source.Url, HttpMethod.Get, classificationHeaders, ct).ConfigureAwait(false);
                if (full.StatusCode != HttpStatusCode.OK
                    || full.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException("Invalid HLS response.");
                }

                await using var fullBody = await full.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await WritePlaylistAsync(session, fullBody, ReadOnlyMemory<byte>.Empty, full.RequestMessage?.RequestUri ?? finalUrl, readDeadline, ct).ConfigureAwait(false);
                return;
            }
        }
        var mediaType = upstream.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        await using var body = await upstream.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var prefix = new byte[512];
        var prefixLength = await ReadPrefixAsync(body, prefix, readDeadline).ConfigureAwait(false);

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
                await WritePlaylistAsync(session, completeBody, ReadOnlyMemory<byte>.Empty, full.RequestMessage?.RequestUri ?? finalUrl, readDeadline, ct).ConfigureAwait(false);
            }
            else
            {
                await WritePlaylistAsync(session, body, prefix.AsMemory(0, prefixLength), finalUrl, readDeadline, ct).ConfigureAwait(false);
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
            var chunk = new byte[64 * 1024];
            int count;
            while ((count = await ReadUpstreamAsync(body, chunk, readDeadline).ConfigureAwait(false)) != 0)
            {
                await Response.Body.WriteAsync(chunk.AsMemory(0, count), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task WritePlaylistAsync(ProxySession session, Stream body, ReadOnlyMemory<byte> prefix, Uri finalUrl, CancellationTokenSource readDeadline, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await buffer.WriteAsync(prefix, ct).ConfigureAwait(false);
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var count = await ReadUpstreamAsync(body, chunk, readDeadline).ConfigureAwait(false);
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

    private async ValueTask<int> ReadPrefixAsync(Stream body, Memory<byte> buffer, CancellationTokenSource deadline)
    {
        var length = 0;
        while (length < buffer.Length)
        {
            var count = await ReadUpstreamAsync(body, buffer[length..], deadline).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        return length;
    }

    private async ValueTask<int> ReadUpstreamAsync(Stream body, Memory<byte> buffer, CancellationTokenSource deadline)
    {
        // Only upstream inactivity expires the lease's request: an entire film may run
        // for hours, and downstream backpressure must not consume its read deadline.
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.Current.AddonTimeoutSeconds, 1, 120)));
        try
        {
            return await body.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

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
