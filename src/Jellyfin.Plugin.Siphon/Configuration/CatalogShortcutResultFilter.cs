using System.Text;
using Jellyfin.Plugin.Siphon.Identity;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Branding;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Siphon.Configuration;

/// <summary>Hides catalog shortcuts in native web navigation without changing saved branding or user preferences.</summary>
public sealed class CatalogShortcutResultFilter(ConfigurationAccessor configuration, ILibraryManager library) : IResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor { ControllerName: "Branding" } action
            || context.Result is not ObjectResult result
            || result.StatusCode is not (null or >= 200 and < 300)) return;

        var isCss = action.ActionName == "GetBrandingCss" && result.Value is string;
        var options = action.ActionName == "GetBrandingOptions" ? result.Value as BrandingOptionsDto : null;
        if (!isCss && options is null) return;

        var existingCss = isCss ? (string)result.Value! : options!.CustomCss;
        StringBuilder? css = null;
        foreach (var info in library.GetVirtualFolders())
        {
            if (!Guid.TryParse(info.ItemId, out var id)
                || library.GetItemById<CollectionFolder>(id) is not { } folder
                || string.IsNullOrEmpty(folder.GetProviderId(CatalogLibraryService.CatalogProvider))
                || (configuration.Current.ShowCatalogShortcuts
                    && folder.GetProviderId(CatalogLibraryService.PresentationProvider) != "Collection")) continue;

            css ??= new StringBuilder(existingCss);
            // Jellyfin Web v12.1 UserViewNav links use topParentId; its overflow Menu is a body portal.
            // Custom menu links have a target and are not native catalog shortcuts. JSON GUIDs use N format.
            css.Append($"\nheader.MuiAppBar-root > .MuiToolbar-root a:not([target])[href*=\"topParentId={folder.Id:N}\"],")
                .Append($"\n#user-view-overflow-menu a:not([target])[href*=\"topParentId={folder.Id:N}\"]")
                .Append(" { display: none !important; }\n");
        }

        if (css is null) return;
        // The native controller creates this DTO per response; persisted branding is untouched.
        if (options is not null) options.CustomCss = css.ToString();
        else result.Value = css.ToString();
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
