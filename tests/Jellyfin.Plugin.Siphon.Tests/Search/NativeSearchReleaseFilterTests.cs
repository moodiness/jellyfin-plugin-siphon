using System.Reflection;
using System.Reflection.Emit;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Search;
using Jellyfin.Plugin.Siphon.Tests.Identity;
using Jellyfin.Plugin.Siphon.Tests.Infrastructure;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Search;

public sealed class NativeSearchReleaseFilterTests
{
    [Fact]
    public async Task EmptyProviderFallbackCountsAndPaginatesOnlyReleasedManagedOrPersonalItems()
    {
        var directory = Path.Combine(Path.GetTempPath(), "siphon-search-fallback-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var database = new FollowedSeriesSelectorTests.Database();
            var configuration = new ConfigurationAccessor(() => new PluginConfiguration());
            var paths = DispatchProxy.Create<IApplicationPaths, MetadataQuotaStoreTests.PathsProxy>();
            ((MetadataQuotaStoreTests.PathsProxy)(object)paths).Directory = directory;
            using var preferences = new UserPreferenceStore(new SiphonPaths(paths), configuration);
            var user = new User("viewer", "authentication", "password-reset");
            await preferences.SaveAsync(user.Id, new UserPreferences { SearchMode = "Local", HideUnreleased = true }, default);
            var future = Item("A future", true, DateTime.UtcNow.AddYears(2));
            var futureYear = Item("A future year only", true, null);
            futureYear.ProductionYear = DateTime.UtcNow.Year + 2;
            var released = Item("B released", true, DateTime.UtcNow.AddYears(-1));
            var personal = Item("C personal future", false, DateTime.UtcNow.AddYears(2));
            var excluded = Item("D excluded by client", false, DateTime.UtcNow.AddYears(-1));
            var unknown = Item("E unknown date", true, null);
            await using (var db = database.CreateDbContext())
            {
                db.BaseItems.AddRange(future, futureYear, released, personal, excluded, unknown);
                await db.SaveChangesAsync();
            }
            var authorization = DispatchProxy.Create<IAuthorizationContext, NativeSearchBehaviorTests.CallbackProxy>();
            ((NativeSearchBehaviorTests.CallbackProxy)(object)authorization).InvokeMethod = (method, _) => method.Name == "GetAuthorizationInfo"
                ? Task.FromResult(new AuthorizationInfo { User = user }) : throw new NotSupportedException(method.Name);
            var nativeController = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Jellyfin.Api"), AssemblyBuilderAccess.Run)
                .DefineDynamicModule("ReleaseSearch").DefineType("ReleaseSearch.Controller", TypeAttributes.Public).CreateTypeInfo()!;
            var http = new DefaultHttpContext();
            http.Request.Method = "GET";
            var action = new ActionContext(http, new RouteData(), new ControllerActionDescriptor
            {
                ControllerName = "Items",
                ActionName = "GetItems",
                ControllerTypeInfo = nativeController
            });
            var executing = new ActionExecutingContext(action, [], new Dictionary<string, object?>
            {
                ["userId"] = user.Id,
                ["searchTerm"] = "fixture",
                ["excludeItemIds"] = new[] { excluded.Id }
            }, new object());
            QueryResult<BaseItemDto>? response = null;
            await new NativeSearchReleaseFilter(preferences, configuration, authorization, database).OnActionExecutionAsync(executing, async () =>
            {
                // Jellyfin's empty-provider path executes its native query, then counts and pages.
                var exclusions = (Guid[])executing.ActionArguments["excludeItemIds"]!;
                await using var db = database.CreateDbContext();
                var matches = await db.BaseItems.AsNoTracking().Where(item => item.Name!.Contains("fixture") && !exclusions.Contains(item.Id))
                    .OrderBy(item => item.Name).ToArrayAsync();
                response = new QueryResult<BaseItemDto>(1, matches.Length,
                    matches.Skip(1).Take(1).Select(item => new BaseItemDto { Id = item.Id, Name = item.Name }).ToArray());
                return new ActionExecutedContext(action, [], new object()) { Result = new OkObjectResult(response) };
            });
            Assert.NotNull(response);
            Assert.Equal(3, response.TotalRecordCount);
            Assert.Equal(personal.Id, Assert.Single(response.Items).Id);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static BaseItemEntity Item(string name, bool managed, DateTime? premiere)
    {
        var item = new BaseItemEntity { Id = Guid.NewGuid(), Name = name + " fixture", Type = "MediaBrowser.Controller.Entities.Movies.Movie", PremiereDate = premiere };
        if (managed) item.Provider = [new BaseItemProvider { Item = item, ItemId = item.Id, ProviderId = "Siphon", ProviderValue = "movie:" + item.Id.ToString("N") }];
        return item;
    }
}
