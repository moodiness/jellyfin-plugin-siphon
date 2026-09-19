using System.Diagnostics;
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
using Microsoft.Extensions.Logging;

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
    ConfigurationAccessor configuration,
    PlaybackAccess access,
    PlaybackDownloadService downloads,
    ILogger<SiphonProxyController> logger) : ControllerBase
{
    private static readonly SemaphoreSlim GlobalSlots = new(64, 64);
    private static readonly SemaphoreSlim ResolutionGate = new(4, 4);
    private static readonly PlaybackRequestBudget InvalidRequests = new();
    private static readonly PlaybackRequestBudget PlaybackRequests = new();
    private static readonly PlaybackRequestBudget ImageRequests = new();
    private string _stage = "authorization";
    private int? _upstreamStatus;
    private long _started;

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

    [HttpGet("download/{token}")]
    [HttpHead("download/{token}")]
    public async Task Download(string token)
    {
        var session = sessions.Get(token);
        if (session is not { Download: true } || !access.AllowsLease(session, download: true))
        {
            InvalidReference();
            return;
        }
        await downloads.RelayAsync(HttpContext, session.Source, HttpContext.RequestAborted).ConfigureAwait(false);
    }


    private async Task ExecuteAsync(string token, int tokenKind)
    {
        var ct = HttpContext.RequestAborted;
        var admitted = false;
        ProxySession? session = null;
        var acquired = false;
        _stage = "authorization";
        _upstreamStatus = null;
        _started = Stopwatch.GetTimestamp();
        try
        {
            if (token.Length > 4096)
            {
                InvalidReference();
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
                    InvalidReference();
                    return;
                }

                var current = access.CurrentUser();
                var userId = sourceId is null ? current?.Id ?? Guid.Empty : access.SourceOwner(key, sourceId);
                if (userId == Guid.Empty || !access.CanPlay(userId) || (User.Identity?.IsAuthenticated == true && current is null) || (current is not null && current.Id != userId)
                    || access.FindVideo(key, userId, sourceId) is null)
                {
                    InvalidReference();
                    return;
                }
                if (!TryAdmit(PlaybackRequests, userId.ToString("N"))) return;
                admitted = true;
                var selectionKey = userId.ToString("N") + "|" + (sourceId is null ? key : key + "|" + sourceId);

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
                            _stage = "source-resolution";
                            var sources = await resolver.GetSourcesAsync(item, userId, ct).ConfigureAwait(false);
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

            if (session is null || session.Download || state.FindByKey(session.ItemKey) is null || !access.AllowsLease(session))
            {
                InvalidReference();
                return;
            }
            if (!admitted)
            {
                if (!TryAdmit(PlaybackRequests, session.Source.UserId.ToString("N"))) return;
                admitted = true;
            }

            if (!sessions.TryAcquire(session))
            {
                Reject();
                return;
            }

            acquired = true;
            await ProxyAsync(session, tokenKind == 0 ? "../" : "../media/", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            HttpContext.Abort();
        }
        catch (Exception error)
        {
            LogFailure(error switch
            {
                OperationCanceledException => "timeout",
                HttpRequestException => "transport",
                InvalidDataException => "invalid-media",
                IOException => "io",
                _ => "internal"
            });
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

            if (admitted) GlobalSlots.Release();
        }
    }

    private void LogFailure(string cause)
    {
        // Only bounded, locally chosen fields: exception text and request/source URLs
        // can contain credentials. Do not pass an exception object to the logger.
        logger.LogWarning("Siphon relay failed: stage={Stage}, cause={Cause}, upstreamStatus={UpstreamStatus}, responseStarted={ResponseStarted}, elapsedMs={ElapsedMs}",
            _stage, cause, _upstreamStatus, Response.HasStarted, (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds);
    }
    private async Task ExecuteImageAsync(string token, string? type)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.Current.AddonTimeoutSeconds, 1, 120)));
        var ct = deadline.Token;
        var admitted = false;

        try
        {
            if (token.Length > 4096 || type is { Length: > 32 } || !tokens.TryReadItem(token, out var key)
                || (state.ReadByKey(key) ?? state.ReadByContentKey(key)) is not { } item)
            {
                InvalidReference();
                return;
            }

            var variant = type?.ToLowerInvariant();
            var image = variant switch
            {
                null or "" or "poster" => item.PosterUrl,
                "season" => item.SeasonPosterUrl,
                "primary" => item.Type == "series" && key != item.ContentKey ? item.ThumbnailUrl : item.PosterUrl,
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
                InvalidReference();
                return;
            }
            if (!TryAdmit(ImageRequests, PlaybackRequestBudget.Client(HttpContext))) return;
            admitted = true;

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
            if (admitted) GlobalSlots.Release();
        }
    }

    private static string? CreditPhoto(IReadOnlyList<ManagedPerson> people, ReadOnlySpan<char> index)
        => int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var position)
            && (uint)position < (uint)people.Count ? people[position].PhotoUrl : null;


    private async Task ProxyAsync(ProxySession session, string childPrefix, CancellationToken ct)
    {
        if (session.Source.P2p is not null)
        {
            _stage = "p2p-media";
            await downloads.RelayP2pPlaybackAsync(HttpContext, session.Source, ct).ConfigureAwait(false);
            return;
        }
        using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var headers = new Dictionary<string, string>(session.Source.RequestHeaders, StringComparer.OrdinalIgnoreCase);
        headers.Remove("Range");
        headers.Remove("If-Range");
        headers["Accept-Encoding"] = "identity";
        var range = Request.Headers.Range.ToString();
        RangeItemHeaderValue? requestedRange = null;
        if (range.Length != 0)
        {
            if (range.Length > 256 || !RangeHeaderValue.TryParse(range, out var parsed) || parsed.Unit != "bytes" || parsed.Ranges.Count != 1)
            {
                Response.StatusCode = 416;
                return;
            }

            requestedRange = parsed.Ranges.Single();
            headers["Range"] = parsed.ToString();
            var ifRange = Request.Headers.IfRange.ToString();
            if (ifRange.Length <= 1024 && RangeConditionHeaderValue.TryParse(ifRange, out var condition))
            {
                headers["If-Range"] = condition.ToString();
            }
        }

        var rootMedia = session.RootToken == session.Token;
        var nativeMedia = rootMedia ? sessions.GetNativeMedia(session) : null;
        var hasNativeMediaClassification = nativeMedia is not null;
        var classified = nativeMedia is not null;
        EntityTagHeaderValue? classifiedTag = null;
        if (nativeMedia?.ETag is { } cachedEtag && EntityTagHeaderValue.TryParse(cachedEtag, out var parsedTag))
            classifiedTag = parsedTag;
        DateTimeOffset? classifiedDate = nativeMedia?.LastModified;
        long? classifiedLength = nativeMedia?.Length;
        // Long byte-zero responses classify themselves. For seeks and short ranges,
        // finish and dispose the prefix response before opening the requested body.
        if (requestedRange is not null && !hasNativeMediaClassification
            && (requestedRange.From != 0 || requestedRange.To < 511))
        {
            var classificationHeaders = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
            {
                ["Range"] = "bytes=0-511"
            };
            classificationHeaders.Remove("If-Range");
            _stage = "classification-headers";
            _upstreamStatus = null;
            using (var classification = await http.SendAsync(session.Source.Url, HttpMethod.Get, classificationHeaders, ct).ConfigureAwait(false))
            {
                _upstreamStatus = (int)classification.StatusCode;
                if (classification.StatusCode == HttpStatusCode.NotFound)
                {
                    Response.StatusCode = 404;
                    return;
                }
                if (classification.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
                {
                    LogFailure("upstream-status");
                    Response.StatusCode = 502;
                    return;
                }
                _stage = "classification-validation";
                if ((classification.StatusCode == HttpStatusCode.PartialContent
                        && classification.Content.Headers.ContentRange is not { Unit: "bytes", From: 0, To: not null })
                    || classification.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException("Unable to classify upstream representation.");
                }
                _stage = "classification-body";
                await using var classificationBody = await classification.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var start = new byte[512];
                var length = await ReadPrefixAsync(classificationBody, start, readDeadline).ConfigureAwait(false);
                _stage = "classification-validation";
                if (Encoding.UTF8.GetString(start, 0, length).TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("#EXTM3U", StringComparison.Ordinal))
                {
                    headers.Remove("Range");
                    headers.Remove("If-Range");
                }
                else
                {
                    if (rootMedia) NativeMediaClassifier.RequireSupported(start.AsSpan(0, length));
                    classified = true;
                    classifiedTag = classification.Headers.ETag;
                    classifiedDate = classification.Content.Headers.LastModified;
                    classifiedLength = classification.Content.Headers.ContentRange?.Length
                        ?? (classification.StatusCode == HttpStatusCode.OK ? classification.Content.Headers.ContentLength : null);
                    if (!headers.ContainsKey("If-Range") && classification.Headers.ETag is { IsWeak: false } tag && tag.Tag != "*")
                        headers["If-Range"] = tag.ToString();
                }
            }
        }


        _stage = "media-headers";
        _upstreamStatus = null;
        using var upstream = await http.SendAsync(session.Source.Url, HttpMethod.Get, headers, ct).ConfigureAwait(false);
        _upstreamStatus = (int)upstream.StatusCode;
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
            if (Response.StatusCode == 502) LogFailure("upstream-status");
            return;
        }

        _stage = "media-validation";
        if (upstream.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Unexpected content encoding.");
        }
        if (upstream.StatusCode == HttpStatusCode.PartialContent
            && (upstream.Content.Headers.ContentRange is not { Unit: "bytes", From: not null, To: not null } receivedRange
                || !headers.TryGetValue("Range", out var sentRange)
                || (receivedRange.Length is { } total
                    && (!PlaybackDownloadService.TryBounds(sentRange, total, out var from, out var to)
                        || receivedRange.From != from || receivedRange.To > to))
                || (upstream.Content.Headers.ContentLength is { } bodyLength && bodyLength != receivedRange.To - receivedRange.From + 1)))
        {
            throw new InvalidDataException("Invalid upstream byte range.");
        }

        var finalUrl = upstream.RequestMessage?.RequestUri ?? session.Source.Url;
        var partial = upstream.StatusCode == HttpStatusCode.PartialContent;
        if (partial && classified
            && ((classifiedTag is not null && upstream.Headers.ETag is { } currentTag && !classifiedTag.Equals(currentTag))
                || (classifiedDate is not null && upstream.Content.Headers.LastModified is { } currentDate && classifiedDate != currentDate)
                || (classifiedLength is not null && upstream.Content.Headers.ContentRange?.Length is { } currentLength && classifiedLength != currentLength)))
        {
            throw new InvalidDataException("The classified representation changed.");
        }
        if (partial && !classified && upstream.Content.Headers.ContentRange?.From != 0)
            throw new InvalidDataException("The response does not include a classified prefix.");
        var mediaType = upstream.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        _stage = "media-prefix";
        await using var body = await upstream.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var prefix = new byte[512];
        var prefixLength = await ReadPrefixAsync(body, prefix, readDeadline).ConfigureAwait(false);

        _stage = "media-validation";
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
                await body.DisposeAsync().ConfigureAwait(false);
                upstream.Dispose();
                headers.Remove("Range");
                headers.Remove("If-Range");
                _stage = "playlist-headers";
                _upstreamStatus = null;
                using var full = await http.SendAsync(session.Source.Url, HttpMethod.Get, headers, ct).ConfigureAwait(false);
                _upstreamStatus = (int)full.StatusCode;
                if (full.StatusCode != HttpStatusCode.OK || full.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException("Invalid HLS response.");
                }

                _stage = "playlist-body";
                await using var completeBody = await full.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await WritePlaylistAsync(session, completeBody, ReadOnlyMemory<byte>.Empty, full.RequestMessage?.RequestUri ?? finalUrl, childPrefix, readDeadline, ct).ConfigureAwait(false);
            }
            else
            {
                _stage = "playlist-body";
                await WritePlaylistAsync(session, body, prefix.AsMemory(0, prefixLength), finalUrl, childPrefix, readDeadline, ct).ConfigureAwait(false);
            }

            return;
        }

        if (rootMedia && !hasNativeMediaClassification)
        {
            if (!partial || !classified)
                NativeMediaClassifier.RequireSupported(prefix.AsSpan(0, prefixLength));
            sessions.MarkNativeMedia(session,
                classifiedTag?.ToString() ?? upstream.Headers.ETag?.ToString(),
                classifiedDate ?? upstream.Content.Headers.LastModified,
                classifiedLength ?? upstream.Content.Headers.ContentRange?.Length ?? upstream.Content.Headers.ContentLength);
        }
        Response.StatusCode = (int)upstream.StatusCode;
        Response.ContentType = GetMediaContentType(mediaType);
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
            _stage = "client-write";
            await Response.Body.WriteAsync(prefix.AsMemory(0, prefixLength), ct).ConfigureAwait(false);
            var chunk = new byte[64 * 1024];
            while (true)
            {
                _stage = "media-body";
                var count = await PlaybackDownloadService.ReadUpstreamAsync(body, chunk, readDeadline, configuration.Current.AddonTimeoutSeconds).ConfigureAwait(false);
                if (count == 0) break;
                _stage = "client-write";
                await Response.Body.WriteAsync(chunk.AsMemory(0, count), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task WritePlaylistAsync(ProxySession session, Stream body, ReadOnlyMemory<byte> prefix, Uri finalUrl, string childPrefix, CancellationTokenSource readDeadline, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await buffer.WriteAsync(prefix, ct).ConfigureAwait(false);
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var count = await PlaybackDownloadService.ReadUpstreamAsync(body, chunk, readDeadline, configuration.Current.AddonTimeoutSeconds).ConfigureAwait(false);
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
        _stage = "playlist-validation";
        _ = HlsPlaylistRewriter.Rewrite(text, finalUrl, _ => "validated");
        // Relative capabilities stay on the reader's origin and base path. FFmpeg's
        // local playlists must not send segment/key requests back through the public proxy.
        var rewritten = HlsPlaylistRewriter.Rewrite(text, finalUrl,
            uri => childPrefix + ProxySessionStore.GetMediaPath(sessions.CreateChild(session, uri)));
        var bytes = Encoding.UTF8.GetBytes(rewritten);
        Response.StatusCode = 200;
        Response.ContentType = "application/vnd.apple.mpegurl";
        Response.ContentLength = bytes.Length;
        ProtectBytes();
        if (!HttpMethods.IsHead(Request.Method))
        {
            _stage = "client-write";
            await Response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask<int> ReadPrefixAsync(Stream body, Memory<byte> buffer, CancellationTokenSource deadline)
    {
        var length = 0;
        while (length < buffer.Length)
        {
            var count = await PlaybackDownloadService.ReadUpstreamAsync(body, buffer[length..], deadline, configuration.Current.AddonTimeoutSeconds).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        return length;
    }

    private static string GetMediaContentType(string mediaType)
    {
        if (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("text/xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The source returned a document instead of a supported media response.");
        }

        return mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/mp4", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/ogg", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("text/vtt", StringComparison.OrdinalIgnoreCase)
                ? mediaType : "application/octet-stream";
    }


    private void ProtectBytes()
    {
        Response.Headers.ContentEncoding = "identity";
        Response.Headers.CacheControl = "private, no-store, no-transform";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'; base-uri 'none'; frame-ancestors 'none'";
    }

    private void Reject()
    {
        Response.StatusCode = 429;
        Response.Headers.RetryAfter = "2";
    }

    private void InvalidReference()
    {
        if (InvalidRequests.Allow(PlaybackRequestBudget.Client(HttpContext))) Response.StatusCode = 404;
        else Reject();
    }

    private bool TryAdmit(PlaybackRequestBudget budget, string client)
    {
        if (!GlobalSlots.Wait(0))
        {
            Reject();
            return false;
        }

        if (budget.Allow(client)) return true;
        GlobalSlots.Release();
        Reject();
        return false;
    }
}
