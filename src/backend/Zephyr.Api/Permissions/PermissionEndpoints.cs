namespace Zephyr.Api.Permissions;

/// <summary>A screen of the web app and the permission that opens it. A path also covers the paths below it (<c>/admin/users/42</c>).</summary>
public sealed record UiRoute(string Path, string Entity, string Action);

/// <summary>The caller's own permissions, for the UI (US-144). The UI hides what the API would refuse, from the same engine.</summary>
public static class PermissionEndpoints
{
    /// <summary>
    /// Every guarded screen, declared once. <c>/</c> and <c>/access-denied</c> are open to every signed-in user and are not listed.
    /// A screen is offered to the caller when their scope for its (entity, action) is not <c>none</c>.
    /// </summary>
    public static readonly UiRoute[] UiRoutes =
    [
        new("/accounts", "account", "read"),
        new("/contacts", "contact", "read"),
        new("/leads", "lead", "read"),
        new("/opportunities", "opportunity", "read"),
        new("/projects", "project", "read"),
        new("/service-requests", "service_request", "read"),
        new("/time", "time_entry", "read"),
        new("/approvals", "time_entry", "approve"),
        new("/admin/users", "app_user", "update"),
        new("/admin/api-tokens", "api_token", "read"),
        new("/admin/reference-data", "reference_data", "update"),
        new("/admin/audit", "audit_log", "read"),
        new("/admin/retention", "retention_rule", "read"),
        new("/admin/imports", "import_run", "read"),
        new("/admin/dsar", "dsar_request", "read")
    ];

    /// <summary>
    /// <c>GET /api/v1/me/permissions</c>: <c>{roles, permissions: [{entity, action, scope}], routes: [path]}</c> for the signed-in
    /// user. Never cached, so a role change shows within the status recheck window.
    /// </summary>
    public static IEndpointRouteBuilder MapPermissionEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/v1/me/permissions", async (HttpContext context, IPermissionService permissions) =>
        {
            await permissions.EnsureLoadedAsync(context.RequestAborted);
            var user = await context.CurrentUserAsync();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                Roles = user.Roles.Order(StringComparer.Ordinal).ToList(),
                Permissions = permissions.Grants(user),
                Routes = UiRoutes.Where(r => permissions.Scope(user, r.Entity, r.Action) != "none").Select(r => r.Path).ToList()
            });
        }).AsMeEndpoint();
        return routes;
    }
}
