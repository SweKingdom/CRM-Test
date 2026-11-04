using System.Globalization;
using Zephyr.Api.Common;
using Zephyr.Api.Data;

namespace Zephyr.Api.Time;

/// <summary>A row of <c>time_type</c> as the rules need it. Read with SQL, so the property names are the column aliases.</summary>
public sealed class TimeTypeInfo
{
    public string Key { get; set; } = "";
    public bool Active { get; set; }
    public bool BillableDefault { get; set; }
    public bool RequiresProject { get; set; }
    public bool AllowsFuture { get; set; }
}

/// <summary>
/// The pure time-entry rules (US-163): no database, no request. Every business date comes from the <see cref="BusinessClock"/>
/// (Europe/Stockholm), so the future check, <c>iso_week</c> and the lock month cannot disagree about which day it is.
/// </summary>
public static class TimeRules
{
    public const int StepMinutes = 15;
    public const int MaxNotesLength = 500;
    public const int MaxDayMinutes = 24 * 60;
    public const int WarnDayMinutes = 10 * 60;

    /// <summary><c>iso_week</c> of a work date, for example <c>2026-W45</c>.</summary>
    public static string IsoWeekOf(BusinessClock clock, DateOnly date)
    {
        var (year, week) = clock.IsoWeek(date);
        return string.Create(CultureInfo.InvariantCulture, $"{year:0000}-W{week:00}");
    }

    /// <summary>
    /// Adds one error per failing field: unknown or inactive type, duration not in 15-minute steps, notes over 500 characters,
    /// a missing project for a type that needs one, and a work date after today for a type that does not allow the future.
    /// </summary>
    public static void Validate(TimeEntry entry, TimeTypeInfo? type, BusinessClock clock, ModelState state)
    {
        if (type is not { Active: true })
        {
            state.Add("timeTypeKey", ErrorCodes.Validation, $"'{entry.TimeTypeKey}' is not an active time type. Choose a type from the time type list.");
        }

        if (entry.DurationMinutes <= 0 || entry.DurationMinutes % StepMinutes != 0)
        {
            state.Add("durationMinutes", ErrorCodes.Validation,
                $"durationMinutes must be a positive number of minutes in {StepMinutes}-minute steps, for example 450 for 7.5 hours.");
        }

        if (entry.Notes is { } notes && notes.EnumerateRunes().Count() > MaxNotesLength)
        {
            state.Add("notes", ErrorCodes.Validation, $"notes can have at most {MaxNotesLength} characters. Shorten the text.");
        }

        if (type is not { Active: true })
        {
            return;
        }

        if (type.RequiresProject && entry.ProjectId is null)
        {
            state.Add("projectId", ErrorCodes.Validation, "This time type needs a project. Choose the project the time belongs to.");
        }

        if (!type.AllowsFuture && clock.IsFuture(entry.WorkDate))
        {
            state.Add("workDate", ErrorCodes.Validation,
                "This time type cannot be reported for a date after today. Only time types that allow future dates (vacation, absence) can.");
        }
    }

    /// <summary>409 <c>PERIOD_LOCKED</c> when the month of <paramref name="date"/> is locked (the seam is <see cref="BusinessClock.IsLocked"/>).</summary>
    public static void EnsureNotLocked(BusinessClock clock, DateOnly date)
    {
        if (clock.IsLocked(date))
        {
            throw new ProblemException(
                StatusCodes.Status409Conflict, ErrorCodes.PeriodLocked, "Period locked",
                $"The period {clock.PeriodOf(date)} is locked, so time in it can no longer be added or changed. Ask finance if it must be corrected.");
        }
    }

    /// <summary>
    /// Billable for a new or re-typed entry: the person's own choice wins; on a project known to have no hourly rate it is false;
    /// otherwise the time type decides. <paramref name="projectHasRate"/> is null when there is no project or it is not known.
    /// </summary>
    public static bool ResolveBillable(TimeTypeInfo type, bool? projectHasRate, bool? requested) =>
        requested ?? (projectHasRate != false && type.BillableDefault);

    /// <summary>
    /// The daily limit. <paramref name="otherMinutes"/> is what the user already has on the work date besides this entry.
    /// Over 24 hours adds an error; the result is true when the day is over 10 hours and still allowed (a warning, the entry saves).
    /// </summary>
    public static bool CheckDay(int otherMinutes, TimeEntry entry, ModelState state)
    {
        var total = otherMinutes + entry.DurationMinutes;
        if (total > MaxDayMinutes)
        {
            state.Add("durationMinutes", ErrorCodes.Validation,
                $"With this entry {entry.WorkDate:yyyy-MM-dd} would have {Hours(total)} hours reported. A day can hold at most 24 hours. Shorten this entry or change another one on the same day.");
            return false;
        }

        return total > WarnDayMinutes;
    }

    public static string Hours(int minutes) => (minutes / 60m).ToString("0.##", CultureInfo.InvariantCulture);
}
