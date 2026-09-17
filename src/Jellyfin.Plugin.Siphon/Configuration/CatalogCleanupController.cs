using Jellyfin.Plugin.Siphon.Identity;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Siphon.Configuration;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Siphon/Cleanup")]
public sealed class CatalogCleanupController(CatalogCleanupService cleanup) : ControllerBase
{
    [HttpGet("Preview")]
    public async Task<ActionResult<CleanupPreview>> Preview([FromQuery] int startIndex = 0, [FromQuery] int limit = 50,
        [FromQuery] string? previewToken = null, CancellationToken cancellationToken = default)
    {
        if (startIndex < 0 || limit is < 1 or > 200)
            return BadRequest(new { Message = "StartIndex must be nonnegative and Limit must be between 1 and 200." });
        if (previewToken is not null && previewToken.Length != 64)
            return BadRequest(new { Message = "Use the current preview token when requesting more items." });
        try
        {
            return await cleanup.PreviewAsync(startIndex, limit, cancellationToken, previewToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return Conflict(new { Message = "The cleanup preview changed or expired. Refresh the preview before continuing." });
        }
    }

    [HttpPost("Execute")]
    [RequestSizeLimit(4096)]
    public async Task<ActionResult<CleanupExecution>> Execute([FromBody] CleanupRequest request, CancellationToken cancellationToken)
    {
        if (!request.Confirmed || string.IsNullOrWhiteSpace(request.PreviewToken) || request.PreviewToken.Length != 64)
            return BadRequest(new { Message = "Confirm cleanup using a current preview token." });
        try
        {
            return await cleanup.ExecuteAsync(request.PreviewToken, request.Confirmed, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return Conflict(new { Message = "Cleanup could not be completed safely. Refresh the preview and review the saved settings before confirming again." });
        }
    }

    public sealed record CleanupRequest(string PreviewToken, bool Confirmed);
}
