using Jellyfin.Plugin.Siphon.Calendar;
using Xunit;

namespace Jellyfin.Plugin.Siphon.Tests.Calendar;

public sealed class CalendarDateTests
{
    [Fact]
    public void CalendarDaysIncludeBothUtcBoundariesWithoutHostTimezoneConversion()
    {
        Assert.True(CalendarWindow.TryCreate("2026-09-17", "2026-09-17", DateTimeOffset.UtcNow, out var window));
        Assert.True(window.Contains(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero)));
        Assert.True(window.Contains(new DateTimeOffset(2026, 9, 18, 1, 59, 59, TimeSpan.FromHours(2))));
        Assert.False(window.Contains(new DateTimeOffset(2026, 9, 17, 1, 59, 59, TimeSpan.FromHours(2))));
        Assert.False(window.Contains(new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero)));
        var native = CalendarDates.Utc(new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Unspecified));
        Assert.Equal(TimeSpan.Zero, native.Offset);
        Assert.Equal(17, native.Day);
        Assert.True(CalendarDates.IsDateOnly("2026-09-17", native));
        Assert.False(CalendarDates.IsDateOnly("2026-09-17T00:00:00Z", native));
        Assert.False(CalendarDates.IsDateOnly("2026-09-18", native));
    }

    [Fact]
    public void WindowLimitCountsInclusiveDaysAndRejectsAmbiguousDateInputs()
    {
        var now = new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero);
        Assert.True(CalendarWindow.TryCreate("2026-01-01", "2026-03-31", now, out _));
        Assert.False(CalendarWindow.TryCreate("2026-01-01", "2026-04-01", now, out _));
        Assert.False(CalendarWindow.TryCreate("2026-09-18", "2026-09-17", now, out _));
        Assert.False(CalendarWindow.TryCreate("09/17/2026", null, now, out _));
        Assert.False(CalendarWindow.TryCreate("2026-09-17T23:00:00-07:00", null, now, out _));
        Assert.True(CalendarWindow.TryCreate("9999-12-31", null, now, out var last));
        Assert.Equal(DateOnly.MaxValue, last.To);
    }
}
