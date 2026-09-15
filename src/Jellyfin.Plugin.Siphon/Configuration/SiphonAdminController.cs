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
public sealed class SiphonAdminController(StremioClient client, ISiphonStateStore state) : ControllerBase
{
    [HttpGet("Status")]
    public ActionResult<StatusResponse> GetStatus()
    {
        var items = state.GetItems();
        return new StatusResponse(items.Count, items.Count(item => item.Type == "movie"), items.Count(item => item.Type == "series"));
    }

    [HttpPost("Addons/Validate")]
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
                    new ExtraResponse(extra.Name, extra.IsRequired, extra.Options, extra.OptionsLimit)).ToArray())).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Exception messages may contain credential-bearing URLs or addon response bodies.
            return BadRequest(new { Message = "The addon could not be validated. Check its manifest URL, network access, and private-host policy." });
        }
    }

    public sealed record ValidateRequest(string ManifestUrl);
    public sealed record StatusResponse(int ItemCount, int MovieCount, int EpisodeCount);
    public sealed record ManifestResponse(string Id, string Name, string Description, IReadOnlyList<CatalogResponse> Catalogs);
    public sealed record CatalogResponse(string Type, string Id, string Name, IReadOnlyList<ExtraResponse> Extras);
    public sealed record ExtraResponse(string Name, bool IsRequired, IReadOnlyList<string> Options, int? OptionsLimit);
}
