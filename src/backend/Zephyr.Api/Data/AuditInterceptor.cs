using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Zephyr.Api.Data;

/// <summary>The authenticated user of the current request, if any (provided by the sign-in story).</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
}

/// <summary>
/// The user of the authenticated request: the app_user id in the Zephyr-specific <c>zephyr_user_id</c> claim.
/// Sign-in (US-140) and personal API tokens (US-142) set it; the identity provider's own <c>sub</c> is not an app user id.
/// Never read from a request body. Null when the request is anonymous or the claim is not a user id
/// (writes then use <see cref="SystemUser"/>).
/// </summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public const string ClaimType = "zephyr_user_id";

    public Guid? UserId
    {
        get
        {
            var principal = accessor.HttpContext?.User;
            if (principal?.Identity?.IsAuthenticated != true)
            {
                return null;
            }

            return Guid.TryParse(principal.FindFirst(ClaimType)?.Value, out var id) ? id : null;
        }
    }
}

public static class SystemUser
{
    /// <summary>Fixed app_user id used for seeds, migrations and other writes without a signed-in user.</summary>
    public static readonly Guid Id = new("00000000-0000-0000-0000-000000000001");
}

/// <summary>Sets the audit columns on every <see cref="IEntity"/> write and turns deletes into soft deletes.</summary>
public class AuditInterceptor(ICurrentUser? currentUser = null) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return new ValueTask<InterceptionResult<int>>(result);
    }

    private static void Touch(EntityEntry entry, DateTime now, Guid user)
    {
        entry.Property(nameof(IEntity.UpdatedAt)).CurrentValue = now;
        entry.Property(nameof(IEntity.UpdatedBy)).CurrentValue = user;
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        // PostgreSQL keeps microseconds. Truncating here makes the in-memory updated_at equal the stored one,
        // so an ETag computed right after a save equals the ETag of the next read.
        var ticks = DateTime.UtcNow.Ticks;
        var now = new DateTime(ticks - ticks % 10, DateTimeKind.Utc);
        var user = currentUser?.UserId ?? SystemUser.Id;

        foreach (var entry in context.ChangeTracker.Entries<IEntity>().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Property(nameof(IEntity.CreatedAt)).CurrentValue = now;
                    entry.Property(nameof(IEntity.UpdatedAt)).CurrentValue = now;
                    entry.Property(nameof(IEntity.CreatedBy)).CurrentValue = user;
                    break;
                case EntityState.Deleted:
                    entry.State = EntityState.Modified;
                    foreach (var property in entry.Properties)
                    {
                        property.IsModified = false;
                    }

                    entry.Property(nameof(IEntity.DeletedAt)).CurrentValue = now;
                    Touch(entry, now, user);
                    break;
                case EntityState.Modified:
                    entry.Property(nameof(IEntity.CreatedAt)).IsModified = false;
                    entry.Property(nameof(IEntity.CreatedBy)).IsModified = false;
                    Touch(entry, now, user);
                    break;
            }
        }
    }
}
