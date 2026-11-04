using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zephyr.Api.Common;
using Zephyr.Api.Data;
using Zephyr.Api.Permissions;

namespace Zephyr.Api.Time;

/// <summary>A non-blocking remark on a saved entry, for example <c>DAY_OVER_10H</c>.</summary>
public sealed record TimeWarning(string Code, string Message);

/// <summary>A time entry as the API returns it. <c>Warnings</c> is filled by the write that saved it.</summary>
public sealed record TimeEntryView(
    Guid Id, Guid UserId, Guid? ProjectId, string TimeTypeKey, DateOnly WorkDate, int DurationMinutes, string IsoWeek, bool Billable,
    string Status, string? Notes, string Source, DateTime? SubmittedAt, Guid? ApprovedBy, DateTime? ApprovedAt, string? RejectionComment,
    DateTime UpdatedAt, IReadOnlyList<TimeWarning> Warnings);

/// <summary>
/// <c>/api/v1/time-entries</c> (US-163): list a week, create, merge-patch with <c>If-Match</c>, soft delete. Validation order:
/// auth, status, field rules, locked period, team membership, daily total. The rules themselves are in <see cref="TimeRules"/>.
/// </summary>
public static class TimeEntryEndpoints
{
    /// <summary>Authentication types that name the channel of a request; a session cookie (or anything else) is the web UI.</summary>
    public const string ApiTokenAuthType = "ApiToken";
    public const string McpAuthType = "Mcp";

    private const string Entity = "time_entry";
    private const string Url = "/api/v1/time-entries";
    private static readonly Regex Week = new("^[0-9]{4}-W(0[1-9]|[1-4][0-9]|5[0-3])$", RegexOptions.CultureInvariant);

    /// <summary>What the project register (US-161) will tell: whether the project has an hourly rate and whether the user is on its team.</summary>
    private sealed record ProjectInfo(bool HasRate, bool IsMember);

    public static WebApplication MapTimeEntries(this WebApplication app)
    {
        app.MapGet(Url, ListAsync).RequirePermission(Entity, "read");
        app.MapPost(Url, CreateAsync).RequirePermission(Entity, "create").WithWriteHooks();
        app.MapPatch($"{Url}/{{id:guid}}", PatchAsync).RequirePermission(Entity, "update").WithWriteHooks();
        app.MapDelete($"{Url}/{{id:guid}}", DeleteAsync).RequirePermission(Entity, "delete");
        return app;
    }

    private static TimeEntryView View(TimeEntry e, IReadOnlyList<TimeWarning>? warnings = null) => new(
        e.Id, e.UserId, e.ProjectId, e.TimeTypeKey, e.WorkDate, e.DurationMinutes, e.IsoWeek, e.Billable, e.Status, e.Notes, e.Source,
        e.SubmittedAt, e.ApprovedBy, e.ApprovedAt, e.RejectionComment, e.UpdatedAt, warnings ?? []);

    /// <summary>The channel the server stamps as <c>source</c>; never taken from the body.</summary>
    private static string SourceOf(ClaimsPrincipal principal) => principal.Identity?.AuthenticationType switch
    {
        ApiTokenAuthType => "api",
        McpAuthType => "mcp",
        _ => "ui"
    };

    // ---- read ----

    /// <summary>
    /// The entries of one ISO week (<c>week=2026-W45</c>, default the current Stockholm week) of one user (<c>userId</c>, default the
    /// caller). Another user is 403 unless the read scope of the caller's roles covers them (team or org).
    /// </summary>
    private static async Task<IResult> ListAsync(
        string? week, Guid? userId, HttpContext context, ZephyrDbContext db, BusinessClock clock, IPermissionService permissions, CancellationToken ct)
    {
        var user = await context.CurrentUserAsync();
        var isoWeek = week ?? TimeRules.IsoWeekOf(clock, clock.Today());
        if (!Week.IsMatch(isoWeek))
        {
            throw Problems.BadRequest(ErrorCodes.Validation, "week", "week must look like 2026-W45 (year, W, two-digit week).");
        }

        var target = userId ?? user.Id;
        var scope = permissions.Scope(user, Entity, "read");
        if (target != user.Id && scope != "org" && !(scope == "team" && user.Team.Contains(target)))
        {
            throw Problems.Forbidden();
        }

        var rows = await db.TimeEntries.AsNoTracking()
            .Where(e => e.UserId == target && e.IsoWeek == isoWeek)
            .OrderBy(e => e.WorkDate).ThenBy(e => e.CreatedAt).ThenBy(e => e.Id)
            .ToListAsync(ct);
        return Results.Ok(rows.Select(row => View(row)));
    }

    // ---- write ----

    /// <summary>201 with the entry (status Draft, source from the channel, <c>iso_week</c> from the work date) and <c>warnings</c>.</summary>
    private static async Task<IResult> CreateAsync(HttpContext context, ZephyrDbContext db, BusinessClock clock, CancellationToken ct)
    {
        var user = await context.CurrentUserAsync();
        var body = await WriteHooks.ReadBodyAsync(context.Request, ct);
        await WriteHooks.Run(context, Entity, body);
        var entry = new TimeEntry { Id = Guid.NewGuid(), UserId = user.Id, Status = TimeEntry.Draft, Source = SourceOf(context.User) };

        var warnings = await SaveAsync(db, clock, entry, body, ct);

        ETags.Set(context.Response, entry);
        return Results.Created($"{Url}/{entry.Id}", View(entry, warnings));
    }

    /// <summary>
    /// Merge-patch of projectId, timeTypeKey, workDate, durationMinutes, billable and notes. 404 when the caller cannot see the entry,
    /// 403 when they may see but not change it, 428/412 on the ETag, 409 <c>STATUS_CHANGE_NOT_ALLOWED</c> unless Draft or Rejected.
    /// </summary>
    private static async Task<IResult> PatchAsync(
        Guid id, HttpContext context, ZephyrDbContext db, BusinessClock clock, IPermissionService permissions, CancellationToken ct)
    {
        var user = await context.CurrentUserAsync();
        var body = await WriteHooks.ReadBodyAsync(context.Request, ct);
        var entry = await permissions.FindAsync(db.TimeEntries, user, Entity, "update", e => e.Id == id, ct);
        ETags.RequireMatch(context.Request, entry);
        EnsureEditable(entry);
        await WriteHooks.Run(context, Entity, body);

        var warnings = await SaveAsync(db, clock, entry, body, ct);

        ETags.Set(context.Response, entry);
        return Results.Ok(View(entry, warnings));
    }

    /// <summary>Soft delete (204) of a Draft or Rejected entry.</summary>
    private static async Task<IResult> DeleteAsync(
        Guid id, HttpContext context, ZephyrDbContext db, BusinessClock clock, IPermissionService permissions, CancellationToken ct)
    {
        var user = await context.CurrentUserAsync();
        var entry = await permissions.FindAsync(db.TimeEntries, user, Entity, "delete", e => e.Id == id, ct);
        EnsureEditable(entry);
        TimeRules.EnsureNotLocked(clock, entry.WorkDate);

        db.TimeEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static void EnsureEditable(TimeEntry entry)
    {
        if (entry.Status is not (TimeEntry.Draft or TimeEntry.Rejected))
        {
            var message = $"This time entry is {entry.Status} and can no longer be changed. Only Draft or Rejected entries can be edited or deleted.";
            throw new ProblemException(
                StatusCodes.Status409Conflict, ErrorCodes.StatusChangeNotAllowed, "Status change not allowed", message,
                [new FieldError("status", ErrorCodes.StatusChangeNotAllowed, message)]);
        }
    }

    /// <summary>
    /// Applies the body to <paramref name="entry"/> (new when untracked), checks the rules in order, then saves under a per-user,
    /// per-day advisory lock so two concurrent writes cannot together pass the 24-hour limit. Returns the warnings.
    /// </summary>
    private static async Task<IReadOnlyList<TimeWarning>> SaveAsync(
        ZephyrDbContext db, BusinessClock clock, TimeEntry entry, JsonElement body, CancellationToken ct)
    {
        var isNew = db.Entry(entry).State == EntityState.Detached;
        var originalDate = entry.WorkDate;
        var state = new ModelState();
        var edit = Apply(body, entry, isNew, state);
        var type = await TimeTypeAsync(db, entry.TimeTypeKey, ct);
        TimeRules.Validate(entry, type, clock, state);
        if (!state.IsValid)
        {
            throw Problems.Validation(state.Errors);
        }

        if (!isNew && originalDate != entry.WorkDate)
        {
            TimeRules.EnsureNotLocked(clock, originalDate);
        }

        TimeRules.EnsureNotLocked(clock, entry.WorkDate);

        var timeType = type!;
        var project = entry.ProjectId is { } projectId && timeType.RequiresProject
            ? await ProjectInfoAsync(db, projectId, entry.UserId, ct)
            : null;
        if (project is { IsMember: false })
        {
            throw Problems.Validation([new FieldError("projectId", ErrorCodes.Validation, "You are not on this project's team. Ask the project manager to add you.")]);
        }

        if (isNew || edit.BillableGiven || edit.TypeOrProjectChanged)
        {
            entry.Billable = TimeRules.ResolveBillable(timeType, project?.HasRate, edit.Billable);
        }

        entry.IsoWeek = TimeRules.IsoWeekOf(clock, entry.WorkDate);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var lockKey = $"{entry.UserId:N}:{entry.WorkDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        await db.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);
        var otherMinutes = await db.TimeEntries
            .Where(e => e.UserId == entry.UserId && e.WorkDate == entry.WorkDate && e.Id != entry.Id)
            .SumAsync(e => e.DurationMinutes, ct);
        var overTenHours = TimeRules.CheckDay(otherMinutes, entry, state);
        if (!state.IsValid)
        {
            throw Problems.Validation(state.Errors);
        }

        if (isNew)
        {
            db.TimeEntries.Add(entry);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return overTenHours
            ? [new TimeWarning("DAY_OVER_10H", $"{entry.WorkDate:yyyy-MM-dd} now has {TimeRules.Hours(otherMinutes + entry.DurationMinutes)} hours reported. The entry is saved; check that the hours are right.")]
            : [];
    }

    private sealed class Edit
    {
        /// <summary>The value sent for <c>billable</c> (null with <see cref="BillableGiven"/> means: use the default again).</summary>
        public bool? Billable { get; set; }

        public bool BillableGiven { get; set; }

        /// <summary>The time type or the project changed, so the billable default is worked out again.</summary>
        public bool TypeOrProjectChanged { get; set; }
    }

    /// <summary>
    /// Copies the fields of the body onto the entry. A POST needs timeTypeKey, workDate and durationMinutes; a PATCH changes only
    /// what it sends. Anything else (status, source, userId, isoWeek...) is refused: the server owns those.
    /// </summary>
    private static Edit Apply(JsonElement body, TimeEntry entry, bool isNew, ModelState state)
    {
        var edit = new Edit();
        foreach (var property in body.EnumerateObject())
        {
            var value = property.Value;
            switch (property.Name)
            {
                case "projectId":
                    if (value.ValueKind == JsonValueKind.Null)
                    {
                        edit.TypeOrProjectChanged |= entry.ProjectId is not null;
                        entry.ProjectId = null;
                    }
                    else if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var project))
                    {
                        edit.TypeOrProjectChanged |= entry.ProjectId != project;
                        entry.ProjectId = project;
                    }
                    else
                    {
                        state.Add("projectId", ErrorCodes.Validation, "projectId must be the id of a project, or null.");
                    }

                    break;
                case "timeTypeKey":
                    if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                    {
                        edit.TypeOrProjectChanged |= entry.TimeTypeKey != value.GetString();
                        entry.TimeTypeKey = value.GetString()!;
                    }
                    else
                    {
                        state.Add("timeTypeKey", ErrorCodes.Validation, "timeTypeKey must be the key of a time type, for example client_work.");
                    }

                    break;
                case "workDate":
                    if (value.ValueKind == JsonValueKind.String
                        && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    {
                        entry.WorkDate = date;
                    }
                    else
                    {
                        state.Add("workDate", ErrorCodes.Validation, "workDate must be a date written yyyy-MM-dd, for example 2026-11-04.");
                    }

                    break;
                case "durationMinutes":
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var minutes))
                    {
                        entry.DurationMinutes = minutes;
                    }
                    else
                    {
                        state.Add("durationMinutes", ErrorCodes.Validation, "durationMinutes must be a whole number of minutes, for example 450.");
                    }

                    break;
                case "billable":
                    if (value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)
                    {
                        edit.BillableGiven = true;
                        edit.Billable = value.ValueKind == JsonValueKind.Null ? null : value.GetBoolean();
                    }
                    else
                    {
                        state.Add("billable", ErrorCodes.Validation, "billable must be true or false.");
                    }

                    break;
                case "notes":
                    if (value.ValueKind is JsonValueKind.Null or JsonValueKind.String)
                    {
                        entry.Notes = value.GetString();
                    }
                    else
                    {
                        state.Add("notes", ErrorCodes.Validation, "notes must be text, or null to clear it.");
                    }

                    break;
                default:
                    state.Add(property.Name, ErrorCodes.NotPatchable, $"{property.Name} cannot be set here. The server sets it.");
                    break;
            }
        }

        if (isNew)
        {
            foreach (var required in new[] { "timeTypeKey", "workDate", "durationMinutes" })
            {
                if (!body.TryGetProperty(required, out _))
                {
                    state.Add(required, ErrorCodes.Validation, $"{required} is required.");
                }
            }
        }

        return edit;
    }

    /// <summary>The time type row, or null when the key is unknown. Read through the connection: the column names are not the property names.</summary>
    private static async Task<TimeTypeInfo?> TimeTypeAsync(ZephyrDbContext db, string key, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText =
                "select key, active, billable_default, requires_project, allows_future from time_type where key = @key";
            command.Parameters.Add(new NpgsqlParameter("key", key));
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct)
                ? new TimeTypeInfo
                {
                    Key = reader.GetString(0),
                    Active = reader.GetBoolean(1),
                    BillableDefault = reader.GetBoolean(2),
                    RequiresProject = reader.GetBoolean(3),
                    AllowsFuture = reader.GetBoolean(4)
                }
                : null;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// The seam to the project register: null means unknown, and then neither team membership nor the hourly-rate default is applied
    /// (billable follows the time type). US-161 replaces the body with a lookup of <c>project</c> and <c>project_team_member</c>.
    /// </summary>
    private static Task<ProjectInfo?> ProjectInfoAsync(ZephyrDbContext db, Guid projectId, Guid userId, CancellationToken ct) =>
        Task.FromResult<ProjectInfo?>(null);
}
