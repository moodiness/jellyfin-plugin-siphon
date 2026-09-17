using System.Text.Json;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;

namespace Jellyfin.Plugin.Siphon.Protocol;

public sealed class StremioClient(ISafeHttpClient http, ConfigurationAccessor configuration) : IDisposable
{
    private readonly SemaphoreSlim _requests = new(16, 16);
    public static Uri ResourceUri(string manifestUrl, string resource, string type, string id, IReadOnlyDictionary<string, string>? extras = null)
    {
        manifestUrl = ManifestUri(manifestUrl).OriginalString;
        // Work with the original escaped representation: installation tokens are opaque.
        var queryIndex = manifestUrl.IndexOf('?');
        var path = queryIndex < 0 ? manifestUrl : manifestUrl[..queryIndex];
        var query = queryIndex < 0 ? "" : manifestUrl[queryIndex..];
        var prefix = path[..^"manifest.json".Length];
        var extra = extras is { Count: > 0 } ? "/" + string.Join("&", extras.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))) : "";
        return new Uri(prefix + Uri.EscapeDataString(resource) + "/" + Uri.EscapeDataString(type) + "/" + Uri.EscapeDataString(id) + extra + ".json" + query);
    }
    public static Uri ManifestUri(string value)
    {
        if (value.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value["stremio://".Length..];
        }

        var queryIndex = value.IndexOf('?');
        var originalPath = queryIndex < 0 ? value : value[..queryIndex];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !originalPath.EndsWith("/manifest.json", StringComparison.Ordinal)) throw new StremioException("Use an HTTP(S) or stremio:// addon URL ending in /manifest.json.");
        return uri;
    }
    public Task<StremioManifest> GetManifestAsync(string manifestUrl, CancellationToken ct) => Fetch(ManifestUri(manifestUrl), StremioJson.Manifest, ct);
    public Task<StremioCatalogPage> GetCatalogAsync(string manifestUrl, string type, string id, IReadOnlyDictionary<string, string>? extras, CancellationToken ct) => Fetch(ResourceUri(manifestUrl, "catalog", type, id, extras), StremioJson.Catalog, ct);
    public Task<StremioMeta?> GetMetaAsync(string manifestUrl, string type, string id, CancellationToken ct) => Fetch(ResourceUri(manifestUrl, "meta", type, id), StremioJson.MetaResponse, ct);
    public Task<IReadOnlyList<StremioStream>> GetStreamsAsync(string manifestUrl, string type, string videoId, CancellationToken ct) => Fetch(ResourceUri(manifestUrl, "stream", type, videoId), StremioJson.Streams, ct);
    private async Task<T> Fetch<T>(Uri uri, Func<JsonElement, T> parse, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.Current.AddonTimeoutSeconds, 1, 120)));
        var acquired = false;
        try
        {
            await _requests.WaitAsync(timeout.Token).ConfigureAwait(false);
            acquired = true;
            using var response = await http.SendAsync(uri, HttpMethod.Get, null, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new StremioException("Addon request failed (HTTP " + (int)response.StatusCode + ").");
            const int maximum = 8 * 1024 * 1024;
            using var output = new MemoryStream();
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[16384];
            int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > maximum) throw new StremioException("Addon response exceeds the size limit.");
                output.Write(buffer, 0, count);
            }
            using var document = JsonDocument.Parse(output.GetBuffer().AsMemory(0, (int)output.Length), new JsonDocumentOptions { MaxDepth = 48 });
            return parse(document.RootElement);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (StremioException) { throw; }
        catch (OperationCanceledException) { throw new StremioException("Addon request timed out.", isTimeout: true); }
        catch (Exception) { throw new StremioException("Addon request failed or returned invalid JSON."); }
        finally { if (acquired) _requests.Release(); }
    }
    public void Dispose() => _requests.Dispose();
}
