using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.MediaSegments;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.Siphon.MediaSegments;

/// <summary>Applies live timing settings without erasing any other provider's saved segments.</summary>
public sealed class NativeIntroDbMediaSegmentManager(
    IMediaSegmentManager inner,
    IntroDbMediaSegmentProvider provider,
    IDbContextFactory<JellyfinDbContext> database,
    IHttpContextAccessor httpContext) : IMediaSegmentManager
{
    public async Task<IEnumerable<MediaSegmentDto>> GetSegmentsAsync(BaseItem item,
        IEnumerable<MediaSegmentType>? typeFilter, LibraryOptions libraryOptions, bool filterByProvider = true)
    {
        var filter = typeFilter?.ToArray();
        var native = (await inner.GetSegmentsAsync(item, filter, libraryOptions, filterByProvider).ConfigureAwait(false)).ToArray();
        if (item is not Video video || !provider.IsManaged(item)) return native;
        var ct = httpContext.HttpContext?.RequestAborted ?? CancellationToken.None;
        await using var db = await database.CreateDbContextAsync(ct).ConfigureAwait(false);
        var ownedIds = await db.MediaSegments.AsNoTracking()
            .Where(segment => segment.ItemId == item.Id && segment.SegmentProviderId == IntroDbMediaSegmentProvider.ProviderId)
            .Select(segment => segment.Id).ToArrayAsync(ct).ConfigureAwait(false);
        var owned = ownedIds.ToHashSet();
        var retained = native.Where(segment => !owned.Contains(segment.Id)).ToArray();
        if (!provider.IsEnabled(item)) return retained;
        var generated = await provider.ReadAsync(video, ct).ConfigureAwait(false);
        return Merge(retained, generated.Segments, filter);
    }

    internal static IReadOnlyList<MediaSegmentDto> Merge(IReadOnlyList<MediaSegmentDto> native,
        IReadOnlyList<MediaSegmentDto> generated, IReadOnlyCollection<MediaSegmentType>? filter)
    {
        // Existing native timings are authoritative. Do not add a second competing skip for
        // the same type or overlap another type supplied by a user or a different provider.
        var result = new List<MediaSegmentDto>(native);
        foreach (var segment in generated)
        {
            if (filter is not null && !filter.Contains(segment.Type)) continue;
            if (native.Any(existing => existing.Type == segment.Type
                || (existing.StartTicks < segment.EndTicks && existing.EndTicks > segment.StartTicks))) continue;
            result.Add(segment);
        }
        return result.OrderBy(segment => segment.StartTicks).ToArray();
    }

    public bool HasSegments(Guid itemId) => inner.HasSegments(itemId) || provider.MayHaveSegments(itemId);
    public bool IsTypeSupported(BaseItem baseItem) => inner.IsTypeSupported(baseItem);
    public IEnumerable<(string Name, string Id)> GetSupportedProviders(BaseItem item) => inner.GetSupportedProviders(item);
    public Task RunSegmentPluginProviders(BaseItem baseItem, LibraryOptions libraryOptions, bool forceOverwrite, CancellationToken cancellationToken)
        => inner.RunSegmentPluginProviders(baseItem, libraryOptions, forceOverwrite, cancellationToken);
    public Task<MediaSegmentDto> CreateSegmentAsync(MediaSegmentDto mediaSegment, string segmentProviderId)
        => inner.CreateSegmentAsync(mediaSegment, segmentProviderId);
    public Task DeleteSegmentAsync(Guid segmentId) => inner.DeleteSegmentAsync(segmentId);
    public Task DeleteSegmentsAsync(Guid itemId, CancellationToken cancellationToken)
        => inner.DeleteSegmentsAsync(itemId, cancellationToken);
}
