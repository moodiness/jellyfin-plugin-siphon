using Jellyfin.Database.Implementations;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.Siphon.Search;

/// <summary>Applies release exclusions to the native Items fallback before its count and pagination.</summary>
public sealed class NativeSearchReleaseFilter(UserPreferenceStore preferences, ConfigurationAccessor configuration,
    IAuthorizationContext authorization, IDbContextFactory<JellyfinDbContext> database) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!HttpMethods.IsGet(context.HttpContext.Request.Method)
            || context.ActionDescriptor is not ControllerActionDescriptor action
            || action.ControllerTypeInfo.Assembly.GetName().Name != "Jellyfin.Api"
            || action.ControllerName != "Items" || action.ActionName is not ("GetItems" or "GetItemsLegacy")
            || !context.ActionArguments.TryGetValue("searchTerm", out var term) || term is not string searchTerm
            || string.IsNullOrWhiteSpace(searchTerm))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var authenticated = (await authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false)).User;
        var requestedId = context.ActionArguments.TryGetValue("userId", out var requested) && requested is Guid id
            ? id : Guid.Empty;
        if (authenticated is null || requestedId != Guid.Empty && requestedId != authenticated.Id
            && !context.HttpContext.User.IsInRole("Administrator"))
        {
            await next().ConfigureAwait(false);
            return;
        }
        var userId = requestedId == Guid.Empty ? authenticated.Id : requestedId;
        if (preferences.HidesUnreleased(userId))
        {
            // Jellyfin retries its database search when every provider result was filtered.
            // Native exclusions also cover explicit IDs without removing personal future-dated media.
            var now = DateTime.UtcNow;
            var cutoff = now.AddDays(-Math.Clamp(configuration.Current.UnreleasedBufferDays, 0, 30));
            await using var db = await database.CreateDbContextAsync(context.HttpContext.RequestAborted).ConfigureAwait(false);
            var hidden = await db.BaseItems.AsNoTracking()
                .Where(item => (item.PremiereDate > cutoff || item.PremiereDate == null && item.ProductionYear > now.Year)
                    && item.Provider!.Any(provider => provider.ProviderId == "Siphon" && provider.ProviderValue != ""))
                .Select(item => item.Id).Take(SiphonStateStore.MaximumItems + 1)
                .ToArrayAsync(context.HttpContext.RequestAborted).ConfigureAwait(false);
            if (hidden.Length > SiphonStateStore.MaximumItems)
            {
                context.Result = new ObjectResult(new { Message = "The native release filter exceeds its item limit." }) { StatusCode = 503 };
                return;
            }
            if (hidden.Length > 0)
            {
                var existing = context.ActionArguments.TryGetValue("excludeItemIds", out var excluded) && excluded is Guid[] ids ? ids : [];
                context.ActionArguments["excludeItemIds"] = existing.Concat(hidden).Distinct().ToArray();
            }
        }
        await next().ConfigureAwait(false);
    }
}
