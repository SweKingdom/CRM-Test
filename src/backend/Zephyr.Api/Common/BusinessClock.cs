using System.Globalization;

namespace Zephyr.Api.Common;

/// <summary>
/// The only way features obtain a business date. Every business-date rule uses Europe/Stockholm; timestamps stay UTC in storage.
/// A period is a calendar month written <c>yyyy-MM</c>.
/// </summary>
public sealed class BusinessClock(TimeProvider time)
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    /// <summary>Day of month on which a period locks: the day after the due date (the 2nd of the next month).</summary>
    private const int LockDay = 3;

    public DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone).DateTime);

    public bool IsFuture(DateOnly date) => date > Today();

    public (int Year, int Week) IsoWeek(DateOnly date)
    {
        var day = date.ToDateTime(TimeOnly.MinValue);
        return (ISOWeek.GetYear(day), ISOWeek.GetWeekOfYear(day));
    }

    public string PeriodOf(DateOnly date) => date.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>The instant a period locks: midnight starting the 3rd of the next month in Stockholm (after the 2nd).</summary>
    public DateTimeOffset LockInstant(string period)
    {
        if (!DateOnly.TryParseExact(period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
        {
            throw new ArgumentException($"'{period}' is not a period. Use yyyy-MM.", nameof(period));
        }

        var next = first.AddMonths(1);
        var local = new DateTime(next.Year, next.Month, LockDay, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, Zone), TimeSpan.Zero);
    }
}
