using System.Net;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Protocol;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Siphon.Configuration;

/// <summary>Administrator-only configuration discovery, with no upstream URLs in responses.</summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Siphon")]
public sealed class SiphonAdminController(StremioClient client, ISiphonStateStore state, SsrfPolicy policy) : ControllerBase
{
    [HttpGet("Status")]
    public ActionResult<StatusResponse> GetStatus()
    {
        var items = state.GetItems();
        return new StatusResponse(items.Count, items.Count(item => item.Type == "movie"), items.Count(item => item.Type == "series"));
    }

    [HttpPost("Addons/Validate")]
    [RequestSizeLimit(128 * 1024)]
    public async Task<ActionResult<ManifestResponse>> ValidateAddon([FromBody] ValidateRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ManifestUrl) || request.ManifestUrl.Length > 16384)
        {
            return BadRequest(new { Message = "Enter an HTTP(S) addon manifest URL." });
        }

        try
        {
            var manifest = await client.GetManifestAsync(request.ManifestUrl, cancellationToken).ConfigureAwait(false);
            return new ManifestResponse(manifest.Id, manifest.Name, manifest.Description, manifest.Catalogs.Select(catalog =>
                new CatalogResponse(catalog.Type, catalog.Id, catalog.Name, catalog.GetExtras().Select(extra =>
                    new ExtraResponse(extra.Name, extra.IsRequired, extra.Options, extra.OptionsLimit, extra.Default)).ToArray())).ToArray(),
                manifest.Types.ToArray(), manifest.Resources.Select(resource => new ResourceResponse(resource.Name,
                    resource.IsObject ? resource.Types?.ToArray() : manifest.Types.ToArray(),
                    (resource.IsObject ? resource.IdPrefixes : manifest.IdPrefixes)?.ToArray(), resource.IsObject)).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (StremioException)
        {
            if (TryGetBlockedPrivateLiteralHost(request.ManifestUrl, policy, out var host))
            {
                return BadRequest(new ValidationError("PrivateHostBlocked", $"The private addon host '{host}' is blocked. Add it to Allowed private hosts under Connection & limits, save, then retry.", host));
            }

            return BadRequest(new { Message = "The addon could not be validated. Check its manifest URL and network access." });
        }
        catch (Exception)
        {
            return BadRequest(new { Message = "The addon could not be validated. Check its manifest URL and network access." });
        }
    }

    private static bool TryGetBlockedPrivateLiteralHost(string manifestUrl, SsrfPolicy policy, out string host)
    {
        host = string.Empty;
        try
        {
            var uri = StremioClient.ManifestUri(manifestUrl);
            if (!IPAddress.TryParse(uri.Host, out var address) || SsrfPolicy.IsPublicAddress(address) || policy.AllowsPrivateHost(uri.Host))
            {
                return false;
            }

            host = uri.Host;
            return true;
        }
        catch (StremioException)
        {
            return false;
        }
    }

    public sealed record ValidateRequest(string ManifestUrl);
    public sealed record ValidationError(string Code, string Message, string? Host = null);
    public sealed record StatusResponse(int ItemCount, int MovieCount, int EpisodeCount);
    public sealed record ManifestResponse(string Id, string Name, string Description, IReadOnlyList<CatalogResponse> Catalogs,
        IReadOnlyList<string> Types, IReadOnlyList<ResourceResponse> Resources);
    /// <summary>Effective restrictions use the same object-versus-global rules as AddonRegistry.Supports. Null object types support no types; null prefixes mean unrestricted.</summary>
    public sealed record ResourceResponse(string Name, IReadOnlyList<string>? Types, IReadOnlyList<string>? IdPrefixes, bool IsObject);
    public sealed record CatalogResponse(string Type, string Id, string Name, IReadOnlyList<ExtraResponse> Extras);
    public sealed record ExtraResponse(string Name, bool IsRequired, IReadOnlyList<string> Options, int? OptionsLimit, string? Default);
}
