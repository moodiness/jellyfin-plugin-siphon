using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Siphon.P2p;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Siphon/P2P")]
public sealed class P2pController(P2pStreamService streams) : ControllerBase
{
    [HttpGet("Status")]
    public ActionResult<P2pStatus> GetStatus()
    {
        try { return streams.GetStatus(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status409Conflict, new { Message = "P2P storage is inaccessible or contains unsafe filesystem links." });
        }
    }

    [HttpPost("Cleanup")]
    public ActionResult<P2pCleanupResult> Cleanup()
    {
        try { return streams.Cleanup(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status409Conflict, new { Message = "P2P cleanup could not safely inspect or remove inactive scratch data." });
        }
    }
}
