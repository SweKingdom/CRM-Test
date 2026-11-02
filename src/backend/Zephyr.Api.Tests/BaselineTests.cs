using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zephyr.Api.Data;
using Zephyr.Api.Tests.Support;

namespace Zephyr.Api.Tests;

[Collection("db")]
public class BaselineTests(DbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static ExternalId NewExternalId(string system = "dynamics", string value = "guid-1") =>
        new() { EntityType = "account", EntityId = Guid.NewGuid(), System = system, Value = value };

    [DbFact]
    public async Task Ac1_Extensions_pg_trgm_and_unaccent_exist()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT extname FROM pg_extension WHERE extname IN ('pg_trgm', 'unaccent') ORDER BY extname", connection);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        Assert.Equal(new[] { "pg_trgm", "unaccent" }, names);
    }

    [DbFact]
    public async Task Ac2_Migrating_twice_succeeds_and_applies_nothing()
    {
        await using var db = fixture.CreateContext();
        var before = (await db.Database.GetAppliedMigrationsAsync()).ToList();

        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(before, await db.Database.GetAppliedMigrationsAsync());
        Assert.Contains("0001_Baseline", before);
    }

    [DbFact]
    public async Task Ac3_External_id_rejects_duplicate_system_entity_type_value()
    {
        await using (var db = fixture.CreateContext())
        {
            db.ExternalIds.Add(NewExternalId());
            await db.SaveChangesAsync();
        }

        await using var duplicate = fixture.CreateContext();
        duplicate.ExternalIds.Add(NewExternalId());

        await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
    }

    [DbFact]
    public async Task Ac3_External_id_allows_same_value_in_another_system()
    {
        await using var db = fixture.CreateContext();
        db.ExternalIds.Add(NewExternalId("dynamics"));
        db.ExternalIds.Add(NewExternalId("fortnox"));

        Assert.Equal(2, await db.SaveChangesAsync());
    }

    [DbFact]
    public async Task Ac3_External_id_rejects_unknown_system()
    {
        await using var db = fixture.CreateContext();
        db.ExternalIds.Add(NewExternalId("salesforce"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [DbFact]
    public async Task Ac4_Audit_columns_come_from_the_current_user_on_insert_and_update()
    {
        var author = Guid.NewGuid();
        var editor = Guid.NewGuid();
        Guid id;
        await using (var db = fixture.CreateContext(author))
        {
            var row = NewExternalId();
            db.ExternalIds.Add(row);
            await db.SaveChangesAsync();
            id = row.Id;
        }

        await using (var db = fixture.CreateContext(editor))
        {
            var row = await db.ExternalIds.SingleAsync(x => x.Id == id);
            row.Value = "guid-2";
            await db.SaveChangesAsync();
        }

        await using var check = fixture.CreateContext();
        var saved = await check.ExternalIds.SingleAsync(x => x.Id == id);
        Assert.Equal(author, saved.CreatedBy);
        Assert.Equal(editor, saved.UpdatedBy);
        Assert.Equal(DateTimeKind.Utc, saved.UpdatedAt.Kind);
        Assert.True(saved.UpdatedAt > saved.CreatedAt);
        Assert.Equal("guid-2", saved.Value);
    }

    [DbFact]
    public async Task Ac4_Without_a_user_the_system_user_id_is_used()
    {
        await using var db = fixture.CreateContext();
        var row = NewExternalId();
        db.ExternalIds.Add(row);
        await db.SaveChangesAsync();

        row.Value = "guid-2";
        await db.SaveChangesAsync();

        Assert.Equal(SystemUser.Id, row.CreatedBy);
        Assert.Equal(SystemUser.Id, row.UpdatedBy);
    }

    [DbFact]
    public async Task Ac5_Delete_sets_deleted_at_and_default_queries_exclude_the_row()
    {
        var deleter = Guid.NewGuid();
        Guid id;
        await using (var db = fixture.CreateContext())
        {
            var row = NewExternalId();
            db.ExternalIds.Add(row);
            await db.SaveChangesAsync();
            id = row.Id;
        }

        await using (var db = fixture.CreateContext(deleter))
        {
            db.ExternalIds.Remove(await db.ExternalIds.SingleAsync(x => x.Id == id));
            await db.SaveChangesAsync();
        }

        await using var check = fixture.CreateContext();
        Assert.Empty(await check.ExternalIds.ToListAsync());
        var deleted = await check.ExternalIds.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
        Assert.NotNull(deleted.DeletedAt);
        Assert.Equal(deleter, deleted.UpdatedBy);
    }

    [DbFact]
    public async Task Audit_log_is_append_only()
    {
        await using var db = fixture.CreateContext();
        var entry = new AuditLog { Action = "create", EntityType = "account", EntityId = Guid.NewGuid() };
        db.AuditLogs.Add(entry);
        await db.SaveChangesAsync();

        entry.Action = "changed";

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [DbFact]
    public async Task Reset_between_tests_works_while_audit_log_holds_rows()
    {
        // Regression: Respawn's DELETE FROM must not hit the append-only trigger on audit_log.
        await using (var db = fixture.CreateContext())
        {
            db.AuditLogs.Add(new AuditLog { Action = "create", EntityType = "account", EntityId = Guid.NewGuid() });
            db.ExternalIds.Add(NewExternalId());
            await db.SaveChangesAsync();
        }

        await fixture.ResetAsync();

        await using var check = fixture.CreateContext();
        Assert.Empty(await check.ExternalIds.ToListAsync());
        Assert.NotEmpty(await check.AuditLogs.ToListAsync());
    }
}
