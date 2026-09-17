using Jellyfin.Plugin.Siphon.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Siphon.MediaSegments;

[ApiController]
[Authorize]
[Route("Siphon/Items/{itemId:guid}/Segments")]
public sealed class IntroDbSegmentsController(
    IntroDbMediaSegmentProvider provider,
    IMediaSegmentManager segments,
    ILibraryManager library,
    IAuthorizationContext authorization,
    ConfigurationAccessor configuration) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IntroDbSegmentInspection>> Get(Guid itemId, CancellationToken cancellationToken)
    {
        var user = (await authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).User;
        if (user is null) return Unauthorized();
        if (library.GetItemById<Video>(itemId, user) is not { } item || !provider.IsManaged(item)) return NotFound();
        var result = await provider.ReadAsync(item, cancellationToken).ConfigureAwait(false);
        var enabled = configuration.Current.EnableIntroDb ? configuration.Current.IntroDbSegments ?? [] : [];
        var timings = new List<IntroDbTiming>(4);
        if (result.Response?.Data is { } data)
        {
            Add("intro", data.Intro, "NativeSegment");
            Add("recap", data.Recap, "NativeSegment");
            Add("outro", data.Outro, "NativeSegment");
            Add("post-credits", data.PostCredits, "ProtectedChapter");
        }
        var native = await segments.GetSegmentsAsync(item, null, library.GetLibraryOptions(item)).ConfigureAwait(false);
        return new IntroDbSegmentInspection(item.Id, result.Status, item.RunTimeTicks,
            result.Response?.CheckedUtc, result.Response?.ExpiresUtc, timings,
            native.Select(segment => new IntroDbNativeTiming(segment.Type.ToString(), segment.StartTicks, segment.EndTicks)).ToArray());

        void Add(string type, IntroDbRange? range, string mapping)
        {
            if (!IntroDbSegments.WithinRuntime(range, item.RunTimeTicks)) return;
            timings.Add(new(type, range!.StartTicks, range.EndTicks, enabled.Contains(type, StringComparer.Ordinal), mapping));
        }
    }
}

public sealed record IntroDbSegmentInspection(Guid ItemId, string Status, long? RunTimeTicks,
    DateTimeOffset? CheckedUtc, DateTimeOffset? ExpiresUtc, IReadOnlyList<IntroDbTiming> Timings,
    IReadOnlyList<IntroDbNativeTiming> NativeSegments);
public sealed record IntroDbTiming(string Type, long StartTicks, long EndTicks, bool Enabled, string Mapping);
public sealed record IntroDbNativeTiming(string Type, long StartTicks, long EndTicks);
