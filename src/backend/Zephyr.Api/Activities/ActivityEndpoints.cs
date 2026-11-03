using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Zephyr.Api.Common;
using Zephyr.Api.Data;
using Zephyr.Api.Permissions;

namespace Zephyr.Api.Activities;

/// <summary>
/// Calls, meetings, notes, tasks and emails in one table (US-151, ADR-010). The record an activity is "regarding" is
/// polymorphic (type + id); the server resolves it, derives <c>account_id</c> from it and sets <c>connected</c> from the call
/// outcome, so none of those can come from a client. Plain SQL with typed parameters, one transaction per write.
/// This file holds the routes and the SQL; the field and regarding validation is in <c>ActivityValidation.cs</c>.
/// </summary>
public static partial class ActivityEndpoints
{
    private const string Entity = "activity";
    private const string Route = "/api/v1/activities";
    private const int DefaultLimit = 25;
    private const int MaxLimit = 100;

    // TODO(US-144): replace with RequirePermission("activity", read|create|update|delete) and the scope predicate (own / org),
    // which also decides whether the regarding record is visible. Interim rules follow permission-matrix.csv.
    private static readonly string[] WriteRoles = ["Salesperson", "BDR"];
    private static readonly string[] OrgReadRoles = ["Management", "Admin"];
    private static readonly string[] ReadRoles = [.. WriteRoles, .. OrgReadRoles];

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const string Json = """
        jsonb_build_object(
            'id', a.id, 'type', a.type, 'subject', a.subject, 'body', a.body, 'nextStep', a.next_step,
            'transcript', a.transcript, 'location', a.location, 'meetingUrl', a.meeting_url, 'phoneNumber', a.phone_number,
            'regardingType', a.regarding_type, 'regardingId', a.regarding_id, 'accountId', a.account_id,
            'direction', a.direction, 'due', a.due, 'startAt', a.start_at, 'endAt', a.end_at, 'completedAt', a.completed_at,
            'durationMinutes', a.duration_minutes, 'callOutcomeKey', a.call_outcome_key, 'connected', a.connected,
            'source', a.source, 'occurredAt', coalesce(a.start_at, a.created_at),
            'createdAt', a.created_at, 'updatedAt', a.updated_at, 'createdBy', a.created_by,
            'participants', coalesce((
                select jsonb_agg(jsonb_build_object('role', p.role, 'contactId', p.contact_id, 'userId', p.user_id, 'email', p.email)
                                 order by p.role, p.id)
                from activity_participant p
                where p.activity_id = a.id and p.deleted_at is null), '[]'::jsonb))
        """;

    // a visible row is not deleted and, for a caller with "own" scope, created by the caller
    private const string Visible = "a.deleted_at is null and (@owner::uuid is null or a.created_by = @owner::uuid)";

    private const string SelectOne = "select " + Json + "::text as \"Value\" from activity a where a.id = @id and " + Visible;

    // Keyset page, newest first: the rows after the cursor in (coalesce(start_at, created_at), id) descending order.
    private const string SelectPage = $"""
        select coalesce(jsonb_agg(page.j order by page.sort_at desc, page.id desc), '[]'::jsonb)::text as "Value"
        from (
            select a.id, coalesce(a.start_at, a.created_at) as sort_at, {Json} as j
            from activity a
            where {Visible}
              and (@account::uuid is null or a.account_id = @account::uuid)
              and (@regarding_type::text is null or a.regarding_type = @regarding_type::text)
              and (@regarding_id::uuid is null or a.regarding_id = @regarding_id::uuid)
              and (@type::text is null or a.type = @type::text)
              and (@after_at::timestamptz is null
                   or (coalesce(a.start_at, a.created_at), a.id) < (@after_at::timestamptz, @after_id::uuid))
            order by sort_at desc, a.id desc
            limit @take
        ) page
        """;

    private const string InsertParticipant =
        "insert into activity_participant (activity_id, role, contact_id, user_id, email, created_by, updated_by) " +
        "values (@activity, @role, @contact, @participant_user, @email, @user, @user)";

    private const string MoveContactActivities = """
        with moved as (
            update activity set account_id = @account, updated_at = now(), updated_by = @user
            where regarding_type = 'contact' and regarding_id = @contact and account_id is distinct from @account
            returning id
        )
        insert into audit_log (user_id, action, entity_type, entity_id, detail)
        select @user, 'activity_account_moved', 'activity', id, jsonb_build_object('contactId', @contact, 'accountId', @account)
        from moved
        """;

    // The columns a client can write, shared by the INSERT and the UPDATE.
    private static readonly Column[] Columns =
    [
        new("type", NpgsqlDbType.Text, r => r.Type),
        new("subject", NpgsqlDbType.Text, r => r.Subject),
        new("body", NpgsqlDbType.Text, r => r.Body),
        new("next_step", NpgsqlDbType.Text, r => r.NextStep),
        new("transcript", NpgsqlDbType.Text, r => r.Transcript),
        new("location", NpgsqlDbType.Text, r => r.Location),
        new("meeting_url", NpgsqlDbType.Text, r => r.MeetingUrl),
        new("phone_number", NpgsqlDbType.Text, r => r.PhoneNumber),
        new("regarding_type", NpgsqlDbType.Text, r => r.RegardingType),
        new("regarding_id", NpgsqlDbType.Uuid, r => r.RegardingId),
        new("account_id", NpgsqlDbType.Uuid, r => r.AccountId),
        new("direction", NpgsqlDbType.Text, r => r.Direction),
        new("due", NpgsqlDbType.TimestampTz, r => r.Due?.UtcDateTime),
        new("start_at", NpgsqlDbType.TimestampTz, r => r.StartAt?.UtcDateTime),
        new("end_at", NpgsqlDbType.TimestampTz, r => r.EndAt?.UtcDateTime),
        new("completed_at", NpgsqlDbType.TimestampTz, r => r.CompletedAt?.UtcDateTime),
        new("duration_minutes", NpgsqlDbType.Integer, r => r.DurationMinutes),
        new("call_outcome_key", NpgsqlDbType.Text, r => r.CallOutcomeKey),
        new("connected", NpgsqlDbType.Boolean, r => r.Connected)
    ];

    private static readonly string InsertSql =
        $"insert into activity (id, source, created_by, updated_by, {string.Join(", ", Columns.Select(c => c.Name))}) " +
        $"values (@id, @source, @user, @user, {string.Join(", ", Columns.Select(c => "@" + c.Name))})";

    // updated_at is the concurrency token: the UPDATE only succeeds while it is still the value that was read.
    private static readonly string UpdateSql =
        $"update activity set {string.Join(", ", Columns.Select(c => c.Name + " = @" + c.Name))}, updated_at = now(), updated_by = @user " +
        "where id = @id and deleted_at is null and updated_at = @loaded";

    private sealed record Column(string Name, NpgsqlDbType Type, Func<Row, object?> Value);

    private sealed record Loaded(Row Row, JsonElement Json);

    private sealed record Cursor(string At, Guid Id);

    private sealed record ListArgs(int Limit, Guid? Account, string? RegardingType, Guid? RegardingId, string? Type, DateTime? AfterAt, Guid? AfterId);

    public static IEndpointRouteBuilder MapActivityEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(Route, CreateAsync).WithWriteHooks();
        routes.MapGet(Route, ListAsync);
        routes.MapGet(Route + "/{id:guid}", GetAsync);
        routes.MapPatch(Route + "/{id:guid}", PatchAsync).WithWriteHooks();
        routes.MapDelete(Route + "/{id:guid}", DeleteAsync);
        return routes;
    }

    /// <summary>
    /// Moves the activities regarding a contact to the contact's new account (AC 5). Call it inside the transaction that
    /// changes the contact (<c>db.Database.BeginTransactionAsync</c>): it joins that transaction, so a failure rolls both
    /// back. Writes one audit_log row per moved activity. Activities where the contact is only a participant do not move.
    /// </summary>
    public static Task<int> MoveContactActivitiesAsync(
        ZephyrDbContext db, Guid contactId, Guid? newAccountId, Guid? userId, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(
            MoveContactActivities,
            new[] { P("account", NpgsqlDbType.Uuid, newAccountId), P("user", NpgsqlDbType.Uuid, userId), P("contact", NpgsqlDbType.Uuid, contactId) },
            ct);

    // ---- routes ----

    /// <summary>201 + Location. 422 for a bad field, an unknown or unseen regarding record, a bad outcome or participant. Nothing is stored then.</summary>
    private static async Task<IResult> CreateAsync(HttpContext context, ZephyrDbContext db, ICurrentUser current, CancellationToken ct)
    {
        await InterimRoleGate.RequireAsync(context, WriteRoles);
        var user = current.UserId!.Value;
        var body = await WriteHooks.ReadBodyAsync(context.Request, ct);
        await WriteHooks.Run(context, Entity, body);

        // A bearer token means a program (an MCP client or script), not the web UI.
        var row = new Row { Id = Guid.NewGuid(), Source = context.Request.Headers.Authorization.Count > 0 ? "mcp" : "ui" };
        var state = new ModelState();
        Apply(row, body, state, creating: true);
        await CheckAsync(db, row, regardingChanged: true, state, ct);
        if (!state.IsValid)
        {
            throw Problems.Validation(state.Errors);
        }

        var parameters = Parameters(row, user);
        parameters.Add(P("source", NpgsqlDbType.Text, row.Source));
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync(InsertSql, parameters, ct);
            await SaveParticipantsAsync(db, row, user, ct);
            await transaction.CommitAsync(ct);
        }

        var loaded = await LoadAsync(db, row.Id, user, ct) ?? throw new InvalidOperationException($"Activity {row.Id} was not found after it was created.");
        context.Response.Headers.ETag = ETags.Compute(loaded.Row.Id, loaded.Row.UpdatedAt.UtcDateTime);
        return Results.Created($"{Route}/{row.Id}", loaded.Json);
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext context, ZephyrDbContext db, ICurrentUser current, CancellationToken ct)
    {
        var roles = await InterimRoleGate.RequireAsync(context, ReadRoles);
        var loaded = await LoadAsync(db, id, OwnerScope(current, roles), ct);
        if (loaded is null)
        {
            return Results.NotFound();
        }

        context.Response.Headers.ETag = ETags.Compute(loaded.Row.Id, loaded.Row.UpdatedAt.UtcDateTime);
        return Results.Ok(loaded.Json);
    }

    /// <summary>Merge-patch with <c>If-Match</c> (428 missing, 412 stale). The type cannot change. A changed regarding re-derives the account.</summary>
    private static async Task<IResult> PatchAsync(Guid id, HttpContext context, ZephyrDbContext db, ICurrentUser current, CancellationToken ct)
    {
        await InterimRoleGate.RequireAsync(context, WriteRoles);
        var user = current.UserId!.Value;
        var loaded = await LoadAsync(db, id, user, ct);
        if (loaded is null)
        {
            return Results.NotFound();
        }

        var row = loaded.Row;
        var updatedAt = row.UpdatedAt.UtcDateTime;
        ETags.RequireMatch(context.Request, row.Id, updatedAt);
        var body = await WriteHooks.ReadBodyAsync(context.Request, ct);
        await WriteHooks.Run(context, Entity, body);

        var regarding = (row.RegardingType, row.RegardingId);
        var state = new ModelState();
        Apply(row, body, state, creating: false);
        await CheckAsync(db, row, regardingChanged: regarding != (row.RegardingType, row.RegardingId), state, ct);
        if (!state.IsValid)
        {
            throw Problems.Validation(state.Errors);
        }

        var parameters = Parameters(row, user);
        parameters.Add(P("loaded", NpgsqlDbType.TimestampTz, updatedAt));
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            if (await db.Database.ExecuteSqlRawAsync(UpdateSql, parameters, ct) == 0)
            {
                throw Problems.PreconditionFailed();
            }

            if (body.TryGetProperty("participants", out _))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "delete from activity_participant where activity_id = @id", new[] { P("id", NpgsqlDbType.Uuid, row.Id) }, ct);
                await SaveParticipantsAsync(db, row, user, ct);
            }

            await transaction.CommitAsync(ct);
        }

        var saved = await LoadAsync(db, id, user, ct) ?? throw new InvalidOperationException($"Activity {id} was not found after it was updated.");
        context.Response.Headers.ETag = ETags.Compute(saved.Row.Id, saved.Row.UpdatedAt.UtcDateTime);
        return Results.Ok(saved.Json);
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpContext context, ZephyrDbContext db, ICurrentUser current, CancellationToken ct)
    {
        await InterimRoleGate.RequireAsync(context, WriteRoles);
        var changed = await db.Database.ExecuteSqlRawAsync(
            "update activity set deleted_at = now(), updated_at = now(), updated_by = @user " +
            "where id = @id and created_by = @user and deleted_at is null",
            new[] { P("id", NpgsqlDbType.Uuid, id), P("user", NpgsqlDbType.Uuid, current.UserId) },
            ct);
        return changed == 0 ? Results.NotFound() : Results.NoContent();
    }

    /// <summary>Keyset-paged, newest first. Default limit 25, at most 100 (more is a 400).</summary>
    private static async Task<IResult> ListAsync(HttpContext context, ZephyrDbContext db, ICurrentUser current, CancellationToken ct)
    {
        var roles = await InterimRoleGate.RequireAsync(context, ReadRoles);
        var args = ParseList(context.Request.Query);
        var text = await db.Database.SqlQueryRaw<string>(
            SelectPage,
            P("owner", NpgsqlDbType.Uuid, OwnerScope(current, roles)),
            P("account", NpgsqlDbType.Uuid, args.Account),
            P("regarding_type", NpgsqlDbType.Text, args.RegardingType),
            P("regarding_id", NpgsqlDbType.Uuid, args.RegardingId),
            P("type", NpgsqlDbType.Text, args.Type),
            P("after_at", NpgsqlDbType.TimestampTz, args.AfterAt),
            P("after_id", NpgsqlDbType.Uuid, args.AfterId),
            P("take", NpgsqlDbType.Integer, args.Limit + 1)).SingleAsync(ct);

        var items = JsonSerializer.Deserialize<List<JsonElement>>(text) ?? [];
        string? next = null;
        if (items.Count > args.Limit)
        {
            items.RemoveRange(args.Limit, items.Count - args.Limit);
            var last = items[^1];
            var at = last.GetProperty("occurredAt").GetDateTimeOffset().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            next = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new Cursor(at, last.GetProperty("id").GetGuid()), Web));
        }

        return Results.Ok(new { items, nextCursor = next });
    }

    // ---- reading ----

    private static async Task<Loaded?> LoadAsync(ZephyrDbContext db, Guid id, Guid? owner, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<string>(
            SelectOne, P("id", NpgsqlDbType.Uuid, id), P("owner", NpgsqlDbType.Uuid, owner)).ToListAsync(ct);
        if (rows.Count == 0)
        {
            return null;
        }

        var json = JsonSerializer.Deserialize<JsonElement>(rows[0]);
        return new Loaded(json.Deserialize<Row>(Web)!, json);
    }

    /// <summary>Salesperson and BDR see their own activities; Management and Admin see all (permission matrix, scope own / org).</summary>
    private static Guid? OwnerScope(ICurrentUser current, string[] roles) => roles.Intersect(OrgReadRoles).Any() ? null : current.UserId;

    private static ListArgs ParseList(IQueryCollection query)
    {
        var limit = DefaultLimit;
        Guid? account = null;
        string? regardingType = null;
        Guid? regardingId = null;
        string? type = null;
        DateTime? afterAt = null;
        Guid? afterId = null;
        foreach (var (key, values) in query)
        {
            if (values.Count > 1)
            {
                throw Problems.BadRequest(ErrorCodes.Validation, key, $"'{key}' was given more than once.");
            }

            var value = values.ToString();
            switch (key)
            {
                case "limit":
                    limit = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= MaxLimit
                        ? number
                        : throw Problems.BadRequest(ErrorCodes.Validation, key, $"limit must be a whole number from 1 to {MaxLimit}.");
                    break;
                case "cursor":
                    (afterAt, afterId) = DecodeCursor(value);
                    break;
                case "accountId":
                    account = QueryGuid(key, value);
                    break;
                case "regardingId":
                    regardingId = QueryGuid(key, value);
                    break;
                case "regardingType":
                    regardingType = QueryChoice(key, value, RegardingTypes);
                    break;
                case "type":
                    type = QueryChoice(key, value, Types);
                    break;
                default:
                    throw Problems.BadRequest(ErrorCodes.InvalidFilter, key,
                        $"Unknown query parameter '{key}'. Use regardingType, regardingId, accountId, type, cursor or limit.");
            }
        }

        return new ListArgs(limit, account, regardingType, regardingId, type, afterAt, afterId);
    }

    private static Guid QueryGuid(string key, string value) =>
        Guid.TryParse(value, out var id) ? id : throw Problems.BadRequest(ErrorCodes.Validation, key, $"{key} must be an id.");

    private static string QueryChoice(string key, string value, string[] allowed) =>
        allowed.Contains(value)
            ? value
            : throw Problems.BadRequest(ErrorCodes.Validation, key, $"{key} must be one of: {string.Join(", ", allowed)}.");

    private static (DateTime At, Guid Id) DecodeCursor(string cursor)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<Cursor>(WebEncoders.Base64UrlDecode(cursor), Web);
            if (payload is not null
                && DateTime.TryParse(payload.At, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                && at.Kind == DateTimeKind.Utc)
            {
                return (at, payload.Id);
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            // falls through to the problem below
        }

        throw Problems.BadRequest(ErrorCodes.Validation, "cursor", "The cursor is not valid. Start again without a cursor.");
    }

    // ---- writing ----

    private static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) => new(name, type) { Value = value ?? DBNull.Value };

    /// <summary>The parameters shared by the INSERT and the UPDATE: every writable column plus @id and @user.</summary>
    private static List<NpgsqlParameter> Parameters(Row row, Guid user)
    {
        var parameters = Columns.Select(c => P(c.Name, c.Type, c.Value(row))).ToList();
        parameters.Add(P("id", NpgsqlDbType.Uuid, row.Id));
        parameters.Add(P("user", NpgsqlDbType.Uuid, user));
        return parameters;
    }

    private static async Task SaveParticipantsAsync(ZephyrDbContext db, Row row, Guid user, CancellationToken ct)
    {
        foreach (var participant in row.Participants)
        {
            await db.Database.ExecuteSqlRawAsync(
                InsertParticipant,
                new[]
                {
                    P("activity", NpgsqlDbType.Uuid, row.Id),
                    P("role", NpgsqlDbType.Text, participant.Role),
                    P("contact", NpgsqlDbType.Uuid, participant.ContactId),
                    P("participant_user", NpgsqlDbType.Uuid, participant.UserId),
                    P("email", NpgsqlDbType.Text, participant.Email),
                    P("user", NpgsqlDbType.Uuid, user)
                },
                ct);
        }
    }
}
