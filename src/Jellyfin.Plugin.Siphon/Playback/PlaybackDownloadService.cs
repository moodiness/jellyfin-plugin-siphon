using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.P2p;
using Jellyfin.Plugin.Siphon.Protocol;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Siphon.Playback;

public sealed class PlaybackDownloadService(ISafeHttpClient http, P2pStreamService p2p, ConfigurationAccessor configuration)
{
    public async Task RelayAsync(HttpContext context, ResolvedStream source, CancellationToken ct)
    {
        try
        {
            using var admission = DirectTransferAdmission.Shared.TryAcquire(source.UserId, ct);
            if (admission is null)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = "2";
                return;
            }
            if (source.P2p is not null) { await RelayP2pCoreAsync(context, source, true, ct).ConfigureAwait(false); return; }
            if (DownloadUnavailableReason(source) is not null) { await RejectHls(context, ct).ConfigureAwait(false); return; }
            var headers = new Dictionary<string, string>(source.RequestHeaders, StringComparer.OrdinalIgnoreCase) { ["Accept-Encoding"] = "identity" };
            headers.Remove("Range");
            headers.Remove("If-Range");
            var range = context.Request.Headers.Range.ToString();
            if (range.Length != 0)
            {
                if (!TryRange(range, out var parsed)) { context.Response.StatusCode = 416; return; }
                headers["Range"] = parsed!.ToString();
                var ifRange = context.Request.Headers.IfRange.ToString();
                if (ifRange.Length <= 1024 && RangeConditionHeaderValue.TryParse(ifRange, out var condition)) headers["If-Range"] = condition.ToString();
            }
            using var upstream = await http.SendAsync(source.Url, HttpMethod.Get, headers, ct).ConfigureAwait(false);
            if (upstream.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                context.Response.StatusCode = 416;
                if (upstream.Content.Headers.ContentRange is { } unsatisfied) context.Response.Headers.ContentRange = unsatisfied.ToString();
                return;
            }
            if (upstream.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent)) { context.Response.StatusCode = upstream.StatusCode == HttpStatusCode.NotFound ? 404 : 502; return; }
            if (upstream.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException();
            if (upstream.StatusCode == HttpStatusCode.PartialContent && upstream.Content.Headers.ContentRange is not { Unit: "bytes", From: not null, To: not null }) throw new InvalidDataException();
            // Classify from byte zero even when resuming: playlists must never leak upstream URLs.
            var classificationHeaders = new Dictionary<string, string>(source.RequestHeaders, StringComparer.OrdinalIgnoreCase) { ["Range"] = "bytes=0-511", ["Accept-Encoding"] = "identity" };
            classificationHeaders.Remove("If-Range");
            using var classification = await http.SendAsync(source.Url, HttpMethod.Get, classificationHeaders, ct).ConfigureAwait(false);
            if (classification.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent)
                || (classification.StatusCode == HttpStatusCode.PartialContent && classification.Content.Headers.ContentRange is not { From: 0 })
                || classification.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException();
            await using var classificationBody = await classification.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var prefix = new byte[512];
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var prefixLength = 0;
            while (prefixLength < prefix.Length)
            {
                var count = await ReadUpstreamAsync(classificationBody, prefix.AsMemory(prefixLength), deadline, configuration.Current.AddonTimeoutSeconds).ConfigureAwait(false);
                if (count == 0) break;
                prefixLength += count;
            }
            var mediaType = upstream.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            if (Encoding.UTF8.GetString(prefix, 0, prefixLength).TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("#EXTM3U", StringComparison.Ordinal)
                || mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase))
            {
                await RejectHls(context, ct).ConfigureAwait(false);
                return;
            }
            context.Response.StatusCode = (int)upstream.StatusCode;
            SetHeaders(context, mediaType, upstream.Content.Headers.ContentLength, true, SafeExtension(source));
            if (upstream.Content.Headers.ContentRange is { } contentRange) context.Response.Headers.ContentRange = contentRange.ToString();
            if (upstream.Headers.AcceptRanges.Contains("bytes")) context.Response.Headers.AcceptRanges = "bytes";
            if (upstream.Headers.ETag is { } etag) context.Response.Headers.ETag = etag.ToString();
            if (upstream.Content.Headers.LastModified is { } modified) context.Response.Headers.LastModified = modified.ToString("R", CultureInfo.InvariantCulture);
            if (!HttpMethods.IsHead(context.Request.Method))
            {
                await using var content = await upstream.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var buffer = ArrayPool<byte>.Shared.Rent(65536);
                try
                {
                    int count;
                    while ((count = await ReadUpstreamAsync(content, buffer, deadline, configuration.Current.AddonTimeoutSeconds).ConfigureAwait(false)) != 0)
                        await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { context.Abort(); }
        catch { if (context.Response.HasStarted) context.Abort(); else { context.Response.Clear(); context.Response.StatusCode = 502; } }
    }

    internal static async ValueTask<int> ReadUpstreamAsync(Stream body, Memory<byte> buffer, CancellationTokenSource deadline, int timeoutSeconds)
    {
        // Only upstream inactivity expires a request; a film and downstream backpressure
        // may last much longer than the configured per-read timeout.
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 120)));
        try { return await body.ReadAsync(buffer, deadline.Token).ConfigureAwait(false); }
        finally { deadline.CancelAfter(Timeout.InfiniteTimeSpan); }
    }

    public static string? DownloadUnavailableReason(ResolvedStream source)
        => source.P2p is null && (source.Url.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || source.Url.AbsolutePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)
            || source.FileName?.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) == true)
            ? "HLS needs offline preparation. Use Queue offline when server downloads are enabled." : null;

    private static async Task RejectHls(HttpContext context, CancellationToken ct)
    {
        context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
        if (!HttpMethods.IsHead(context.Request.Method))
            await context.Response.WriteAsJsonAsync(new { Code = "HlsRequiresOfflinePreparation", Message = "HLS needs offline preparation. Use the server download queue." }, ct).ConfigureAwait(false);
    }

    // Playback is already admitted by the proxy. Direct downloads enter RelayAsync once,
    // regardless of whether the selected source uses HTTP or P2P.
    internal Task RelayP2pPlaybackAsync(HttpContext context, ResolvedStream source, CancellationToken ct)
        => RelayP2pCoreAsync(context, source, false, ct);

    private async Task RelayP2pCoreAsync(HttpContext context, ResolvedStream source, bool attachment, CancellationToken ct)
    {
        await using var lease = await p2p.OpenAsync(source.P2p ?? throw new InvalidOperationException(), source.UserId, ct).ConfigureAwait(false);
        var length = lease.Length;
        var etag = "\"" + AddonRegistry.Digest(source.Id + ":" + length.ToString(CultureInfo.InvariantCulture)) + "\"";
        var range = context.Request.Headers.Range.ToString();
        var ifRange = context.Request.Headers.IfRange.ToString();
        long start = 0, end = length - 1;
        var partial = range.Length != 0 && (ifRange.Length == 0 || ifRange == etag);
        if (partial && !TryBounds(range, length, out start, out end))
        {
            context.Response.StatusCode = 416;
            context.Response.Headers.ContentRange = "bytes */" + length.ToString(CultureInfo.InvariantCulture);
            return;
        }
        if (!attachment)
        {
            // A torrent filename is not a container classification either. Direct byte
            // exports remain unchanged; native playback only receives supported roots.
            var prefix = new byte[(int)Math.Min(512, length)];
            lease.Content.Seek(0, SeekOrigin.Begin);
            var read = 0;
            while (read < prefix.Length)
            {
                var count = await lease.Content.ReadAsync(prefix.AsMemory(read), ct).ConfigureAwait(false);
                if (count == 0) break;
                read += count;
            }
            NativeMediaClassifier.RequireSupported(prefix.AsSpan(0, read));
        }
        context.Response.StatusCode = partial ? 206 : 200;
        SetHeaders(context, "application/octet-stream", partial ? end - start + 1 : length, attachment, SafeExtension(source with { FileName = lease.FileName }));
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.Headers.ETag = etag;
        if (partial) context.Response.Headers.ContentRange = $"bytes {start}-{end}/{length}";
        if (HttpMethods.IsHead(context.Request.Method)) return;
        lease.Content.Seek(start, SeekOrigin.Begin);
        var remaining = partial ? end - start + 1 : length;
        var buffer = new byte[65536];
        while (remaining > 0)
        {
            var count = await lease.Content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException();
            await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            remaining -= count;
        }
    }
    internal static bool TryBounds(string value, long length, out long start, out long end)
    {
        start = 0; end = length - 1;
        if (length <= 0 || !TryRange(value, out var parsed)) return false;
        var range = parsed!.Ranges.Single();
        if (range.From is { } from) { start = from; end = Math.Min(range.To ?? end, end); }
        else if (range.To is > 0) start = Math.Max(0, length - range.To.Value);
        else return false;
        return start < length && start <= end;
    }
    private static bool TryRange(string value, out RangeHeaderValue? parsed)
        => RangeHeaderValue.TryParse(value.Length <= 256 ? value : string.Empty, out parsed) && parsed.Unit == "bytes" && parsed.Ranges.Count == 1;
    private static string SafeExtension(ResolvedStream source)
    {
        var extension = Path.GetExtension(source.FileName ?? (source.P2p is null ? source.Url.AbsolutePath : string.Empty)).ToLowerInvariant();
        return extension is ".mp4" or ".mkv" or ".webm" or ".avi" or ".mov" or ".m4v" or ".ts" or ".m3u8" ? extension : ".bin";
    }
    private static void SetHeaders(HttpContext context, string mediaType, long? length, bool attachment, string extension)
    {
        context.Response.ContentType = mediaType;
        context.Response.ContentLength = length;
        context.Response.Headers.CacheControl = "private, no-store, no-transform";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (attachment) context.Response.Headers.ContentDisposition = "attachment; filename=\"Siphon" + extension + "\"";
    }
}
