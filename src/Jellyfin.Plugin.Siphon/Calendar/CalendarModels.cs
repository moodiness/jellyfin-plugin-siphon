using System.Globalization;

namespace Jellyfin.Plugin.Siphon.Calendar;

public sealed record CalendarEpisode(Guid Id, Guid SeriesId, string SeriesName, string Name,
    int? SeasonNumber, int? EpisodeNumber, string Date, DateTimeOffset AnnouncedReleaseUtc,
    bool IsDateOnly, bool AnnouncedReleased);

public sealed record CalendarResponse(string From, string To, string TimeZone,
    IReadOnlyList<CalendarEpisode> Items, bool Truncated, string AvailabilityNote);

public sealed record CalendarNotification(Guid Id, string Kind, CalendarEpisode Episode,
    DateTimeOffset CreatedUtc, DateTimeOffset? ReadUtc, DateTimeOffset? PreviousAnnouncedReleaseUtc);

public sealed record CalendarInbox(IReadOnlyList<CalendarNotification> Items, int UnreadCount,
    bool NotificationsAllowed, bool NotificationsEnabled);

internal sealed record FollowedCalendarSnapshot(IReadOnlyList<CalendarEpisode> Episodes,
    IReadOnlySet<Guid> SeriesIds, bool Truncated);

internal readonly record struct CalendarWindow(DateOnly From, DateOnly To)
{
    public static bool TryCreate(string? from, string? to, DateTimeOffset now, out CalendarWindow window)
    {
        window = default;
        var first = DateOnly.FromDateTime(now.UtcDateTime);
        if (from is not null && !DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out first)) return false;
        var last = first.DayNumber <= DateOnly.MaxValue.DayNumber - 30 ? first.AddDays(30) : DateOnly.MaxValue;
        if (to is not null && !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out last)) return false;
        if (last < first || last.DayNumber - first.DayNumber >= 90) return false;
        window = new(first, last);
        return true;
    }

    public bool Contains(DateTimeOffset value)
    {
        var day = DateOnly.FromDateTime(value.UtcDateTime);
        return day >= From && day <= To;
    }

    public static string Format(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

internal static class CalendarDates
{
    // Jellyfin persists PremiereDate in UTC; unspecified values from its DB are
    // UTC, not the timezone of the plugin host. Never shift date-only air dates.
    public static DateTimeOffset Utc(DateTime value) => new(value.Kind == DateTimeKind.Local
        ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public static bool IsDateOnly(string? announced, DateTimeOffset native)
        => DateOnly.TryParseExact(announced, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            && native.TimeOfDay == TimeSpan.Zero && date == DateOnly.FromDateTime(native.UtcDateTime);

    public static string SafeTitle(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return new string(value.Where(character => !char.IsControl(character)
            && char.GetUnicodeCategory(character) != UnicodeCategory.Format).Take(240).ToArray()).Trim() is { Length: > 0 } clean ? clean : fallback;
    }
}
