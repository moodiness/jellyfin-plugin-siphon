using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Siphon.Notifications;

[ApiController]
[Authorize]
[Route("Siphon/Notifications/Webhook")]
public sealed class NotificationWebhookController(NotificationWebhookService service,
    IAuthorizationContext authorization, IUserManager users) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<NotificationWebhookStatus>> Get()
    {
        Response.Headers.CacheControl = "no-store";
        var userId = await CurrentUserAsync().ConfigureAwait(false);
        return userId is { } id ? service.GetStatus(id) : Unauthorized();
    }

    [HttpPut]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<NotificationWebhookStatus>> Configure([FromBody] NotificationWebhookSettings settings, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var userId = await CurrentUserAsync().ConfigureAwait(false);
        if (userId is not { } id) return Unauthorized();
        try
        {
            await service.ConfigureAsync(id, settings, ct).ConfigureAwait(false);
            return service.GetStatus(id);
        }
        catch (ArgumentException exception) { return BadRequest(new { Message = exception.Message }); }
        catch (InvalidOperationException) { return BadRequest(new { Message = "Webhook settings are not currently permitted or storage capacity was reached." }); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return StatusCode(503, new { Message = "Webhook settings could not be saved." }); }
    }

    [HttpPost("Secret")]
    public async Task<ActionResult<NotificationWebhookSecret>> RotateSecret(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var userId = await CurrentUserAsync().ConfigureAwait(false);
        if (userId is not { } id) return Unauthorized();
        try { return new NotificationWebhookSecret(await service.RotateAsync(id, ct).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return StatusCode(503, new { Message = "The webhook signing secret could not be rotated." }); }
    }

    [HttpPost("Test")]
    public async Task<ActionResult<NotificationWebhookTestResult>> Test(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var userId = await CurrentUserAsync().ConfigureAwait(false);
        if (userId is not { } id) return Unauthorized();
        try { return await service.TestAsync(id, ct).ConfigureAwait(false); }
        catch (WebhookTestRateLimitException)
        {
            Response.Headers.RetryAfter = "60";
            return StatusCode(429, new { Message = "Wait at least one minute before sending another webhook test." });
        }
        catch (ArgumentException exception) { return BadRequest(new { Message = exception.Message }); }
        catch (InvalidOperationException) { return BadRequest(new { Message = "Enable all notification options, configure a destination and rotate a signing secret first." }); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return StatusCode(503, new { Message = "The webhook test could not be completed." }); }
    }

    private async Task<Guid?> CurrentUserAsync()
    {
        var authenticated = (await authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).User;
        var user = authenticated is null ? null : users.GetUserById(authenticated.Id);
        return user is null || user.HasPermission(PermissionKind.IsDisabled)
            || (!user.HasPermission(PermissionKind.IsAdministrator) && !user.IsParentalScheduleAllowed()) ? null : user.Id;
    }
}
