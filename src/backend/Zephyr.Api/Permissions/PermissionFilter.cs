using Zephyr.Api.Auth;
using Zephyr.Api.Common;
using Zephyr.Api.Data;

namespace Zephyr.Api.Permissions;

/// <summary>Endpoint metadata: the entity and action an endpoint needs. The endpoint inventory test reads it.</summary>
public sealed record PermissionRequirement(string Entity, string Action);

/// <summary>
/// Endpoint metadata: the roles that may call an endpoint, for the rule the matrix cannot express (design v22 R7: the
/// by-external-id writes are for Admin and Integration, and Integration is an API client, not a matrix role).
/// </summary>
public sealed record RoleRequirement(IReadOnlyList<string> Roles);

/// <summary>Marks an endpoint for any signed-in user that is about the caller, not an entity: <c>/auth/me</c>, <c>/auth/logout</c>, <c>/api/v1/me/*</c>.</summary>
public sealed class MeEndpointMarker
{
    public static readonly MeEndpointMarker Instance = new();

    private MeEndpointMarker()
    {
    }
}

/// <summary>
/// Declares what an endpoint needs and enforces it before the handler runs (default deny: the endpoint inventory test fails for
/// an endpoint with no declaration). The API is authoritative; the UI route guard only hides what the API would refuse.
/// </summary>
public static class PermissionFilter
{
    private const string AccessKey = "Zephyr.Permissions.Access";

    /// <summary>
    /// 401 when nobody is signed in, 403 problem+json when the caller's widest scope for <paramref name="action"/> on
    /// <paramref name="entity"/> is <c>none</c>. Record-level rules (404 for a record out of scope, 403 for one that can be
    /// seen but not changed) are applied by the handler with <see cref="IPermissionService.FindAsync{T}"/>.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string entity, string action)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new PermissionRequirement(entity, action));
        builder.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            if (context.User.Identity?.IsAuthenticated != true)
            {
                throw Problems.Unauthenticated();
            }

            var permissions = context.RequestServices.GetRequiredService<IPermissionService>();
            await permissions.EnsureLoadedAsync(context.RequestAborted);
            var user = await context.CurrentUserAsync();
            if (permissions.Scope(user, entity, action) == "none")
            {
                throw Problems.Forbidden();
            }

            return await next(invocation);
        });
        return builder;
    }

    /// <summary>403 problem+json unless the signed-in, active user holds one of <paramref name="roles"/> (read from the cached status, never from the token).</summary>
    public static TBuilder RequireRoles<TBuilder>(this TBuilder builder, params string[] roles)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new RoleRequirement(roles));
        builder.AddEndpointFilter(async (invocation, next) =>
        {
            var user = await invocation.HttpContext.CurrentUserAsync();
            if (!user.Roles.Intersect(roles).Any())
            {
                throw Problems.Forbidden();
            }

            return await next(invocation);
        });
        return builder;
    }

    public static TBuilder AsMeEndpoint<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(MeEndpointMarker.Instance);

    /// <summary>
    /// The signed-in user's roles, home country and team from the cached status, or a 403 problem when the caller is not an active
    /// user. Only the user id of the principal is used: roles and scopes in a cookie or a token never widen access, so a personal
    /// token has exactly its owner's permissions. Read once per request.
    /// </summary>
    public static async Task<UserAccess> CurrentUserAsync(this HttpContext context)
    {
        if (context.Items[AccessKey] is UserAccess known)
        {
            return known;
        }

        var userId = context.RequestServices.GetRequiredService<ICurrentUser>().UserId;
        var access = userId is null ? null : await context.RequestServices.GetRequiredService<IUserStatus>().GetAsync(userId.Value, context.RequestAborted);
        if (access is null)
        {
            throw Problems.Forbidden();
        }

        context.Items[AccessKey] = access;
        return access;
    }
}
