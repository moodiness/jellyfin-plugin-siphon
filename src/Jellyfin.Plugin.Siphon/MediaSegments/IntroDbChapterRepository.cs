using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Siphon.MediaSegments;

/// <summary>A reversible chapter projection. Native/user chapter rows are never owned or rewritten by IntroDB.</summary>
public sealed class IntroDbChapterRepository(IChapterRepository inner, IntroDbMediaSegmentProvider provider) : IChapterRepository
{
    public IReadOnlyList<ChapterInfo> GetChapters(Guid baseItemId)
        => Merge(inner.GetChapters(baseItemId), provider.CachedPostCredits(baseItemId));

    public ChapterInfo? GetChapter(Guid baseItemId, int index)
    {
        // Image endpoints must see the same indices as the augmented chapter list.
        var chapters = GetChapters(baseItemId);
        return index >= 0 && index < chapters.Count ? chapters[index] : null;
    }

    public void SaveChapters(Guid itemId, IReadOnlyList<ChapterInfo> chapters)
        => inner.SaveChapters(itemId, NativeOnly(chapters));

    public Task DeleteChaptersAsync(Guid itemId, CancellationToken cancellationToken)
        => inner.DeleteChaptersAsync(itemId, cancellationToken);

    internal static IReadOnlyList<ChapterInfo> Merge(IReadOnlyList<ChapterInfo> chapters, IntroDbRange? scene)
    {
        if (scene is null || chapters.Any(chapter => chapter.StartPositionTicks == scene.StartTicks)) return chapters;
        return chapters.Append(new GeneratedChapter
        {
            Name = "Post-credit scene (IntroDB)",
            StartPositionTicks = scene.StartTicks
        }).OrderBy(chapter => chapter.StartPositionTicks).ToArray();
    }

    internal static IReadOnlyList<ChapterInfo> NativeOnly(IReadOnlyList<ChapterInfo> chapters)
        => chapters.Any(chapter => chapter is GeneratedChapter) ? chapters.Where(chapter => chapter is not GeneratedChapter).ToArray() : chapters;

    private sealed class GeneratedChapter : ChapterInfo { }
}
