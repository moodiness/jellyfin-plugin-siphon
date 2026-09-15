using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Siphon.Identity;

/// <summary>Materializes Siphon items as virtual Jellyfin media in existing libraries.</summary>
public sealed class LibraryMaterializer(
    ConfigurationAccessor configuration,
    CapabilityTokenService tokens,
    ISiphonStateStore state,
    ILibraryManager library,
    IItemPersistenceService persistence,
    ILogger<LibraryMaterializer> logger)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(configuration.Current.MoviesLibraryPath)
        || !string.IsNullOrWhiteSpace(configuration.Current.TvShowsLibraryPath);

    public void ValidateTargets()
    {
        ValidateTarget(configuration.Current.MoviesLibraryPath, CollectionTypeOptions.movies);
        ValidateTarget(configuration.Current.TvShowsLibraryPath, CollectionTypeOptions.tvshows);
    }

    public async Task ApplyAsync(IReadOnlyList<ManagedItem> retained, IReadOnlyList<ManagedItem> removals, CancellationToken cancellationToken)
    {
        ValidateTargets();
        var desired = retained.ToDictionary(item => item.Key, StringComparer.Ordinal);
        var previous = state.GetItems().ToDictionary(item => item.Key, StringComparer.Ordinal);
        var all = previous.Values.Concat(removals).Concat(retained).DistinctBy(item => item.Key).ToArray();

        foreach (var item in all)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = GetRoot(item.Type);
            if (root is null)
            {
                continue;
            }

            var current = FindMaterialized(root, item.Key);
            if (desired.TryGetValue(item.Key, out var wanted))
            {
                var materialized = ToBaseItem(wanted);
                materialized.Id = current?.Id ?? library.GetNewItemId(materialized.Path, materialized.GetType());
                materialized.ParentId = root.Id;
                materialized.PresentationUniqueKey = materialized.CreatePresentationUniqueKey();
                materialized.DateCreated = current?.DateCreated == DateTime.MinValue ? DateTime.UtcNow : current?.DateCreated ?? DateTime.UtcNow;
                if (current is null)
                {
                    library.CreateItem(materialized, root);
                }

                persistence.SaveItems([materialized], cancellationToken);
            }
            else if (current is not null)
            {
                library.DeleteItem(current, new DeleteOptions { DeleteFileLocation = false }, root, false);
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
        logger.LogInformation("Siphon materialized {Count} native home items", retained.Count);
    }

    private BaseItem ToBaseItem(ManagedItem item)
    {
        var path = configuration.Current.PublicBaseUrl.TrimEnd('/') + "/Siphon/s/" + tokens.SignItem(item.Key);
        BaseItem result = item.Type == "movie" ? new Movie() : new Episode();
        result.Name = item.Name;
        result.Path = path;
        result.IsVirtualItem = false;
        result.Overview = item.Description;
        result.ProductionYear = item.Year;
        result.PremiereDate = ParseDate(item.Released);
        result.DateModified = DateTime.UtcNow;
        result.DateLastSaved = DateTime.UtcNow;
        result.ProviderIds = new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase)
        {
            ["Siphon"] = item.Key
        };
        if (item.PosterUrl is { Length: > 0 })
        {
            result.SetImagePath(ImageType.Primary, configuration.Current.PublicBaseUrl.TrimEnd('/') + "/Siphon/image/" + tokens.SignItem(item.Key));
        }

        if (result is Episode episode)
        {
            episode.SeriesName = item.SeriesName;
            episode.IndexNumber = item.Episode;
            episode.ParentIndexNumber = item.Season;
        }

        return result;
    }

    private BaseItem? FindMaterialized(Folder root, string key) =>
        library.GetItemList(new InternalItemsQuery
        {
            ParentId = root.Id,
            Recursive = false,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            IsDeadPerson = true
        }).FirstOrDefault(item => string.Equals(item.GetProviderId("Siphon"), key, StringComparison.Ordinal));

    private Folder? GetRoot(string type)
    {
        var path = type == "movie" ? configuration.Current.MoviesLibraryPath : configuration.Current.TvShowsLibraryPath;
        if (string.IsNullOrWhiteSpace(path)) return null;
        var view = library.GetVirtualFolders().FirstOrDefault(candidate => candidate.Locations.Any(location => Path.GetFullPath(location).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)));
        return Guid.TryParse(view?.ItemId, out var id) ? library.GetItemById<Folder>(id) : library.FindByPath(path, true) as Folder;
    }

    private void ValidateTarget(string path, CollectionTypeOptions type)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException($"The Siphon {type} target must be an absolute existing Jellyfin library path.");
        var collection = library.GetVirtualFolders().FirstOrDefault(view => view.Locations.Any(location => Path.GetFullPath(location).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)));
        if (collection is null) throw new InvalidOperationException($"Add the existing {type} library path to Siphon before synchronizing.");
        if (collection.CollectionType != type) throw new InvalidOperationException($"The configured Siphon target must belong to an existing {type} Jellyfin library.");
        if (!Guid.TryParse(collection.ItemId, out var collectionId) || library.GetItemById<Folder>(collectionId) is null) throw new InvalidOperationException($"Jellyfin has not indexed the existing {type} library yet. Run a library scan and retry.");
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var date) ? date : null;
}
