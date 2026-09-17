using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.P2p;
using Jellyfin.Plugin.Siphon.Playback;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.Siphon.Downloads;

/// <summary>Transfers the exact resolved source into a bounded private workspace.</summary>
public sealed class DownloadTransferService(ISafeHttpClient http, P2pStreamService peers, IMediaEncoder encoder) : IDownloadTransfer
{
    public async Task<DownloadTransferResult> TransferAsync(ResolvedStream source, string directory, long maximumBytes,
        Func<DownloadTransferProgress, Task> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var workspace = new DownloadTransferWorkspace(directory, maximumBytes, progress);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromHours(24));
        try
        {
            return source.P2p is not null
                ? await TransferPeerAsync(source, workspace, lifetime.Token).ConfigureAwait(false)
                : await TransferHttpAsync(source, workspace, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The download exceeded its time limit.");
        }
    }

    private async Task<DownloadTransferResult> TransferPeerAsync(ResolvedStream source, DownloadTransferWorkspace workspace, CancellationToken cancellationToken)
    {
        await using var lease = await peers.OpenAsync(source.P2p!, source.UserId, cancellationToken).ConfigureAwait(false);
        var fingerprint = DownloadTransferWorkspace.Fingerprint(JsonSerializer.Serialize(new
        {
            source.Id,
            source.P2p!.InfoHash,
            source.P2p.FileIndex,
            source.P2p.FileName,
            source.P2p.Season,
            source.P2p.Episode,
            SelectedName = lease.FileName,
            lease.Length
        }));
        var extension = SafeExtension(lease.FileName);
        var state = workspace.ReadResume();
        var offset = workspace.Length("transfer.part");
        if (state is not { Kind: "p2p" } || state.Fingerprint != fingerprint || state.TotalBytes != lease.Length
            || offset > lease.Length || !lease.Content.CanSeek)
        {
            workspace.Reset();
            offset = 0;
        }
        workspace.EnsureCapacity(lease.Length - offset);
        if (offset > 0) lease.Content.Seek(offset, SeekOrigin.Begin);
        await workspace.SaveResumeAsync(new DownloadResumeState("p2p", fingerprint, null, lease.Length, extension), cancellationToken).ConfigureAwait(false);
        await CopyAsync(lease.Content, workspace, offset, lease.Length, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        return await FinalizeAsync(workspace, extension, lease.Length).ConfigureAwait(false);
    }

    private async Task<DownloadTransferResult> TransferHttpAsync(ResolvedStream source, DownloadTransferWorkspace workspace, CancellationToken cancellationToken)
    {
        var fingerprint = DownloadTransferWorkspace.Fingerprint(source.Id + "\n" + source.Url.AbsoluteUri);
        var state = workspace.ReadResume();
        var offset = workspace.Length("transfer.part");
        var resume = state is { Kind: "http", TotalBytes: > 0 } && state.Fingerprint == fingerprint
            && IsStrongETag(state.ETag) && offset > 0 && offset < state.TotalBytes && IsSafeExtension(state.Extension);
        if (!resume)
        {
            workspace.Reset();
            offset = 0;
        }

        using var response = await OpenHttpAsync(source, resume ? state : null, offset, cancellationToken).ConfigureAwait(false);
        if (resume && response.StatusCode == HttpStatusCode.PartialContent && !CanAppend(response, state!, offset))
        {
            // A server may ignore If-Range or change representations. Never splice those bytes.
            response.Dispose();
            workspace.Reset();
            using var fresh = await OpenHttpAsync(source, null, 0, cancellationToken).ConfigureAwait(false);
            return await ReceiveHttpAsync(source, fresh, workspace, fingerprint, null, 0, cancellationToken).ConfigureAwait(false);
        }
        if (resume && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            response.Dispose();
            workspace.Reset();
            using var fresh = await OpenHttpAsync(source, null, 0, cancellationToken).ConfigureAwait(false);
            return await ReceiveHttpAsync(source, fresh, workspace, fingerprint, null, 0, cancellationToken).ConfigureAwait(false);
        }
        if (response.StatusCode == HttpStatusCode.OK)
        {
            workspace.Reset();
            offset = 0;
            state = null;
        }
        return await ReceiveHttpAsync(source, response, workspace, fingerprint, state, offset, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> OpenHttpAsync(ResolvedStream source, DownloadResumeState? state, long offset, CancellationToken cancellationToken)
    {
        var headers = RequestHeaders(source, source.Url);
        if (state is not null)
        {
            headers["Range"] = "bytes=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-";
            headers["If-Range"] = state.ETag!;
        }
        return await http.SendAsync(source.Url, HttpMethod.Get, headers, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DownloadTransferResult> ReceiveHttpAsync(ResolvedStream source, HttpResponseMessage response,
        DownloadTransferWorkspace workspace, string fingerprint, DownloadResumeState? state, long offset, CancellationToken cancellationToken)
    {
        if (offset > 0 ? state is null || !CanAppend(response, state, offset) : response.StatusCode != HttpStatusCode.OK)
            throw new IOException("The upstream server did not return a valid download representation.");
        EnsureIdentity(response);
        var total = offset > 0 ? response.Content.Headers.ContentRange!.Length : response.Content.Headers.ContentLength;
        if (total is long length) workspace.EnsureCapacity(length - offset);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var prefix = new byte[512];
        var prefixLength = 0;
        if (offset == 0)
        {
            while (prefixLength < prefix.Length)
            {
                var read = await DownloadTransferWorkspace.ReadAsync(input, prefix.AsMemory(prefixLength), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                prefixLength += read;
            }
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var finalUrl = response.RequestMessage?.RequestUri ?? source.Url;
            if (mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
                || finalUrl.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                || Encoding.UTF8.GetString(prefix, 0, prefixLength).TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                var transfer = new HlsOfflineTransfer(http, encoder, source, workspace);
                return await transfer.TransferAsync(input, prefix.AsMemory(0, prefixLength), finalUrl, cancellationToken).ConfigureAwait(false);
            }
        }
        var extension = offset > 0 ? state!.Extension : SafeExtension(source.FileName ?? source.Url.AbsolutePath);
        var etag = response.Headers.ETag is { IsWeak: false } value && IsStrongETag(value.ToString()) ? value.ToString() : null;
        await workspace.SaveResumeAsync(new DownloadResumeState("http", fingerprint, etag, total, extension), cancellationToken).ConfigureAwait(false);
        await CopyAsync(input, workspace, offset, total, prefix.AsMemory(0, prefixLength), cancellationToken).ConfigureAwait(false);
        return await FinalizeAsync(workspace, extension, workspace.Length("transfer.part")).ConfigureAwait(false);
    }

    internal static bool CanAppend(HttpResponseMessage response, DownloadResumeState state, long offset)
    {
        var range = response.Content.Headers.ContentRange;
        return response.StatusCode == HttpStatusCode.PartialContent && offset > 0 && IsStrongETag(state.ETag)
            && response.Headers.ETag is { IsWeak: false } tag && tag.ToString() == state.ETag
            && range is { HasRange: true, HasLength: true, Unit: "bytes" } && range.From == offset
            && range.Length == state.TotalBytes && range.To == range.Length - 1
            && response.Content.Headers.ContentLength == range.Length - offset
            && response.Content.Headers.ContentEncoding.All(value => value.Equals("identity", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsStrongETag(string? value) => value?.Length is > 1 and <= 1024
        && EntityTagHeaderValue.TryParse(value, out var tag) && !tag.IsWeak && tag.Tag != "*";

    internal static Dictionary<string, string> RequestHeaders(ResolvedStream source, Uri target)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source.Url.Scheme == target.Scheme && source.Url.Port == target.Port
            && source.Url.IdnHost.Equals(target.IdnHost, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (name, value) in source.RequestHeaders)
            {
                if (!name.Equals("Range", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("If-", StringComparison.OrdinalIgnoreCase))
                    headers[name] = value;
            }
        }
        headers["Accept-Encoding"] = "identity";
        return headers;
    }

    internal static void EnsureIdentity(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Encoded download representations are not supported.");
    }

    private static async Task CopyAsync(Stream input, DownloadTransferWorkspace workspace, long offset, long? total,
        ReadOnlyMemory<byte> prefix, CancellationToken cancellationToken)
    {
        await using var output = workspace.OpenWrite("transfer.part", offset > 0 ? FileMode.Append : FileMode.CreateNew);
        var received = offset;
        await workspace.ReportAsync(received, total, force: true).ConfigureAwait(false);
        if (prefix.Length > 0)
        {
            if (total is long expected && prefix.Length > expected) throw new IOException("The upstream length does not match the download.");
            await workspace.AppendAsync(output, prefix, cancellationToken).ConfigureAwait(false);
            received += prefix.Length;
        }
        var buffer = new byte[DownloadTransferWorkspace.BufferSize];
        while (true)
        {
            var read = await DownloadTransferWorkspace.ReadAsync(input, buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (total is long expected && read > expected - received) throw new IOException("The upstream length does not match the download.");
            await workspace.AppendAsync(output, buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            await workspace.ReportAsync(received, total).ConfigureAwait(false);
        }
        if (received == 0 || (total.HasValue && received != total.Value)) throw new IOException("The upstream download ended before the complete file arrived.");
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        await workspace.ReportAsync(received, total ?? received, force: true).ConfigureAwait(false);
    }

    private static async Task<DownloadTransferResult> FinalizeAsync(DownloadTransferWorkspace workspace, string extension, long bytes)
    {
        var name = "media" + extension;
        workspace.Delete("resume.json");
        workspace.Move("transfer.part", name);
        await workspace.ReportAsync(bytes, bytes, force: true).ConfigureAwait(false);
        return new DownloadTransferResult(name, ContentType(extension), bytes);
    }

    private static bool IsSafeExtension(string extension) => extension is ".mp4" or ".mkv" or ".webm" or ".avi" or ".mov" or ".m4v" or ".ts" or ".bin";
    private static string SafeExtension(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return IsSafeExtension(extension) ? extension : ".bin";
    }
    private static string ContentType(string extension) => extension switch
    {
        ".mkv" => "video/x-matroska",
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".ts" => "video/mp2t",
        ".mov" => "video/quicktime",
        ".avi" => "video/x-msvideo",
        _ => "application/octet-stream"
    };
}
