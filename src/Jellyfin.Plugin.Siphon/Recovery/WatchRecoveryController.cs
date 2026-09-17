using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Siphon.Recovery;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Siphon/Recovery")]
public sealed class WatchRecoveryController(WatchRecoveryService recovery, IAuthorizationContext authorization) : ControllerBase
{
    [HttpPost("Preview")]
    [RequestSizeLimit(4096)]
    public async Task<ActionResult<RecoveryPreview>> Preview([FromBody] RecoveryPreviewRequest request, CancellationToken ct)
    {
        if (request.StartIndex is < 0 or > 100000 || request.Limit is < 1 or > 100)
            return BadRequest(new { Message = "StartIndex must be from 0 to 100000 and Limit from 1 to 100." });
        var user = (await authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).User;
        if (user is null) return Unauthorized();
        try { return await recovery.PreviewAsync(user.Id, request.StartIndex, request.Limit, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return StatusCode(503, new { Message = "Recovery preview is unavailable. Existing history has not been changed." }); }
    }

    [HttpPost("Execute")]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<RecoveryExecution>> Execute([FromBody] RecoveryExecuteRequest request, CancellationToken ct)
    {
        if (!request.Confirmed || request.PreviewToken is not { Length: 64 }
            || request.Ids is not { Length: > 0 and <= 50 } || request.Ids.Any(id => id is not { Length: 32 }))
            return BadRequest(new { Message = "Confirm recovery and select up to 50 candidates from a current preview." });
        var user = (await authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).User;
        if (user is null) return Unauthorized();
        try { return await recovery.ExecuteAsync(user.Id, request.PreviewToken, request.Ids, ct).ConfigureAwait(false); }
        catch (ArgumentException) { return BadRequest(new { Message = "Only distinct, restorable candidates from this preview can be selected." }); }
        catch (InvalidOperationException) { return Conflict(new { Message = "Recovery preview expired or changed. Generate and review a new preview." }); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return StatusCode(503, new { Message = "Recovery did not complete. Generate a new preview to inspect current state." }); }
    }
}

public sealed record RecoveryPreviewRequest(int StartIndex = 0, int Limit = 50);
public sealed record RecoveryExecuteRequest(string PreviewToken, string[] Ids, bool Confirmed);
