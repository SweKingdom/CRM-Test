using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zephyr.Api.Common;
using Zephyr.Api.Data;

namespace Zephyr.Api.Permissions;

/// <summary>
/// Per-project time approvers and approval delegation (US-145). A delegate holds approver rights on the delegator's own
/// <c>project_approver</c> projects while <c>revoked_at IS NULL</c> and <c>today</c> (<see cref="BusinessClock.Today"/>, inclusive) is
/// within the date range. Delegated rights are never passed on. Everything is evaluated per request from the database: nothing is cached.
/// The helpers <see cref="ApproverScopeAsync"/>, <see cref="IsApproverOfAsync"/> and <see cref="EnsureNotSelfApproval"/> are what the
/// time-entry reads and the approval action (US-166, US-170) call.
/// </summary>
public static class Approvers
{
    private const string ApproverEntity = "project_approver";
    private const string DelegationEntity = "approval_delegation";
    private static readonly JsonElement NoBody = EmptyObject();

    /// <summary>
    /// Every user role. The delegation routes belong to no matrix entity: they are about the caller's own approval rights, so any
    /// signed-in user with a role may call them and the handlers scope everything to the caller (an Admin sees and revokes all).
    /// </summary>
    private static readonly string[] AnyRole = PermissionSeeder.Roles.Select(r => r.Key).ToArray();

    public static WebApplication MapApprovers(this WebApplication app)
    {
        // TODO(US-161): allow the project's PM as well as Admin once the project register exists.
        app.MapPut("/api/v1/projects/{id:guid}/approvers/{userId:guid}", AddApproverAsync)
            .RequireRoles(AppUser.AdminRole)
            .WithWriteHooks();
        app.MapDelete("/api/v1/projects/{id:guid}/approvers/{userId:guid}", RemoveApproverAsync)
            .RequireRoles(AppUser.AdminRole);

        app.MapPost("/api/v1/delegations", CreateDelegationAsync).RequireRoles(AnyRole).WithWriteHooks();
        app.MapGet("/api/v1/delegations", ListDelegationsAsync).RequireRoles(AnyRole);
        app.MapDelete("/api/v1/delegations/{id:guid}", RevokeDelegationAsync).RequireRoles(AnyRole);
        return app;
    }

    /// <summary>
    /// The projects <paramref name="userId"/> approves time for on <paramref name="today"/>: their own approver rows plus the approver
    /// rows of every delegator with an active delegation to them. A delegator's delegated projects are not included (not transitive).
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> ApproverScopeAsync(
        ZephyrDbContext db, Guid userId, DateOnly today, CancellationToken ct = default) =>
        await db.Database.SqlQuery<Guid>(
            $"""
            select project_id as "Value" from project_approver where user_id = {userId}
            union
            select pa.project_id from approval_delegation d
            join project_approver pa on pa.user_id = d.delegator_id
            where d.delegate_id = {userId} and d.revoked_at is null and d.deleted_at is null
              and {today} between d.from_date and d.to_date
            """).ToListAsync(ct);

    public static async Task<bool> IsApproverOfAsync(
        ZephyrDbContext db, Guid userId, Guid projectId, DateOnly today, CancellationToken ct = default) =>
        (await ApproverScopeAsync(db, userId, today, ct)).Contains(projectId);

    /// <summary>The approval action calls this first: nobody approves their own entry, as approver or as delegate. 403 otherwise.</summary>
    public static void EnsureNotSelfApproval(Guid entryUserId, Guid callerId)
    {
        if (entryUserId == callerId)
        {
            throw Forbidden("You cannot approve your own time entries. Ask another approver.");
        }
    }

    private static ProblemException Forbidden(string detail) =>
        new(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, "Forbidden", detail);

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    /// <summary>The signed-in, active user and whether they are an administrator; 403 for anyone else. Read from the database each time.</summary>
    private static async Task<(Guid Id, bool Admin)> CallerAsync(ZephyrDbContext db, ICurrentUser current, CancellationToken ct)
    {
        var id = current.UserId;
        var roles = id is null
            ? null
            : await db.AppUsers.AsNoTracking().Where(u => u.Id == id && u.Active).Select(u => u.Roles).SingleOrDefaultAsync(ct);
        return roles is null
            ? throw Forbidden("Sign in with an active Zephyr account to manage approvers and delegations.")
            : (id!.Value, roles.Contains(AppUser.AdminRole));
    }

    private static void Audit(ZephyrDbContext db, Guid actor, string action, string entityType, Guid entityId, object? detail = null) =>
        db.AuditLogs.Add(new AuditLog
        {
            UserId = actor,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Detail = detail is null ? null : JsonSerializer.Serialize(detail)
        });

    // ---- approvers ----

    private static async Task<IResult> AddApproverAsync(
        Guid id, Guid userId, HttpContext context, ZephyrDbContext db, ICurrentUser current, CancellationToken ct)
    {
        await WriteHooks.Run(context, ApproverEntity, NoBody);
        if (!await db.AppUsers.AnyAsync(u => u.Id == userId, ct))
        {
            return Results.NotFound();
        }

        var actor = current.UserId ?? SystemUser.Id;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var added = await db.Database.ExecuteSqlInterpolatedAsync(
            $"insert into project_approver (project_id, user_id, created_by) values ({id}, {userId}, {actor}) on conflict do nothing", ct);
        if (added > 0)
        {
            Audit(db, actor, "approver_added", ApproverEntity, id, new { userId });
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveApproverAsync(
        Guid id, Guid userId, ZephyrDbContext db, ICurrentUser current, CancellationToken ct)
    {
        var actor = current.UserId ?? SystemUser.Id;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var removed = await db.Database.ExecuteSqlInterpolatedAsync(
            $"delete from project_approver where project_id = {id} and user_id = {userId}", ct);
        if (removed > 0)
        {
            Audit(db, actor, "approver_removed", ApproverEntity, id, new { userId });
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    // ---- delegations ----

    /// <summary>
    /// 201 for the caller as delegator; an Admin may name another delegator with <c>delegatorId</c>. 422 when the dates are wrong
    /// (<c>toDate</c> before <c>fromDate</c>), the delegate is the delegator or unknown, or the delegator has no approver rights of
    /// their own (which also stops a delegate from delegating again).
    /// </summary>
    private static async Task<IResult> CreateDelegationAsync(
        HttpContext context, ZephyrDbContext db, ICurrentUser current, CancellationToken ct)
    {
        var (callerId, admin) = await CallerAsync(db, current, ct);
        var body = await WriteHooks.ReadBodyAsync(context.Request, ct);
        await WriteHooks.Run(context, DelegationEntity, body);

        var errors = new List<FieldError>();
        var delegateId = ReadGuid(body, "delegateId", true, errors);
        var requestedDelegator = ReadGuid(body, "delegatorId", false, errors);
        var from = ReadDate(body, "fromDate", errors);
        var to = ReadDate(body, "toDate", errors);
        if (requestedDelegator is { } other && other != callerId && !admin)
        {
            throw Forbidden("Only an administrator can delegate on behalf of someone else.");
        }

        var delegatorId = requestedDelegator ?? callerId;
        if (from is { } start && to is { } end && end < start)
        {
            errors.Add(new FieldError("toDate", ErrorCodes.Validation, "toDate must be the same day as fromDate or later."));
        }

        if (delegateId == delegatorId)
        {
            errors.Add(new FieldError("delegateId", ErrorCodes.Validation, "You cannot delegate to yourself. Choose another person."));
        }
        else if (delegateId is { } person && !await db.AppUsers.AnyAsync(u => u.Id == person && u.Active, ct))
        {
            errors.Add(new FieldError("delegateId", ErrorCodes.Validation, "No active user has this id."));
        }

        if (errors.Count == 0 && !await db.Database
                .SqlQuery<bool>($"""select exists (select 1 from project_approver where user_id = {delegatorId}) as "Value" """)
                .SingleAsync(ct))
        {
            errors.Add(new FieldError("delegatorId", ErrorCodes.Validation,
                "Only an approver of a project can delegate approval. Delegated approval rights cannot be delegated again."));
        }

        if (errors.Count > 0)
        {
            throw Problems.Validation(errors);
        }

        var id = Guid.NewGuid();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            insert into approval_delegation (id, delegator_id, delegate_id, from_date, to_date, created_by, updated_by)
            values ({id}, {delegatorId}, {delegateId!.Value}, {from!.Value}, {to!.Value}, {callerId}, {callerId})
            """, ct);
        Audit(db, callerId, "delegation_created", DelegationEntity, id,
            new { delegatorId, delegateId, fromDate = from, toDate = to });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Json(new { id, delegatorId, delegateId, fromDate = from, toDate = to }, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>The caller's delegations as delegator or delegate (revoked ones included, with <c>revokedAt</c>); an Admin sees all.</summary>
    private static async Task<IResult> ListDelegationsAsync(ZephyrDbContext db, ICurrentUser current, CancellationToken ct)
    {
        var (callerId, admin) = await CallerAsync(db, current, ct);
        var rows = new List<object>();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText =
                """
                select id, delegator_id, delegate_id, from_date, to_date, revoked_at from approval_delegation
                where deleted_at is null and (@all or delegator_id = @me or delegate_id = @me)
                order by from_date desc, created_at desc, id
                """;
            command.Parameters.Add(new NpgsqlParameter("all", admin));
            command.Parameters.Add(new NpgsqlParameter("me", callerId));
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new
                {
                    id = reader.GetGuid(0),
                    delegatorId = reader.GetGuid(1),
                    delegateId = reader.GetGuid(2),
                    fromDate = reader.GetFieldValue<DateOnly>(3),
                    toDate = reader.GetFieldValue<DateOnly>(4),
                    revokedAt = reader.IsDBNull(5) ? (DateTime?)null : reader.GetFieldValue<DateTime>(5)
                });
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        return Results.Ok(rows);
    }

    /// <summary>Sets <c>revoked_at</c>: the delegation ends on the next request. 404 when it is unknown, already revoked or not the caller's (an Admin may revoke any).</summary>
    private static async Task<IResult> RevokeDelegationAsync(Guid id, ZephyrDbContext db, ICurrentUser current, CancellationToken ct)
    {
        var (callerId, admin) = await CallerAsync(db, current, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var revoked = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            update approval_delegation set revoked_at = now(), updated_at = now(), updated_by = {callerId}
            where id = {id} and revoked_at is null and deleted_at is null and ({admin} or delegator_id = {callerId})
            """, ct);
        if (revoked == 0)
        {
            return Results.NotFound();
        }

        Audit(db, callerId, "delegation_revoked", DelegationEntity, id);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    // ---- body parsing ----

    private static Guid? ReadGuid(JsonElement body, string field, bool required, List<FieldError> errors)
    {
        if (!body.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                errors.Add(new FieldError(field, ErrorCodes.Validation, $"{field} is required."));
            }

            return null;
        }

        if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id))
        {
            return id;
        }

        errors.Add(new FieldError(field, ErrorCodes.Validation, $"{field} must be a user id."));
        return null;
    }

    private static DateOnly? ReadDate(JsonElement body, string field, List<FieldError> errors)
    {
        if (body.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        errors.Add(new FieldError(field, ErrorCodes.Validation, $"{field} is required and must be a date written yyyy-MM-dd, for example 2026-12-24."));
        return null;
    }
}
