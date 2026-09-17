using System.Reflection;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Branding;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Configuration;

public sealed class CatalogShortcutResultFilterTests
{
    [Theory]
    [InlineData(false, "body { color: teal; }\r\n")]
    [InlineData(false, "")]
    [InlineData(true, "body { color: teal; }\r\n")]
    [InlineData(true, null)]
    public void HidingPreservesCustomCssAndShowingReturnsNativeCss(bool options, string? existingCss)
    {
        var config = new PluginConfiguration();
        var catalog = new CollectionFolder { Id = Guid.NewGuid() };
        catalog.SetProviderId(CatalogLibraryService.CatalogProvider, "addon:movie:catalog");
        var library = DispatchProxy.Create<ILibraryManager, Library>();
        var stored = (Library)(object)library;
        stored.Items.Add(catalog);
        var filter = new CatalogShortcutResultFilter(new(() => config), library);
        object? NativeValue() => options ? new BrandingOptionsDto { CustomCss = existingCss } : existingCss;
        var action = options ? "GetBrandingOptions" : "GetBrandingCss";
        var hidden = Context("Branding", action, new ObjectResult(NativeValue()));

        filter.OnResultExecuting(hidden);

        var hiddenValue = Assert.IsType<ObjectResult>(hidden.Result).Value;
        var css = options ? Assert.IsType<BrandingOptionsDto>(hiddenValue).CustomCss : Assert.IsType<string>(hiddenValue);
        Assert.StartsWith(existingCss ?? string.Empty, css);
        Assert.Contains(catalog.Id.ToString("N"), css);

        config.ShowCatalogShortcuts = true;
        var shown = Context("Branding", action, new ObjectResult(NativeValue()));
        filter.OnResultExecuting(shown);

        var shownValue = Assert.IsType<ObjectResult>(shown.Result).Value;
        Assert.Equal(existingCss, options ? Assert.IsType<BrandingOptionsDto>(shownValue).CustomCss : shownValue);
    }

    [Fact]
    public void OnlyMarkedCollectionFoldersContributeSelectors()
    {
        var catalog = new CollectionFolder { Id = Guid.NewGuid(), Name = "Catalog" };
        catalog.SetProviderId(CatalogLibraryService.CatalogProvider, "addon:movie:catalog");
        var native = new CollectionFolder { Id = Guid.NewGuid(), Name = "Catalog" };
        var personal = new CollectionFolder { Id = Guid.NewGuid(), Name = "Siphon" };
        personal.SetProviderId("SiphonStorage", "movies");
        var folder = new Folder { Id = Guid.NewGuid(), Name = "Catalog" };
        folder.SetProviderId(CatalogLibraryService.CatalogProvider, "addon:movie:catalog");
        var library = DispatchProxy.Create<ILibraryManager, Library>();
        var stored = (Library)(object)library;
        stored.Items.AddRange([catalog, native, personal, folder]);
        var filter = new CatalogShortcutResultFilter(new(() => new PluginConfiguration()), library);
        var context = Context("Branding", "GetBrandingCss", new ObjectResult(string.Empty));

        filter.OnResultExecuting(context);

        var css = Assert.IsType<string>(Assert.IsType<ObjectResult>(context.Result).Value);
        Assert.Contains(catalog.Id.ToString("N"), css);
        Assert.DoesNotContain(native.Id.ToString("N"), css);
        Assert.DoesNotContain(personal.Id.ToString("N"), css);
        Assert.DoesNotContain(folder.Id.ToString("N"), css);
    }

    [Theory]
    [InlineData("Other", "GetBrandingCss", 200)]
    [InlineData("Branding", "Other", 200)]
    [InlineData("Branding", "GetBrandingOptions", 200)]
    [InlineData("Branding", "GetBrandingCss", 400)]
    public void UnrelatedOrFailedResultsDoNotAccessConfigurationOrLibrary(string controller, string action, int status)
    {
        var filter = new CatalogShortcutResultFilter(null!, null!);
        var result = new ObjectResult("unchanged") { StatusCode = status };
        var context = Context(controller, action, result);

        filter.OnResultExecuting(context);

        Assert.Equal("unchanged", Assert.IsType<ObjectResult>(context.Result).Value);
    }

    [Fact]
    public void NoCatalogsLeavesNativeBrandingUnchanged()
    {
        var library = DispatchProxy.Create<ILibraryManager, Library>();
        ((Library)(object)library).Items.Add(new CollectionFolder { Id = Guid.NewGuid(), Name = "Siphon" });
        var branding = new BrandingOptionsDto { CustomCss = null };
        var context = Context("Branding", "GetBrandingOptions", new ObjectResult(branding));
        var filter = new CatalogShortcutResultFilter(new(() => new PluginConfiguration()), library);

        filter.OnResultExecuting(context);

        Assert.Null(Assert.IsType<BrandingOptionsDto>(Assert.IsType<ObjectResult>(context.Result).Value).CustomCss);
    }

    private static ResultExecutingContext Context(string controller, string action, IActionResult result)
        => new(new ActionContext(new DefaultHttpContext(), new RouteData(),
            new ControllerActionDescriptor { ControllerName = controller, ActionName = action }), [], result, new object());

    public class Library : DispatchProxy
    {
        public List<BaseItem> Items { get; } = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "GetVirtualFolders")
                return Items.Select(item => new VirtualFolderInfo { ItemId = item.Id.ToString("N") }).ToList();
            if (method?.Name == "GetItemById")
            {
                var item = Items.FirstOrDefault(candidate => candidate.Id == (Guid)args![0]!);
                return item is not null && method.ReturnType.IsInstanceOfType(item) ? item : null;
            }
            throw new NotSupportedException(method?.Name);
        }
    }
}
