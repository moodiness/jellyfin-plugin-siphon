using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Siphon.MediaSegments;

/// <summary>Primes the optional timing cache for a single authorized native item detail response.</summary>
public sealed class IntroDbChapterResultFilter(
    IntroDbMediaSegmentProvider provider,
    IChapterRepository chapters,
    ILibraryManager library,
    IAuthorizationContext authorization,
    ILogger<IntroDbChapterResultFilter> logger) : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { Value: BaseItemDto dto } result
            && (result.StatusCode is null or >= 200 and < 300)
            && (dto.Chapters is not null || dto.MediaSources is not null))
        {
            var user = (await authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false)).User;
            if (user is not null && library.GetItemById<Video>(dto.Id, user) is { } item && provider.IsEnabled(item))
            {
                try
                {
                    var timings = await provider.ReadAsync(item, context.HttpContext.RequestAborted).ConfigureAwait(false);
                    if (timings.PostCredits is not null)
                    {
                        // Re-read through the projected repository so chapter image indices stay
                        // aligned with native GetChapter; no native chapter or image is changed.
                        dto.Chapters = chapters.GetChapters(item.Id).ToList();
                    }
                }
                catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    logger.LogWarning("Optional IntroDB chapters were unavailable for item {ItemId}", item.Id);
                }
            }
        }
        await next().ConfigureAwait(false);
    }
}
