using Zephyr.Api.Common;

namespace Zephyr.Api.Tests.Common;

public class BusinessClockTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static BusinessClock At(string utc) => new(new FixedTime(DateTimeOffset.Parse(utc)));

    [Theory]
    [InlineData("2026-11-02T22:59:59Z", "2026-11-02")] // 23:59:59 in Stockholm (CET, UTC+1)
    [InlineData("2026-11-02T23:00:00Z", "2026-11-03")] // midnight in Stockholm
    [InlineData("2026-07-01T21:59:59Z", "2026-07-01")] // 23:59:59 (CEST, UTC+2)
    [InlineData("2026-07-01T22:00:00Z", "2026-07-02")]
    [InlineData("2026-03-28T22:59:59Z", "2026-03-28")] // the evening before DST starts
    [InlineData("2026-03-28T23:00:00Z", "2026-03-29")] // 00:00 on the day DST starts
    [InlineData("2026-10-24T22:00:00Z", "2026-10-25")] // 00:00 CEST on the day DST ends (a 25-hour day)
    [InlineData("2026-10-25T22:59:59Z", "2026-10-25")] // 23:59:59 CET, same day, 25 hours later
    [InlineData("2026-10-25T23:00:00Z", "2026-10-26")]
    public void Today_is_the_Stockholm_date(string utc, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), At(utc).Today());

    [Fact]
    public void IsFuture_follows_the_Stockholm_date_not_the_UTC_date()
    {
        var beforeMidnight = At("2026-11-02T22:30:00Z"); // 23:30 on 2 Nov in Stockholm
        Assert.False(beforeMidnight.IsFuture(new DateOnly(2026, 11, 2)));
        Assert.True(beforeMidnight.IsFuture(new DateOnly(2026, 11, 3)));

        var afterMidnight = At("2026-11-02T23:30:00Z"); // 00:30 on 3 Nov in Stockholm, still 2 Nov in UTC
        Assert.False(afterMidnight.IsFuture(new DateOnly(2026, 11, 3)));
        Assert.True(afterMidnight.IsFuture(new DateOnly(2026, 11, 4)));
    }

    [Theory]
    [InlineData("2026-11-01", 2026, 44)] // Sunday ends the week
    [InlineData("2026-11-02", 2026, 45)] // Monday starts the next
    [InlineData("2025-12-29", 2026, 1)] // belongs to week 1 of the next year
    [InlineData("2027-01-01", 2026, 53)] // belongs to the last week of the previous year
    public void IsoWeek_uses_ISO_8601(string date, int year, int week) =>
        Assert.Equal((year, week), At("2026-11-02T12:00:00Z").IsoWeek(DateOnly.Parse(date)));

    [Fact]
    public void PeriodOf_is_the_calendar_month()
    {
        var clock = At("2026-11-02T12:00:00Z");
        Assert.Equal("2026-11", clock.PeriodOf(new DateOnly(2026, 11, 30)));
        Assert.Equal("2027-01", clock.PeriodOf(new DateOnly(2027, 1, 1)));
    }

    [Theory]
    [InlineData("2026-10", "2026-11-02T23:00:00Z")] // 3 Nov 00:00 CET
    [InlineData("2026-09", "2026-10-02T22:00:00Z")] // 3 Oct 00:00 CEST
    [InlineData("2026-02", "2026-03-02T23:00:00Z")]
    [InlineData("2026-12", "2027-01-02T23:00:00Z")] // year rollover
    public void LockInstant_is_midnight_starting_the_3rd_of_the_next_month_in_Stockholm(string period, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), At("2026-11-02T12:00:00Z").LockInstant(period));

    [Theory]
    [InlineData("2026-13")]
    [InlineData("2026-1")]
    [InlineData("nonsense")]
    public void LockInstant_rejects_text_that_is_not_a_period(string period) =>
        Assert.Throws<ArgumentException>(() => At("2026-11-02T12:00:00Z").LockInstant(period));
}
