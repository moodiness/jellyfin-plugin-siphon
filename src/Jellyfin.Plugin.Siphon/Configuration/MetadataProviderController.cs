using Jellyfin.Plugin.Siphon.Metadata;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Siphon.Configuration;

/// <summary>Tests saved credentials only; no secrets or upstream responses leave the server.</summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Siphon/Metadata/Providers")]
public sealed class MetadataProviderController(MetadataProviderClient providers) : ControllerBase
{
    [HttpGet("Status")]
    public ActionResult<IReadOnlyList<MetadataProviderStatus>> GetStatus() => Ok(providers.GetStatus());

    [HttpPost("{provider}/Test")]
    public async Task<ActionResult<MetadataProviderResult>> Test(string provider, CancellationToken cancellationToken)
    {
        var name = MetadataProviderClient.ProviderNames.FirstOrDefault(candidate => candidate.Equals(provider, StringComparison.OrdinalIgnoreCase));
        if (name is null) return BadRequest(new MetadataProviderResult("Unknown", false, "UnknownProvider", 0));
        return await providers.TestAsync(name, cancellationToken).ConfigureAwait(false);
    }
}
