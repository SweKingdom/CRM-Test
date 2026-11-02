using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Zephyr.Api.Data;

/// <summary>Conventions shared by every table (ADR-003). Audit columns are set by <see cref="AuditInterceptor"/>.</summary>
public interface IEntity
{
    Guid Id { get; set; }
    DateTime CreatedAt { get; set; }
    DateTime UpdatedAt { get; set; }
    Guid? CreatedBy { get; set; }
    Guid? UpdatedBy { get; set; }
    DateTime? DeletedAt { get; set; }

    /// <summary>Custom fields as a JSON object.</summary>
    string Custom { get; set; }
}

/// <summary>Id of an entity in an external system (Dynamics GUID, Fortnox ID). The only place such ids are stored.</summary>
public class ExternalId : IEntity
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string Custom { get; set; } = "{}";
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public string System { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>A customer or prospect company. Minimal table from US-130; the account feature adds the rest of its columns.</summary>
public class Account : IEntity
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string Custom { get; set; } = "{}";
    public string Name { get; set; } = "";
    public string? CountryCode { get; set; }
}

/// <summary>
/// A person who can sign in (US-140). Users are pre-provisioned: <see cref="Issuer"/> and <see cref="Subject"/> stay null
/// until the first sign-in links the identity provider's user. No password or secret of any kind is stored.
/// </summary>
public class AppUser : IEntity
{
    public const string AdminRole = "Admin";

    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string Custom { get; set; } = "{}";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Issuer { get; set; }
    public string? Subject { get; set; }
    public string? HomeCountry { get; set; }

    /// <summary>Salesperson, BDR, PM, Consultant, Finance, Management, Admin.</summary>
    public string[] Roles { get; set; } = [];

    public bool Active { get; set; } = true;
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>Append-only record of who did what (enforced by a database trigger).</summary>
public class AuditLog
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public DateTime At { get; set; }
    public string? Detail { get; set; }
}

public class ZephyrDbContext(DbContextOptions<ZephyrDbContext> options) : DbContext(options)
{
    public DbSet<ExternalId> ExternalIds => Set<ExternalId>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public static string ConnectionStringFromEnvironment() =>
        Environment.GetEnvironmentVariable("ZEPHYR_DB")
        ?? throw new InvalidOperationException("ZEPHYR_DB is not set. Set it to the PostgreSQL connection string.");

    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, params IInterceptor[] interceptors) =>
        options
            .UseNpgsql(ConnectionStringFromEnvironment())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptors);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        foreach (var type in modelBuilder.Model.GetEntityTypes().Where(t => typeof(IEntity).IsAssignableFrom(t.ClrType)).ToList())
        {
            var entity = modelBuilder.Entity(type.ClrType);
            entity.Property(nameof(IEntity.Id)).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(nameof(IEntity.CreatedAt)).HasDefaultValueSql("now()");
            // updated_at is the concurrency token: an UPDATE only succeeds while updated_at is still the value that was
            // read, which is what makes PATCH with If-Match race-free (ETag = id + updated_at).
            entity.Property(nameof(IEntity.UpdatedAt)).HasDefaultValueSql("now()").IsConcurrencyToken();
            entity.Property(nameof(IEntity.Custom)).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");

            // Soft delete: default queries exclude rows with deleted_at set.
            var row = Expression.Parameter(type.ClrType, "e");
            var notDeleted = Expression.Equal(
                Expression.Property(row, nameof(IEntity.DeletedAt)),
                Expression.Constant(null, typeof(DateTime?)));
            entity.HasQueryFilter(Expression.Lambda(notDeleted, row));
        }

        modelBuilder.Entity<ExternalId>(e =>
        {
            e.ToTable("external_id", t => t.HasCheckConstraint("ck_external_id_system", "\"system\" IN ('dynamics', 'fortnox')"));
            e.HasIndex(x => new { x.System, x.EntityType, x.Value }).IsUnique().HasDatabaseName("ux_external_id_system_entity_type_value");
            e.HasIndex(x => new { x.EntityType, x.EntityId }).HasDatabaseName("ix_external_id_entity_type_entity_id");
        });

        // The table comes from migration 0002 (primary key account_pkey, expression index ix_account_name_lower).
        modelBuilder.Entity<Account>(e =>
        {
            e.ToTable("account");
            e.HasKey(x => x.Id).HasName("account_pkey");
            e.Property(x => x.CountryCode).HasColumnType("character(2)");
        });

        // The unique index on lower(email) (an expression index) is created by the migration only.
        // active and roles have no model default: EF must always send them, a default would swallow active = false.
        modelBuilder.Entity<AppUser>(e =>
        {
            e.ToTable("app_user");
            e.Property(x => x.HomeCountry).HasColumnType("character(2)");
            e.Property(x => x.Roles).HasColumnType("text[]");
            e.HasIndex(x => new { x.Issuer, x.Subject }).IsUnique().HasFilter("issuer is not null and subject is not null")
                .HasDatabaseName("ux_app_user_issuer_subject");
        });

        modelBuilder.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_log");
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.At).HasDefaultValueSql("now()");
            e.Property(x => x.Detail).HasColumnType("jsonb");
            e.HasIndex(x => new { x.EntityType, x.EntityId, x.At }).HasDatabaseName("ix_audit_log_entity_type_entity_id_at");
        });
    }
}

/// <summary>Used by <c>dotnet ef</c> at design time (reads ZEPHYR_DB).</summary>
public class ZephyrDbContextFactory : IDesignTimeDbContextFactory<ZephyrDbContext>
{
    public ZephyrDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ZephyrDbContext>();
        ZephyrDbContext.Configure(options);
        return new ZephyrDbContext(options.Options);
    }
}
