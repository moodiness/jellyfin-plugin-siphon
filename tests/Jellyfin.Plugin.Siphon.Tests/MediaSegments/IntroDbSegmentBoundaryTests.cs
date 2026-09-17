using System.Text.Json;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Siphon.MediaSegments;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.MediaSegments;

public sealed class IntroDbSegmentBoundaryTests
{
    [Fact]
    public void CreditSkipStopsAtSceneAndResumesAfterItEvenWhenSceneChapterIsDisabled()
    {
        var data = new IntroDbData(null, null, Seconds(100, 200), Seconds(130, 160), false);
        var segments = IntroDbSegments.Map(Guid.NewGuid(), data, TimeSpan.FromSeconds(220).Ticks, ["outro"]);

        Assert.Collection(segments,
            first => AssertRange(first, MediaSegmentType.Outro, 100, 130),
            second => AssertRange(second, MediaSegmentType.Outro, 160, 200));
    }

    [Fact]
    public void ASceneProtectsAgainstMisclassifiedIntrosAndRecapsToo()
    {
        var data = new IntroDbData(Seconds(110, 150), Seconds(140, 180), Seconds(150, 160), Seconds(130, 170), false);
        var segments = IntroDbSegments.Map(Guid.NewGuid(), data, TimeSpan.FromSeconds(200).Ticks, ["intro", "recap", "outro"]);

        Assert.Collection(segments,
            first => AssertRange(first, MediaSegmentType.Intro, 110, 130),
            second => AssertRange(second, MediaSegmentType.Recap, 170, 180));
    }

    [Fact]
    public void RuntimeIsCheckedForEachNativeVersionWithoutClampingTheCachedRange()
    {
        var data = new IntroDbData(Seconds(0, 15), null, Seconds(90, 120), null, false);
        var item = Guid.NewGuid();
        var shortCut = IntroDbSegments.Map(item, data, TimeSpan.FromSeconds(100).Ticks, ["intro", "outro"]);
        var fullCut = IntroDbSegments.Map(item, data, TimeSpan.FromSeconds(120).Ticks, ["intro", "outro"]);

        AssertRange(Assert.Single(shortCut), MediaSegmentType.Intro, 0, 15);
        Assert.Collection(fullCut,
            first => AssertRange(first, MediaSegmentType.Intro, 0, 15),
            second => AssertRange(second, MediaSegmentType.Outro, 90, 120));
        Assert.Empty(IntroDbSegments.Map(item, data, null, ["intro", "outro"]));
    }

    [Theory]
    [InlineData("{\"start_ms\":-1,\"end_ms\":1000}")]
    [InlineData("{\"start_ms\":1000,\"end_ms\":1000}")]
    [InlineData("{\"start_ms\":1.5,\"end_ms\":1000}")]
    [InlineData("{\"start_ms\":0,\"end_ms\":9223372036854775807}")]
    [InlineData("{\"start_sec\":0,\"end_sec\":1}")]
    [InlineData("{\"start_ms\":\"0\",\"end_ms\":1000}")]
    public void MalformedSkipRangesCannotBecomeNativeSegments(string range)
    {
        var data = ParseMovie("\"intro\":" + range + ",\"outro\":null,\"post_credits\":null");
        Assert.Empty(IntroDbSegments.Map(Guid.NewGuid(), data, TimeSpan.FromHours(2).Ticks, ["intro"]));
    }

    [Fact]
    public void MalformedOrOutOfRuntimeSceneSuppressesSkipsRatherThanDiscardingItsProtection()
    {
        var invalid = ParseMovie("\"outro\":{\"start_ms\":100000,\"end_ms\":200000},\"post_credits\":{\"start_ms\":-1,\"end_ms\":180000}");
        var beyondRuntime = new IntroDbData(null, null, Seconds(100, 200), Seconds(190, 300), false);

        Assert.Empty(IntroDbSegments.Map(Guid.NewGuid(), invalid, TimeSpan.FromSeconds(220).Ticks, ["outro"]));
        Assert.Empty(IntroDbSegments.Map(Guid.NewGuid(), beyondRuntime, TimeSpan.FromSeconds(220).Ticks, ["outro"]));
    }

    [Fact]
    public void IdentityMismatchCannotApplyAnotherEpisodesTimings()
    {
        using var document = JsonDocument.Parse("""
            {"imdb_id":"tt0903747","media_type":"tv","is_movie":false,"season":1,"episode":2,
             "intro":{"start_ms":0,"end_ms":30000},"recap":null,"outro":null,"post_credits":null}
            """);
        Assert.Throws<JsonException>(() => IntroDbSegments.Parse(document.RootElement, new("tt0903747", false, 1, 1)));
        var correct = IntroDbSegments.Parse(document.RootElement, new("tt0903747", false, 1, 2));
        AssertRange(Assert.Single(IntroDbSegments.Map(Guid.NewGuid(), correct, TimeSpan.FromMinutes(45).Ticks, ["intro"])),
            MediaSegmentType.Intro, 0, 30);
    }

    [Fact]
    public void ExistingNativeTimingsWinWithoutMutatingThem()
    {
        var native = new MediaSegmentDto { Id = Guid.NewGuid(), Type = MediaSegmentType.Intro, StartTicks = 5, EndTicks = 10 };
        var conflicting = new MediaSegmentDto { Type = MediaSegmentType.Intro, StartTicks = 0, EndTicks = 4 };
        var overlapping = new MediaSegmentDto { Type = MediaSegmentType.Recap, StartTicks = 9, EndTicks = 12 };
        var compatible = new MediaSegmentDto { Type = MediaSegmentType.Outro, StartTicks = 50, EndTicks = 100 };

        var merged = NativeIntroDbMediaSegmentManager.Merge([native], [conflicting, overlapping, compatible], null);
        Assert.Collection(merged, first => Assert.Same(native, first), second => Assert.Same(compatible, second));
        Assert.Same(native, Assert.Single(NativeIntroDbMediaSegmentManager.Merge([native], [compatible], [MediaSegmentType.Intro])));
    }

    [Fact]
    public void GeneratedChapterIsNavigableBetweenNativeChaptersButIsNeverSavedAsAUserChapter()
    {
        var before = new ChapterInfo { Name = "Credits", StartPositionTicks = Seconds(100, 101).StartTicks, ImagePath = "before.jpg", ImageTag = "before" };
        var after = new ChapterInfo { Name = "Post-credit scene (IntroDB)", StartPositionTicks = Seconds(190, 191).StartTicks, ImagePath = "after.jpg", ImageTag = "after" };
        var merged = IntroDbChapterRepository.Merge([before, after], Seconds(150, 170));

        Assert.Collection(merged,
            chapter => Assert.Same(before, chapter),
            chapter => Assert.Equal(TimeSpan.FromSeconds(150).Ticks, chapter.StartPositionTicks),
            chapter => Assert.Same(after, chapter));
        var saved = IntroDbChapterRepository.NativeOnly(merged);
        Assert.Collection(saved, chapter => Assert.Same(before, chapter), chapter => Assert.Same(after, chapter));
        Assert.Same(after, IntroDbChapterRepository.Merge([before, after], Seconds(190, 200))[1]);
    }

    [Fact]
    public void DisabledTypesDoNotEmitSegmentsAndPostCreditsNeverBecomesASkipType()
    {
        var data = new IntroDbData(Seconds(0, 15), Seconds(20, 30), Seconds(90, 120), Seconds(100, 110), false);
        var runtime = TimeSpan.FromSeconds(120).Ticks;
        Assert.Empty(IntroDbSegments.Map(Guid.NewGuid(), data, runtime, ["post-credits"]));
        AssertRange(Assert.Single(IntroDbSegments.Map(Guid.NewGuid(), data, runtime, ["recap"])), MediaSegmentType.Recap, 20, 30);
    }

    private static IntroDbRange Seconds(int start, int end) => new(TimeSpan.FromSeconds(start).Ticks, TimeSpan.FromSeconds(end).Ticks);

    private static IntroDbData ParseMovie(string properties)
    {
        using var document = JsonDocument.Parse("{\"imdb_id\":\"tt0371746\",\"media_type\":\"movie\",\"is_movie\":true," + properties + "}");
        return IntroDbSegments.Parse(document.RootElement, new("tt0371746", true, null, null));
    }

    private static void AssertRange(MediaSegmentDto segment, MediaSegmentType type, int start, int end)
    {
        Assert.Equal(type, segment.Type);
        Assert.Equal(TimeSpan.FromSeconds(start).Ticks, segment.StartTicks);
        Assert.Equal(TimeSpan.FromSeconds(end).Ticks, segment.EndTicks);
    }
}
