using System.Text.Json;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Model.MediaSegments;

namespace Jellyfin.Plugin.Siphon.MediaSegments;

internal sealed record IntroDbIdentity(string ImdbId, bool IsMovie, int? Season, int? Episode)
{
    internal Uri RequestUri => new("https://api.introdb.app/segments?imdb_id=" + ImdbId
        + (IsMovie ? "&is_movie=true" : FormattableString.Invariant($"&season={Season}&episode={Episode}")));

    internal static bool IsValidImdb(string? value) => value is { Length: >= 9 and <= 12 }
        && value.StartsWith("tt", StringComparison.Ordinal)
        && value.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0;
}

internal sealed record IntroDbRange(long StartTicks, long EndTicks);
internal sealed record IntroDbData(IntroDbRange? Intro, IntroDbRange? Recap, IntroDbRange? Outro,
    IntroDbRange? PostCredits, bool UnsafePostCredits);
internal sealed record IntroDbRead(IntroDbData? Data, string Status, DateTimeOffset CheckedUtc, DateTimeOffset ExpiresUtc);

/// <summary>Strict millisecond parsing and half-open skip intervals; never clamp a bad range into validity.</summary>
internal static class IntroDbSegments
{
    internal static IntroDbData Parse(JsonElement root, IntroDbIdentity identity)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("imdb_id", out var imdb) || imdb.ValueKind != JsonValueKind.String
            || imdb.GetString() != identity.ImdbId) throw new JsonException("Unexpected segment identity.");
        if (identity.IsMovie)
        {
            if (!root.TryGetProperty("is_movie", out var movie) || movie.ValueKind != JsonValueKind.True)
                throw new JsonException("Unexpected media type.");
        }
        else if (!Number(root, "season", out var season) || season != identity.Season
            || !Number(root, "episode", out var episode) || episode != identity.Episode
            || (root.TryGetProperty("is_movie", out var movie) && movie.ValueKind != JsonValueKind.False))
        {
            throw new JsonException("Unexpected episode identity.");
        }
        if (root.TryGetProperty("media_type", out var mediaType)
            && (mediaType.ValueKind != JsonValueKind.String || mediaType.GetString() != (identity.IsMovie ? "movie" : "tv")))
            throw new JsonException("Unexpected media type.");

        var postCredits = Range(root, "post_credits", out var unsafePostCredits);
        return new(Range(root, "intro", out _), Range(root, "recap", out _), Range(root, "outro", out _),
            postCredits, unsafePostCredits);
    }

    internal static bool WithinRuntime(IntroDbRange? range, long? runtime) => range is not null && runtime is > 0
        && range.StartTicks >= 0 && range.StartTicks < range.EndTicks && range.EndTicks <= runtime.Value;

    internal static IReadOnlyList<MediaSegmentDto> Map(Guid itemId, IntroDbData? data, long? runtime,
        IReadOnlyCollection<string> enabled)
    {
        if (data is null || runtime is not > 0 || data.UnsafePostCredits
            || (data.PostCredits is not null && !WithinRuntime(data.PostCredits, runtime))) return [];
        var result = new List<MediaSegmentDto>(4);
        Add(data.Intro, "intro", MediaSegmentType.Intro);
        Add(data.Recap, "recap", MediaSegmentType.Recap);
        Add(data.Outro, "outro", MediaSegmentType.Outro);
        return result;

        void Add(IntroDbRange? range, string name, MediaSegmentType type)
        {
            if (!enabled.Contains(name, StringComparer.Ordinal) || !WithinRuntime(range, runtime)) return;
            var valid = range!;
            // A post-credit scene is protected even when its chapter is disabled. Protect it
            // from every skip type: crowdsourced intros can also overlap the end of a movie.
            if (data.PostCredits is { } scene && valid.StartTicks < scene.EndTicks && valid.EndTicks > scene.StartTicks)
            {
                AddPart(valid.StartTicks, Math.Min(valid.EndTicks, scene.StartTicks), type);
                AddPart(Math.Max(valid.StartTicks, scene.EndTicks), valid.EndTicks, type);
            }
            else AddPart(valid.StartTicks, valid.EndTicks, type);
        }

        void AddPart(long start, long end, MediaSegmentType type)
        {
            if (start >= end) return;
            result.Add(new MediaSegmentDto { Id = Guid.NewGuid(), ItemId = itemId, Type = type, StartTicks = start, EndTicks = end });
        }
    }

    private static IntroDbRange? Range(JsonElement root, string property, out bool malformed)
    {
        malformed = false;
        if (!root.TryGetProperty(property, out var segment) || segment.ValueKind == JsonValueKind.Null) return null;
        if (segment.ValueKind != JsonValueKind.Object || !Number(segment, "start_ms", out var start)
            || !Number(segment, "end_ms", out var end) || start < 0 || end <= start
            || end > long.MaxValue / TimeSpan.TicksPerMillisecond)
        {
            malformed = true;
            return null;
        }
        return new(start * TimeSpan.TicksPerMillisecond, end * TimeSpan.TicksPerMillisecond);
    }

    private static bool Number(JsonElement root, string property, out long value)
    {
        value = 0;
        return root.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out value);
    }
}
