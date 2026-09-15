using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Siphon.Channels;

/// <summary>Exposes synchronized Siphon catalogs as a virtual Jellyfin channel.</summary>
public sealed class SiphonChannel(
    ISiphonStateStore state,
    CapabilityTokenService tokens,
    ConfigurationAccessor configuration) : IChannel, IHasCacheKey, ISupportsLatestMedia
{
    public string Name => "Siphon";

    public string Description => "Stremio addons in Jellyfin";

    public string DataVersion => "1";

    public string HomePageUrl => string.Empty;

    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    public bool IsEnabledFor(string userId) => string.IsNullOrWhiteSpace(configuration.Current.MoviesLibraryPath)
        && string.IsNullOrWhiteSpace(configuration.Current.TvShowsLibraryPath);

    public IEnumerable<ImageType> GetSupportedChannelImages() => [];

    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken) =>
        throw new ArgumentException("Siphon does not provide a channel image.", nameof(type));

    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = [ChannelMediaType.Video],
        ContentTypes = [ChannelMediaContentType.Movie, ChannelMediaContentType.Episode],
        MaxPageSize = 500,
        AutoRefreshLevels = 0,
        SupportsContentDownloading = false
    };

    public string? GetCacheKey(string? userId)
    {
        var input = new StringBuilder();
        foreach (var addon in configuration.Current.Addons.Where(addon => addon.Enabled).OrderBy(addon => addon.Id, StringComparer.Ordinal))
        {
            input.Append(addon.Id).Append('\n').Append(addon.ManifestUrl).Append('\n');
            foreach (var catalog in addon.Catalogs.Where(catalog => catalog.Enabled).OrderBy(catalog => catalog.Key, StringComparer.Ordinal))
            {
                input.Append(catalog.Key).Append('\n');
            }
        }

        foreach (var item in state.GetItems().OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            input.Append(item.Key).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.ToString())));
    }

    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owner = query.FolderId;
        if (string.IsNullOrEmpty(owner))
        {
            return Task.FromResult(new ChannelItemResult
            {
                Items = CatalogFolders()
            });
        }

        var items = state.GetItems()
            .Where(item => item.Owners.Any(candidate => string.Equals(candidate, owner, StringComparison.Ordinal)))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToChannelItem)
            .ToArray();

        var start = Math.Clamp(query.StartIndex ?? 0, 0, items.Length);
        var limit = Math.Clamp(query.Limit ?? items.Length, 0, items.Length - start);
        return Task.FromResult(new ChannelItemResult
        {
            Items = items.Skip(start).Take(limit).ToArray(),
            TotalRecordCount = items.Length
        });
    }
    public Task<IEnumerable<ChannelItemInfo>> GetLatestMedia(ChannelLatestMediaSearch request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var activeOwners = configuration.Current.Addons
            .Where(addon => addon.Enabled)
            .SelectMany(addon => addon.Catalogs.Where(catalog => catalog.Enabled).Select(catalog => addon.Id + ":" + catalog.Key))
            .ToHashSet(StringComparer.Ordinal);
        var items = state.GetItems()
            .Where(item => item.Owners.Any(owner => activeOwners.Contains(owner)))
            .OrderByDescending(item => item.Released, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .Select(ToChannelItem)
            .ToArray();
        return Task.FromResult<IEnumerable<ChannelItemInfo>>(items);
    }

    private IReadOnlyList<ChannelItemInfo> CatalogFolders()
    {
        var configured = configuration.Current.Addons
            .Where(addon => addon.Enabled)
            .SelectMany(addon => addon.Catalogs.Where(catalog => catalog.Enabled).Select(catalog =>
                (Owner: addon.Id + ":" + catalog.Key,
                    Name: string.IsNullOrWhiteSpace(addon.DisplayName) ? addon.Id : addon.DisplayName,
                    Catalog: catalog)))
            .ToArray();

        return configured.Select(entry => new ChannelItemInfo
        {
            Id = entry.Owner,
            Name = entry.Name + " · " + entry.Catalog.Type + " · " + entry.Catalog.Id,
            Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.Container,
            MediaType = ChannelMediaType.Video
        }).ToArray();
    }

    private ChannelItemInfo ToChannelItem(ManagedItem item)
    {
        var source = new MediaSourceInfo
        {
            Id = item.Key,
            Name = item.Name,
            Path = configuration.Current.PublicBaseUrl.TrimEnd('/') + "/Siphon/s/" + tokens.SignItem(item.Key),
            Protocol = MediaProtocol.Http,
            IsRemote = true,
            Type = MediaSourceType.Default,
            VideoType = VideoType.VideoFile,
            SupportsDirectPlay = false,
            SupportsDirectStream = false,
            SupportsTranscoding = true,
            SupportsProbing = true
        };

        return new ChannelItemInfo
        {
            Id = item.Key,
            Name = item.Name,
            SeriesName = item.SeriesName,
            Type = ChannelItemType.Media,
            MediaType = ChannelMediaType.Video,
            ContentType = item.Type == "movie" ? ChannelMediaContentType.Movie : ChannelMediaContentType.Episode,
            Overview = item.Description,
            ImageUrl = string.IsNullOrWhiteSpace(item.PosterUrl) ? null : configuration.Current.PublicBaseUrl.TrimEnd('/') + "/Siphon/image/" + tokens.SignItem(item.Key),
            ProductionYear = item.Year,
            PremiereDate = DateTime.TryParse(item.Released, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var released) ? released : null,
            IndexNumber = item.Episode,
            ParentIndexNumber = item.Season,
            ProviderIds = new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase),
            MediaSources = [source]
        };
    }
}
