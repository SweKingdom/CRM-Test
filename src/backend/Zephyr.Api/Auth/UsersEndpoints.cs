using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zephyr.Api.Common;
using Zephyr.Api.Data;

namespace Zephyr.Api.Auth;

/// <summary>User administration (US-141): activate and deactivate a user. Further fields come with the user CRUD story.</summary>
public static class UsersEndpoints
{
    private const string Field = "active";

    // Serialises deactivations of administrators so two admins cannot deactivate each other at the same moment.
    private const string AdminLock = "select pg_advisory_xact_lock(7141)";

    public static WebApplication MapUsersEndpoints(this WebApplication app)
    {
        app.MapPatch("/api/v1/users/{id}", PatchAsync)
            .RequireAuthorization(policy => policy.RequireRole(AppUser.AdminRole))
            .WithWriteHooks();
        return app;
    }

    /// <summary>
    /// Merge-patch <c>{ "active": bool }</c> with <c>If-Match</c>. 404 unknown or deleted, 428/412 on the ETag, 422 for other
    /// fields (NOT_PATCHABLE) or for deactivating the last active administrator (LAST_ADMIN). Evicts the cached status so the
    /// user is refused on the next request; the recheck window is the upper bound regardless.
    /// </summary>
    private static async Task<IResult> PatchAsync(
        Guid id, HttpContext context, ZephyrDbContext db, ICurrentUser current, IUserStatus status, CancellationToken ct)
    {
        var body = await WriteHooks.ReadBodyAsync(context.Request, ct);
        var user = await db.AppUsers.SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        ETags.RequireMatch(context.Request, user);
        await WriteHooks.Run(context, "app_user", body);
        var active = ReadActive(body);

        if (user.Active != active)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (!active && user.Roles.Contains(AppUser.AdminRole))
            {
                await db.Database.ExecuteSqlRawAsync(AdminLock, ct);
                if (!await db.AppUsers.AnyAsync(u => u.Id != id && u.Active && u.Roles.Contains(AppUser.AdminRole), ct))
                {
                    const string message = "This is the last active administrator. Make another user an administrator before deactivating this one.";
                    throw new ProblemException(StatusCodes.Status422UnprocessableEntity, ErrorCodes.LastAdmin, "Validation failed", message,
                        [new FieldError(Field, ErrorCodes.LastAdmin, message)]);
                }
            }

            user.Active = active;
            db.AuditLogs.Add(new AuditLog
            {
                UserId = current.UserId,
                Action = active ? "user_activated" : "user_deactivated",
                EntityType = "app_user",
                EntityId = user.Id,
                Detail = JsonSerializer.Serialize(new { active })
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            status.Forget(id);
        }

        ETags.Set(context.Response, user);
        return Results.Ok(new { user.Id, user.Name, user.Email, user.Roles, user.HomeCountry, user.Active, user.LastLoginAt, user.UpdatedAt });
    }

    /// <summary>The new value of <c>active</c>; anything else in the body, or a missing or non-boolean <c>active</c>, is a 422.</summary>
    private static bool ReadActive(JsonElement body)
    {
        var errors = new List<FieldError>();
        bool? active = null;
        foreach (var property in body.EnumerateObject())
        {
            if (property.Name != Field)
            {
                errors.Add(new FieldError(property.Name, ErrorCodes.NotPatchable, $"{property.Name} cannot be changed here. Only {Field} can."));
            }
            else if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                active = property.Value.GetBoolean();
            }
            else
            {
                errors.Add(new FieldError(Field, ErrorCodes.Validation, $"{Field} must be true or false."));
            }
        }

        if (active is null && errors.All(e => e.Field != Field))
        {
            errors.Add(new FieldError(Field, ErrorCodes.Validation, $"Send {{\"{Field}\": true}} or {{\"{Field}\": false}}."));
        }

        return errors.Count == 0 ? active!.Value : throw Problems.Validation(errors);
    }
}
