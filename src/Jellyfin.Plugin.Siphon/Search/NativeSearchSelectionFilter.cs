using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Siphon.Search;

/// <summary>Loads only an authorized selected title before Jellyfin builds its detail/episode DTOs.</summary>
public sealed class NativeSearchSelectionFilter(AddonSearchService search, IAuthorizationContext authorization, IUserManager users) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!HttpMethods.IsGet(context.HttpContext.Request.Method)
            || context.ActionDescriptor is not ControllerActionDescriptor action
            || action.ControllerTypeInfo.Assembly.GetName().Name != "Jellyfin.Api")
        {
            await next().ConfigureAwait(false);
            return;
        }
        var argument = (action.ControllerName, action.ActionName) switch
        {
            ("UserLibrary", "GetItem" or "GetItemLegacy") => "itemId",
            ("TvShows", "GetEpisodes" or "GetSeasons") => "seriesId",
            _ => null
        };
        var selected = argument is not null && context.ActionArguments.TryGetValue(argument, out var value) && value is Guid id
            ? id : Guid.Empty;
        if (selected != Guid.Empty)
        {
            var authenticated = (await authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false)).User;
            var requestedId = context.ActionArguments.TryGetValue("userId", out var requested) && requested is Guid requestedUserId
                ? requestedUserId : Guid.Empty;
            // Apply Jellyfin's impersonation rule before any discovery expansion or addon I/O.
            if (requestedId != Guid.Empty && requestedId != authenticated?.Id && !context.HttpContext.User.IsInRole("Administrator"))
            {
                await next().ConfigureAwait(false);
                return;
            }
            var user = requestedId == Guid.Empty ? authenticated : users.GetUserById(requestedId);
            if (user is not null && search.IsAccessible(selected, user))
            {
                try { await search.ExpandAsync(selected, user, false, context.HttpContext.RequestAborted).ConfigureAwait(false); }
                catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested) { throw; }
                catch (SearchAdmissionException exception)
                {
                    context.Result = new ObjectResult(new { exception.Code, exception.Message })
                    { StatusCode = exception.Code == "Busy" ? 503 : 409 };
                    return;
                }
                catch (Exception)
                {
                    context.Result = new ObjectResult(new { Message = "The selected addon title could not be loaded. Retry when its metadata addon is available." }) { StatusCode = 502 };
                    return;
                }
            }
        }
        await next().ConfigureAwait(false);
    }
}
