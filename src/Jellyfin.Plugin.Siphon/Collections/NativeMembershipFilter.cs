using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Jellyfin.Plugin.Siphon.Collections;

/// <summary>Adapts only native Siphon additions; Jellyfin remains responsible for native policy, shares and ordering.</summary>
public sealed class NativeMembershipFilter(
    ConfigurationAccessor configuration,
    IAuthorizationContext authorization,
    IUserManager users,
    ILibraryManager library,
    IPlaylistManager playlists,
    IItemPersistenceService persistence,
    ISiphonStateStore state,
    CollectionRetentionService membership,
    CatalogCollectionService catalogs,
    LibraryMaterializer materializer) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (HttpMethods.IsDelete(context.HttpContext.Request.Method)
            && context.ActionDescriptor is ControllerActionDescriptor
            { ControllerName: "Collection", ActionName: "RemoveFromCollection" } removal
            && removal.ControllerTypeInfo.Assembly.GetName().Name == "Jellyfin.Api")
        {
            await RemoveDeliberateAsync(context, next).ConfigureAwait(false);
            return;
        }
        if (!HttpMethods.IsPost(context.HttpContext.Request.Method)
            || context.ActionDescriptor is not ControllerActionDescriptor action
            || action.ControllerTypeInfo.Assembly.GetName().Name != "Jellyfin.Api"
            || (action.ControllerName, action.ActionName) is not
                (("Collection", "CreateCollection" or "AddToCollection") or
                 ("Playlists", "CreatePlaylist" or "AddItemToPlaylist" or "UpdatePlaylist")))
        {
            await next().ConfigureAwait(false);
            return;
        }
        var update = action.ActionName == "UpdatePlaylist";
        var bodyName = update ? "updatePlaylistRequest" : "createPlaylistRequest";
        context.ActionArguments.TryGetValue(bodyName, out var body);
        var bodyIds = body?.GetType().GetProperty("Ids");
        context.ActionArguments.TryGetValue("ids", out var argument);
        var ids = ReadIds(argument);
        var useBody = ids.Length == 0 && bodyIds is not null;
        if (useBody) ids = ReadIds(bodyIds!.GetValue(body));
        var generatedCollection = action.ControllerName == "Collection"
            && context.ActionArguments.TryGetValue("collectionId", out var requestedCollection) && requestedCollection is Guid requestedCollectionId
            && library.GetItemById(requestedCollectionId) is BoxSet box
            && !string.IsNullOrEmpty(box.GetProviderId(CatalogCollectionService.OwnerProvider));
        if (ids.Length == 0 || (!generatedCollection && !ids.Any(id => membership.ContentKey(library.GetItemById(id)) is not null)))
        {
            await next().ConfigureAwait(false);
            return;
        }
        var authenticated = (await authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false)).User;
        if (authenticated is null)
        {
            await next().ConfigureAwait(false);
            return;
        }
        var userId = authenticated.Id;
        if (!update && action.ControllerName == "Playlists")
        {
            var requested = context.ActionArguments.TryGetValue("userId", out var value) && value is Guid id ? id
                : body?.GetType().GetProperty("UserId")?.GetValue(body) is Guid bodyUser ? bodyUser : Guid.Empty;
            if (requested != Guid.Empty && requested != authenticated.Id)
            {
                // Leave native impersonation rejection untouched, and do not perform any plugin side effect first.
                if (!context.HttpContext.User.IsInRole("Administrator")) { await next().ConfigureAwait(false); return; }
                userId = requested;
            }
        }
        var user = userId == authenticated.Id ? authenticated : users.GetUserById(userId);
        if (user is null) { await next().ConfigureAwait(false); return; }
        var cancellation = context.HttpContext.RequestAborted;
        await configuration.SynchronizationGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            Folder? target = null;
            if (context.ActionArguments.TryGetValue("playlistId", out var playlistValue) && playlistValue is Guid playlistId)
            {
                var playlist = playlists.GetPlaylistForUser(playlistId, userId);
                if (playlist is null || playlist.OwnerUserId != userId && !playlist.Shares.Any(share => share.UserId == userId && share.CanEdit))
                { await next().ConfigureAwait(false); return; }
                target = membership.LoadMembership(playlist);
            }
            else if (context.ActionArguments.TryGetValue("collectionId", out var collectionValue) && collectionValue is Guid collectionId)
            {
                if (library.GetItemById(collectionId) is not BoxSet collection) { await next().ConfigureAwait(false); return; }
                target = membership.LoadMembership(collection);
            }
            var resolved = ids.Select(membership.Canonicalize).ToArray();
            var managed = resolved.Where(item => membership.ContentKey(item) is not null).OfType<BaseItem>().ToArray();
            if (managed.Length == 0) { await next().ConfigureAwait(false); return; }
            if (!Accessible(managed, user))
            {
                context.Result = new ForbidResult();
                return;
            }
            var existing = !update && target is not null ? target.GetLinkedChildrenInfos().Select(link => link.Item2.Id) : [];
            var normalized = membership.Normalize(ids, existing);
            if (useBody) bodyIds!.SetValue(body, normalized);
            else context.ActionArguments["ids"] = argument is string[]? normalized.Select(id => id.ToString("N")).ToArray() : normalized;

            // Expiry/cleanup cannot interleave between a successful native mutation and durable ownership.
            var executed = await next().ConfigureAwait(false);
            if (!Succeeded(executed)) return;
            if (target is null && executed.Result is ObjectResult result)
            {
                var createdId = result.Value?.GetType().GetProperty("Id")?.GetValue(result.Value);
                if (Guid.TryParse(createdId?.ToString(), out var nativeId)) target = library.GetItemById(nativeId) as Folder;
            }
            if (target is null || library.GetItemById(target.Id) is not Folder currentTarget) return;
            target = membership.LoadMembership(currentTarget);
            // Once native success is observable, finish the durable commit even if the client disconnects.
            await configuration.MutationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                CanonicalizeStoredMembership(target);
                var actual = target.GetLinkedChildrenInfos().Select(link => link.Item2).ToArray();
                var actualIds = actual.Select(item => item.Id).ToHashSet();
                var requestedIds = managed.Select(item => item.Id).Where(actualIds.Contains).ToArray();
                await catalogs.RecordDeliberateAsync(target, requestedIds, CancellationToken.None).ConfigureAwait(false);
                var requestedKeys = managed.Select(membership.ContentKey).OfType<string>().ToHashSet(StringComparer.Ordinal);
                var contentKeys = actual.Select(membership.ContentKey).OfType<string>().Where(requestedKeys.Contains).ToHashSet(StringComparer.Ordinal);
                await PromoteAsync(contentKeys).ConfigureAwait(false);
            }
            finally { configuration.MutationGate.Release(); }
        }
        finally { configuration.SynchronizationGate.Release(); }
    }

    private async Task RemoveDeliberateAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!context.ActionArguments.TryGetValue("collectionId", out var value) || value is not Guid id
            || library.GetItemById(id) is not BoxSet box || string.IsNullOrEmpty(box.GetProviderId(CatalogCollectionService.OwnerProvider)))
        { await next().ConfigureAwait(false); return; }
        context.ActionArguments.TryGetValue("ids", out var requested);
        var ids = ReadIds(requested).Select(id => membership.Canonicalize(id)?.Id ?? id).Distinct().ToArray();
        context.ActionArguments["ids"] = ids;
        await configuration.SynchronizationGate.WaitAsync(context.HttpContext.RequestAborted).ConfigureAwait(false);
        try
        {
            var executed = await next().ConfigureAwait(false);
            if (!Succeeded(executed)) return;
            await configuration.MutationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await catalogs.ForgetDeliberateAsync(box, ids, CancellationToken.None).ConfigureAwait(false); }
            finally { configuration.MutationGate.Release(); }
        }
        finally { configuration.SynchronizationGate.Release(); }
    }

    private bool Accessible(IReadOnlyList<BaseItem> items, User user)
    {
        var wanted = items.Select(item => item.Id).ToHashSet();
        var query = new InternalItemsQuery(user) { Recursive = true, GroupByPresentationUniqueKey = false };
        library.ConfigureUserAccess(query, user);
        query.ItemIds = wanted.ToArray();
        wanted.ExceptWith(library.GetItemList(query).Select(item => item.Id));
        return wanted.Count == 0;
    }

    private void CanonicalizeStoredMembership(Folder target)
    {
        var links = new List<LinkedChild>(target.LinkedChildren.Length);
        var seen = new HashSet<Guid>();
        var changed = false;
        var resolved = target.GetLinkedChildrenInfos().ToDictionary(link => link.Item1, link => link.Item2);
        foreach (var link in target.LinkedChildren)
        {
            var original = link.ItemId is { } id ? library.GetItemById(id) : resolved.GetValueOrDefault(link);
            var canonical = original is null ? null : membership.Canonicalize(original.Id);
            if (membership.ContentKey(canonical) is null) { links.Add(link); continue; }
            if (!seen.Add(canonical!.Id)) { changed = true; continue; }
            if (link.ItemId != canonical.Id)
            {
                links.Add(new LinkedChild { ItemId = canonical.Id, Type = link.Type });
                changed = true;
            }
            else links.Add(link);
        }
        if (!changed) return;
        target.LinkedChildren = links.ToArray();
        if (target is BoxSet box) box.LibraryFolderIds = null;
        target.DateLastSaved = DateTime.UtcNow;
        persistence.SaveItems([target], CancellationToken.None);
        library.RegisterItem(target);
        if (target is Playlist playlist) playlists.SavePlaylistFile(playlist);
    }

    private async Task PromoteAsync(HashSet<string> contentKeys)
    {
        if (contentKeys.Count == 0) return;
        var previous = state.GetItems();
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var retained = previous.Select(item =>
        {
            if (!contentKeys.Contains(item.ContentKey) || !item.IsSearchPreview
                && !item.Owners.Any(owner => owner.StartsWith(CatalogLibraryService.PreviewPrefix, StringComparison.Ordinal))) return item;
            changed.Add(item.Key);
            return item with
            {
                Owners = item.Owners.Where(owner => !owner.StartsWith(CatalogLibraryService.PreviewPrefix, StringComparison.Ordinal))
                    .Append(CatalogLibraryService.ManualPrefix + item.Type).Distinct(StringComparer.Ordinal).ToArray(),
                PreviewExpiresUtc = null,
                IsSearchPreview = item.Type == "series" && item.Season is null,
                MissingSinceUtc = null,
                MissingOwners = []
            };
        }).ToArray();
        if (changed.Count == 0) return;
        await state.SaveAsync(retained, CancellationToken.None).ConfigureAwait(false);
        await materializer.ApplyAsync(retained, CancellationToken.None, changedKeys: changed,
            scope: new LibraryMaterializer.PublicationScope(contentKeys, previous)).ConfigureAwait(false);
    }

    private static bool Succeeded(ActionExecutedContext executed)
        => !executed.Canceled && executed.Exception is null
            && executed.Result is IStatusCodeActionResult { StatusCode: null or >= 200 and < 300 };

    private static Guid[] ReadIds(object? value)
        => value switch
        {
            IEnumerable<Guid> ids => ids.ToArray(),
            string[] ids when ids.All(id => Guid.TryParse(id, out _)) => ids.Select(Guid.Parse).ToArray(),
            _ => []
        };
}
