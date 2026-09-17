using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Siphon.Configuration;

[ApiController]
[Authorize]
[Route("Siphon")]
public sealed class SiphonUserController(UserPreferenceStore preferences, ConfigurationAccessor configuration,
    IAuthorizationContext authorization) : ControllerBase
{
    [HttpGet("User")]
    [AllowAnonymous]
    public IActionResult Portal()
    {
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Siphon.Web.siphon-user.html");
        if (stream is null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; font-src 'self'; base-uri 'none'; frame-ancestors 'self'; form-action 'self'";
        return File(stream, "text/html; charset=utf-8");
    }

    [HttpGet("Preferences")]
    public async Task<ActionResult<UserPreferenceResponse>> GetPreferences()
    {
        var user = (await authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).User;
        if (user is null) return Unauthorized();
        Response.Headers.CacheControl = "no-store";
        return ResponseFor(user.Id);
    }

    [HttpPut("Preferences")]
    public async Task<ActionResult<UserPreferenceResponse>> SavePreferences([FromBody] UserPreferences value, CancellationToken cancellationToken)
    {
        var user = (await authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).User;
        if (user is null) return Unauthorized();
        Response.Headers.CacheControl = "no-store";
        try
        {
            await preferences.SaveAsync(user.Id, value, cancellationToken).ConfigureAwait(false);
            return ResponseFor(user.Id);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { Message = "Choose Inherit, All or Local search." });
        }
        catch (InvalidOperationException)
        {
            return Conflict(new { Message = "The user preference storage limit has been reached." });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return StatusCode(503, new { Message = "Preferences could not be saved. Existing settings were retained." });
        }
    }

    private UserPreferenceResponse ResponseFor(Guid userId)
    {
        var value = preferences.Get(userId);
        return new(value.SearchMode, value.HideUnreleased, value.NotificationsEnabled,
            preferences.UsesLocalSearch(userId) ? "Local" : "All", preferences.HidesUnreleased(userId),
            configuration.Current.EnableCalendarNotifications);
    }
}

public sealed record UserPreferenceResponse(string SearchMode, bool? HideUnreleased, bool NotificationsEnabled,
    string EffectiveSearchMode, bool EffectiveHideUnreleased, bool NotificationsAllowed);
