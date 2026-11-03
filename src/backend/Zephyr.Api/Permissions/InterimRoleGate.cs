using Microsoft.EntityFrameworkCore;
using Zephyr.Api.Common;
using Zephyr.Api.Data;

namespace Zephyr.Api.Permissions;

/// <summary>
/// TODO(US-143/144): interim stand-in for <c>RequirePermission(entity, action)</c> until the permission engine exists.
/// The caller's roles are read from the active <c>app_user</c> row on every request (never from the request body).
/// When the engine lands, replace each <see cref="RequireAsync"/> call with <c>RequirePermission(entity, create|update|delete)</c>
/// plus scope <c>org</c>, and delete this file.
/// </summary>
public static class InterimRoleGate
{
    /// <summary>Org-wide create+update on every entity (design v22 R7): the by-external-id upsert.</summary>
    public static readonly string[] OrgWriteRoles = ["Admin", "Integration"];

    /// <summary>Least privilege until the matrix defines delete per role.</summary>
    public static readonly string[] DeleteRoles = ["Admin"];

    /// <summary>
    /// Passes when the signed-in, active user holds one of <paramref name="roles"/> and returns all roles the user holds;
    /// otherwise a 403 problem.
    /// </summary>
    public static async Task<string[]> RequireAsync(HttpContext context, string[] roles)
    {
        var userId = context.RequestServices.GetRequiredService<ICurrentUser>().UserId;
        if (userId is not null)
        {
            var db = context.RequestServices.GetRequiredService<ZephyrDbContext>();
            var held = await db.AppUsers.AsNoTracking()
                .Where(u => u.Id == userId && u.Active)
                .Select(u => u.Roles)
                .SingleOrDefaultAsync(context.RequestAborted);
            if (held is not null && held.Intersect(roles).Any())
            {
                return held;
            }
        }

        throw new ProblemException(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, "Forbidden",
            "Your role does not allow this action. Ask an administrator if you need access.");
    }
}
