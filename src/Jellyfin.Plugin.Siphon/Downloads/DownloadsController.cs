using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Siphon.Downloads;

[ApiController]
[Authorize]
[Route("Siphon/Downloads")]
public sealed class DownloadsController(DownloadQueueService queue, IAuthorizationContext authorization) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> List() => ForUser(user => Task.FromResult<IActionResult>(Ok(queue.List(user))));

    [HttpPost]
    public Task<IActionResult> Enqueue([FromBody] DownloadQueueRequest request)
        => ForUser(async user => Ok(await queue.EnqueueAsync(user, request, HttpContext.RequestAborted).ConfigureAwait(false)));

    [HttpPost("Preparation")]
    public Task<IActionResult> Preparation([FromBody] DownloadPreparationRequest request)
        => ForUser(async user => Ok(await queue.PreparationAsync(user, request, HttpContext.RequestAborted).ConfigureAwait(false)));

    [HttpPost("BatchPreview")]
    public Task<IActionResult> BatchPreview([FromBody] DownloadBatchPreviewRequest request)
        => ForUser(async user => Ok(await queue.BatchPreviewAsync(user, request.ItemId, HttpContext.RequestAborted).ConfigureAwait(false)));

    [HttpPost("Batch")]
    public Task<IActionResult> Batch([FromBody] DownloadBatchRequest request)
        => ForUser(async user => Ok(await queue.BatchAsync(user, request, HttpContext.RequestAborted).ConfigureAwait(false)));

    [HttpPost("{id:guid}/Pause")]
    public Task<IActionResult> Pause(Guid id)
        => ForUser(async user => Ok(await queue.PauseAsync(user, id, HttpContext.RequestAborted).ConfigureAwait(false)));

    [HttpPost("{id:guid}/Resume")]
    public Task<IActionResult> Resume(Guid id)
        => ForUser(async user => Ok(await queue.ResumeAsync(user, id, HttpContext.RequestAborted).ConfigureAwait(false)));

    [HttpPut("{id:guid}/Priority")]
    public Task<IActionResult> Priority(Guid id, [FromBody] DownloadPriorityRequest request)
        => ForUser(async user => Ok(await queue.SetPriorityAsync(user, id, request.Priority, HttpContext.RequestAborted).ConfigureAwait(false)));

    [HttpPost("{id:guid}/Cancel")]
    public Task<IActionResult> Cancel(Guid id) => ForUser(async user =>
    {
        await queue.CancelAsync(user, id, delete: false, HttpContext.RequestAborted).ConfigureAwait(false);
        return NoContent();
    });

    [HttpPost("{id:guid}/Retry")]
    public Task<IActionResult> Retry(Guid id)
        => ForUser(async user => Ok(await queue.RetryAsync(user, id, HttpContext.RequestAborted).ConfigureAwait(false)));

    [HttpDelete("{id:guid}")]
    public Task<IActionResult> Delete(Guid id) => ForUser(async user =>
    {
        await queue.CancelAsync(user, id, delete: true, HttpContext.RequestAborted).ConfigureAwait(false);
        return NoContent();
    });

    [HttpPost("{id:guid}/Ticket")]
    public Task<IActionResult> Ticket(Guid id) => ForUser(async user =>
    {
        var ticket = await queue.TicketAsync(user, id, HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(new DownloadFileTicket(Request.PathBase.Value + "/Siphon/Downloads/File/" + ticket.Token, ticket.Expires));
    });

    [AllowAnonymous]
    [HttpGet("File/{token}")]
    [HttpHead("File/{token}")]
    public async Task<IActionResult> Download(string token)
    {
        PrivateResponse();
        try
        {
            var user = (await authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).User;
            var result = await queue.OpenTicketAsync(token, user?.Id, user is not null || User.Identity?.IsAuthenticated == true,
                HttpContext.RequestAborted).ConfigureAwait(false);
            return result is { } file ? File(file.Stream, file.ContentType, file.FileName, enableRangeProcessing: true) : NotFound();
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch { return NotFound(); }
    }

    private async Task<IActionResult> ForUser(Func<Guid, Task<IActionResult>> operation)
    {
        PrivateResponse();
        var user = (await authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).User;
        if (user is null) return Unauthorized();
        // Read only the authenticated account; userId route/query values never grant impersonation.
        try { return await operation(user.Id).ConfigureAwait(false); }
        catch (DownloadQueueException exception) { return StatusCode(exception.StatusCode, new { exception.Code, exception.Message }); }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch { return StatusCode(503, new { Code = "DownloadUnavailable", Message = "The download queue is temporarily unavailable." }); }
    }

    private void PrivateResponse()
    {
        Response.Headers.CacheControl = "private, no-store, no-transform";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
