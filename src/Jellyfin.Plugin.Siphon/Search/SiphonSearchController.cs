using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Siphon.Search;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Siphon/Search")]
public sealed class SiphonSearchController(AddonSearchService search, IAuthorizationContext authorization) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<SearchItems>> Search([FromQuery] string searchTerm, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(searchTerm) || searchTerm.Trim().Length is < 2 or > 160)
            return BadRequest(new { Message = "Enter a search term between 2 and 160 characters." });
        var user = (await authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).User;
        if (user is null) return Unauthorized();
        try
        {
            var items = await search.SearchAsync(new SearchProviderQuery { SearchTerm = searchTerm, UserId = user.Id, Limit = 40 }, cancellationToken).ConfigureAwait(false);
            return new SearchItems(items, items.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return StatusCode(503, new { Message = "Addon search is unavailable. Check enabled addons and the public Jellyfin URL." }); }
    }

    [HttpGet("Items")]
    public async Task<ActionResult<SearchItems>> GetAdded()
    {
        var user = (await authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).User;
        if (user is null) return Unauthorized();
        var items = search.GetAdded(user);
        return new SearchItems(items, items.Count);
    }

    [HttpPost("Items/{id:guid}/Add")]
    public async Task<ActionResult<SearchItem>> Add(Guid id, CancellationToken cancellationToken)
    {
        var user = (await authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).User;
        if (user is null) return Unauthorized();
        try
        {
            var item = await search.AddAsync(id, user, cancellationToken).ConfigureAwait(false);
            if (item is null) return NotFound();
            if (!item.IsAdded) return Conflict(new { Message = "This item is not a Siphon discovery and cannot be adopted." });
            return item;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SearchAdmissionException exception)
        {
            return StatusCode(exception.Code == "Busy" ? 503 : 409, new { exception.Code, exception.Message });
        }
        catch (Exception) { return StatusCode(502, new { Message = "The title could not be added. Its addon may be unavailable or could not supply usable metadata." }); }
    }
}
