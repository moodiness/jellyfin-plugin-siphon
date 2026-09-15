using System.Text.Json;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Protocol;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Siphon.Configuration;

/// <summary>Administrator-only addon discovery. Install links are returned only for explicit administrator selection.</summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Siphon/Addons")]
public sealed class AddonDiscoveryController(ISafeHttpClient http, ConfigurationAccessor configuration) : ControllerBase
{
    private static readonly SemaphoreSlim Requests = new(16, 16);

    [HttpPost("Discover")]
    [RequestSizeLimit(128 * 1024)]
    public async Task<ActionResult<DiscoveryResponse>> Discover([FromBody] DiscoveryRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ManifestUrl) || request.ManifestUrl.Length > 16384)
        {
            return BadRequest(new { Message = "Enter an HTTP(S) or stremio:// addon manifest URL." });
        }

        var hasSelection = request.Type is not null || request.Id is not null;
        if (hasSelection && (string.IsNullOrWhiteSpace(request.Type) || request.Type.Length > 1024
            || string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 1024))
        {
            return BadRequest(new { Message = "Choose both the type and ID of an advertised addon discovery catalog." });
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.Current.AddonTimeoutSeconds, 1, 120)));
        var acquired = false;
        try
        {
            var manifestUri = StremioClient.ManifestUri(request.ManifestUrl);
            await Requests.WaitAsync(timeout.Token).ConfigureAwait(false);
            acquired = true;
            IReadOnlyList<DiscoveryCatalog> catalogs;
            using (var manifest = await FetchAsync(manifestUri, timeout.Token).ConfigureAwait(false))
            {
                catalogs = ReadCatalogs(manifest.RootElement);
            }

            if (!hasSelection)
            {
                return new DiscoveryResponse(catalogs, []);
            }

            // addonCatalogs has its own types and IDs: Cinemeta's "all" is not a media type.
            if (!catalogs.Any(catalog => catalog.Type == request.Type && catalog.Id == request.Id))
            {
                return BadRequest(new { Message = "This addon does not advertise the selected discovery catalog. Refresh discovery and try again." });
            }

            using var result = await FetchAsync(StremioClient.ResourceUri(request.ManifestUrl, "addon_catalog", request.Type!, request.Id!), timeout.Token).ConfigureAwait(false);
            return new DiscoveryResponse(catalogs, ReadAddons(result.RootElement));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Neither exception text nor upstream URLs belong in errors or logs.
            return BadRequest(new { Message = "Addon discovery failed. Check the manifest URL, network access, saved private-host exceptions, and addon availability." });
        }
        finally
        {
            if (acquired) Requests.Release();
        }
    }

    private async Task<JsonDocument> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(uri, HttpMethod.Get, null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new StremioException("Addon discovery request failed.");
        const int maximum = 8 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximum) throw new StremioException("Addon discovery response exceeds the size limit.");
        using var output = new MemoryStream();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > maximum) throw new StremioException("Addon discovery response exceeds the size limit.");
            output.Write(buffer, 0, count);
        }

        return JsonDocument.Parse(output.GetBuffer().AsMemory(0, (int)output.Length), new JsonDocumentOptions { MaxDepth = 48 });
    }

    private static IReadOnlyList<DiscoveryCatalog> ReadCatalogs(JsonElement manifest)
    {
        if (string.IsNullOrWhiteSpace(Text(manifest, "id", 1024))) throw new StremioException("Invalid addon manifest.");
        if (!manifest.TryGetProperty("addonCatalogs", out var entries)) return [];
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 128) throw new StremioException("Invalid addon discovery catalogs.");
        var result = new List<DiscoveryCatalog>();
        var seen = new HashSet<(string Type, string Id)>();
        foreach (var entry in entries.EnumerateArray())
        {
            var type = Text(entry, "type", 1024);
            var id = Text(entry, "id", 1024);
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id) || !seen.Add((type, id))) continue;
            result.Add(new DiscoveryCatalog(type, id, Text(entry, "name", 1024) ?? id));
        }

        return result;
    }

    private static IReadOnlyList<DiscoveredAddon> ReadAddons(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("addons", out var entries)
            || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 10000)
        {
            throw new StremioException("Invalid addon discovery response.");
        }

        var result = new List<DiscoveredAddon>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            if (Text(entry, "transportName", 32) != "http" || Text(entry, "transportUrl", 16384) is not { Length: > 0 } transportUrl
                || !entry.TryGetProperty("manifest", out var manifest)) continue;
            var id = Text(manifest, "id", 1024);
            var name = Text(manifest, "name", 1024);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
            try
            {
                // Syntax only: do not contact or install an addon until the administrator chooses it.
                var url = StremioClient.ManifestUri(transportUrl).OriginalString;
                if (seen.Add(url)) result.Add(new DiscoveredAddon(url, id, name, Text(manifest, "description", 16384) ?? ""));
            }
            catch (StremioException)
            {
                // Unsupported or malformed transport links cannot become installation choices.
            }
        }

        return result;
    }

    private static string? Text(JsonElement element, string property, int maximum)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return text is not null && text.Length <= maximum ? text : null;
    }

    public sealed record DiscoveryRequest(string ManifestUrl, string? Type = null, string? Id = null);
    public sealed record DiscoveryResponse(IReadOnlyList<DiscoveryCatalog> Catalogs, IReadOnlyList<DiscoveredAddon> Addons);
    public sealed record DiscoveryCatalog(string Type, string Id, string Name);
    public sealed record DiscoveredAddon(string TransportUrl, string Id, string Name, string Description);
}
