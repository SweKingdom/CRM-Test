using Npgsql;
using Zephyr.Api.Data;
using Zephyr.Api.Permissions;
using Zephyr.Api.Tests.Support;

namespace Zephyr.Api.Tests.Permissions;

/// <summary>
/// The permission model (US-143): the CSV matrix, its documentation, the stored tables, the team column and the role keys.
/// Tests that need only the CSV or the docs run without a database; the rest need PostgreSQL.
/// </summary>
[Collection("db")]
public class MatrixTests(DbFixture fixture) : IAsyncLifetime
{
    private const string Header = "role,entity,action,scope\n";

    private static readonly string[] RoleKeys = ["Salesperson", "BDR", "PM", "Consultant", "Finance", "Management", "Admin"];

    private static readonly string[] EntityKeys =
    [
        "account", "contact", "lead", "opportunity", "meddpicc", "activity", "project", "project_team_member", "skill",
        "time_type", "time_entry", "service_request", "app_user", "api_token", "reference_data", "audit_log",
        "retention_rule", "import_run", "dsar_request"
    ];

    private static readonly string[] BusinessEntities =
    [
        "account", "contact", "lead", "opportunity", "meddpicc", "activity", "project", "project_team_member", "time_entry", "service_request"
    ];

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers ----

    private static IReadOnlyList<PermissionRow> Matrix() => PermissionSeeder.Parse(PermissionSeeder.EmbeddedCsv());

    private static string Scope(IReadOnlyList<PermissionRow> rows, string role, string entity, string action) =>
        rows.Single(r => r.Role == role && r.Entity == entity && r.Action == action).Scope;

    private static string[] Sorted(IEnumerable<string> values) => values.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    private static string[] Lines(IEnumerable<PermissionRow> rows) => Sorted(rows.Select(r => $"{r.Role},{r.Entity},{r.Action},{r.Scope}"));

    private async Task SeedAsync(string? csv = null)
    {
        await using var db = fixture.CreateContext();
        if (csv is null)
        {
            await PermissionSeeder.SeedAsync(db);
        }
        else
        {
            await PermissionSeeder.SeedAsync(db, csv);
        }
    }

    private async Task<int> ExecAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync();
    }

    private async Task<string[]> StringsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values.ToArray();
    }

    private async Task<long> CountAsync(string table) => long.Parse((await StringsAsync($"select count(*)::text from {table}"))[0]);

    private async Task<string[]> StoredPermissionsAsync() =>
        Sorted(await StringsAsync("select role_key || ',' || entity || ',' || action || ',' || scope from role_permission"));

    private async Task<string> InsertUserAsync(string name, string? managerId = null)
    {
        var manager = managerId is null ? "null" : $"'{managerId}'";
        return (await StringsAsync(
            $"insert into app_user (name, email, manager_id) values ('{name}', '{name.ToLowerInvariant()}@example.test', {manager}) returning id::text"))[0];
    }

    // ---- AC 1: matrix ----

    [Fact]
    public void Ac1_The_csv_has_one_row_for_every_role_entity_and_action()
    {
        var rows = Matrix();

        Assert.Equal(RoleKeys.Length * EntityKeys.Length * 5, rows.Count);
        Assert.Equal(665, rows.Count);
        foreach (var role in RoleKeys)
        {
            foreach (var entity in EntityKeys)
            {
                foreach (var action in new[] { "read", "create", "update", "delete", "approve" })
                {
                    Assert.Single(rows, r => r.Role == role && r.Entity == entity && r.Action == action);
                }
            }
        }

        Assert.Equal(RoleKeys, PermissionSeeder.Roles.Select(r => r.Key).ToArray());
        Assert.Equal(EntityKeys, PermissionSeeder.Entities);
        Assert.DoesNotContain(rows, r => r.Role == "Integration");
    }

    [Fact]
    public void Ac1_The_matrix_follows_the_product_owner_rules()
    {
        var rows = Matrix();

        // Admin reads business content across the organisation but writes none of it and approves no time.
        foreach (var entity in new[] { "account", "contact", "lead", "opportunity", "project", "time_entry" })
        {
            Assert.Equal("org", Scope(rows, "Admin", entity, "read"));
        }

        // The one exception is the soft delete of an account (US-135 AC 2, US-146 PO answer #156).
        foreach (var entity in BusinessEntities)
        {
            foreach (var action in new[] { "create", "update", "delete", "approve" })
            {
                Assert.Equal(entity == "account" && action == "delete" ? "org" : "none", Scope(rows, "Admin", entity, action));
            }
        }

        Assert.Equal("org", Scope(rows, "Admin", "app_user", "update"));
        Assert.Equal("org", Scope(rows, "Admin", "retention_rule", "approve"));
        Assert.Equal("org", Scope(rows, "Admin", "dsar_request", "update"));

        // Management reads everything and writes only its own time.
        Assert.All(rows.Where(r => r.Role == "Management" && r.Action != "read"),
            r => Assert.Equal(r.Entity == "time_entry" && r.Action != "approve" ? "own" : "none", r.Scope));
        Assert.Equal("org", Scope(rows, "Management", "lead", "read"));

        // Finance changes the status of time entries across the organisation and creates nothing.
        Assert.Equal("org", Scope(rows, "Finance", "time_entry", "update"));
        Assert.Equal("none", Scope(rows, "Finance", "time_entry", "create"));
        Assert.Equal("none", Scope(rows, "Finance", "lead", "read"));

        Assert.Equal("org", Scope(rows, "Salesperson", "account", "read"));
        Assert.Equal("own", Scope(rows, "Salesperson", "account", "update"));
        Assert.Equal("country", Scope(rows, "Salesperson", "lead", "read"));
        Assert.Equal("own", Scope(rows, "Salesperson", "time_entry", "read"));
        Assert.Equal("team", Scope(rows, "BDR", "lead", "update"));
        Assert.Equal("none", Scope(rows, "BDR", "project", "read"));
        Assert.Equal("own", Scope(rows, "PM", "project", "update"));
        Assert.Equal("team", Scope(rows, "PM", "time_entry", "approve"));
        Assert.Equal("none", Scope(rows, "Consultant", "lead", "read"));
        Assert.Equal("own", Scope(rows, "Consultant", "project", "read"));

        // Only a PM approves time and only an Admin approves retention runs.
        Assert.Equal(
            new[] { "Admin,retention_rule", "PM,time_entry" },
            Sorted(rows.Where(r => r.Action == "approve" && r.Scope != "none").Select(r => $"{r.Role},{r.Entity}")));
    }

    [Fact]
    public void Ac1_The_documentation_matrix_table_equals_the_csv_and_lists_the_entities()
    {
        var rows = Matrix();
        var letters = new Dictionary<string, string> { ["read"] = "R", ["create"] = "C", ["update"] = "U", ["delete"] = "D", ["approve"] = "A" };
        var doc = File.ReadAllText(Path.Combine(DbFixture.RepoRoot(), "docs", "permissions.md")).Replace("\r\n", "\n").Split('\n');

        string Cell(string role, string entity)
        {
            var granted = PermissionSeeder.Actions
                .Select(action => (action, scope: Scope(rows, role, entity, action)))
                .Where(x => x.scope != "none")
                .Select(x => $"{letters[x.action]}:{x.scope}");
            var text = string.Join(" ", granted);
            return text == "" ? "—" : text;
        }

        Assert.Contains($"| Entity | {string.Join(" | ", RoleKeys)} |", doc);
        foreach (var entity in EntityKeys)
        {
            Assert.Contains($"| {entity} | {string.Join(" | ", RoleKeys.Select(role => Cell(role, entity)))} |", doc);
        }
    }

    // ---- AC 3: team definition ----

    [Fact]
    public void Ac3_The_documentation_defines_team_and_project_scoped_access_separately()
    {
        var doc = File.ReadAllText(Path.Combine(DbFixture.RepoRoot(), "docs", "permissions.md")).Replace("\r\n", "\n");

        Assert.Contains("## Team", doc);
        Assert.Contains("same non-null `app_user.manager_id`, plus that manager", doc);
        Assert.Contains("## Project-scoped access", doc);
        Assert.Contains("`project_approver`", doc);
    }

    [DbFact]
    public async Task Ac3_manager_id_links_a_user_to_a_manager_and_rejects_self_and_unknown_managers()
    {
        var manager = await InsertUserAsync("Boss");

        var report = await InsertUserAsync("Report", manager);

        Assert.Equal(new[] { manager }, await StringsAsync($"select manager_id::text from app_user where id = '{report}'"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecAsync($"update app_user set manager_id = id where id = '{manager}'"));
        await Assert.ThrowsAsync<PostgresException>(() => InsertUserAsync("Orphan", Guid.NewGuid().ToString()));
    }

    // ---- AC 2: stored model ----

    [DbFact]
    public async Task Ac2_Seeding_stores_seven_roles_and_exactly_the_csv_rows()
    {
        await SeedAsync();

        Assert.Equal(7, await CountAsync("role"));
        Assert.Equal(Sorted(RoleKeys), Sorted(await StringsAsync("select key from role")));
        Assert.Contains(AppUser.AdminRole, RoleKeys);
        var expected = Matrix();
        Assert.Equal(expected.Count, await CountAsync("role_permission"));
        Assert.Equal(Lines(expected), await StoredPermissionsAsync());
    }

    [DbFact]
    public async Task Ac2_Seeding_again_changes_nothing()
    {
        await SeedAsync();
        var roleVersions = await StringsAsync("select xmin::text from role order by key");
        var permissionVersions = await StringsAsync("select xmin::text from role_permission order by role_key, entity, action");

        await SeedAsync();

        Assert.Equal(roleVersions, await StringsAsync("select xmin::text from role order by key"));
        Assert.Equal(permissionVersions, await StringsAsync("select xmin::text from role_permission order by role_key, entity, action"));
        Assert.Equal(7, await CountAsync("role"));
        Assert.Equal(665, await CountAsync("role_permission"));
    }

    [DbFact]
    public async Task Ac2_Seeding_repairs_edited_missing_and_extra_rows()
    {
        await SeedAsync();
        await ExecAsync("update role_permission set scope = 'org' where role_key = 'Consultant' and entity = 'lead' and action = 'read'");
        await ExecAsync("delete from role_permission where role_key = 'Admin' and entity = 'account' and action = 'read'");
        await ExecAsync("insert into role_permission (role_key, entity, action, scope) values ('Admin', 'ghost', 'read', 'org')");
        await ExecAsync("insert into role (key, label) values ('Intern', 'Intern')");
        await ExecAsync("insert into role_permission (role_key, entity, action, scope) values ('Intern', 'account', 'read', 'org')");
        await ExecAsync("update role set label = 'Changed' where key = 'PM'");

        await SeedAsync();

        Assert.Equal(7, await CountAsync("role"));
        Assert.Equal(Lines(Matrix()), await StoredPermissionsAsync());
        Assert.Equal(new[] { "Project manager and approver" }, await StringsAsync("select label from role where key = 'PM'"));
    }

    [DbFact]
    public async Task Ac2_The_tables_reject_an_unknown_role_action_or_scope()
    {
        await SeedAsync();

        await Assert.ThrowsAsync<PostgresException>(() => ExecAsync("insert into role_permission values ('Nobody', 'account', 'read', 'org')"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecAsync("insert into role_permission values ('Admin', 'ghost', 'export', 'org')"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecAsync("insert into role_permission values ('Admin', 'ghost', 'read', 'everyone')"));
    }

    // ---- AC 4: integrity ----

    [Theory]
    [InlineData("Intern,account,read,org\n", "line 2: unknown role 'Intern'")]
    [InlineData("admin,account,read,org\n", "line 2: unknown role 'admin'")]
    [InlineData("Admin,invoice,read,org\n", "line 2: unknown entity 'invoice'")]
    [InlineData("Admin,account,export,org\n", "line 2: unknown action 'export'")]
    [InlineData("Admin,account,read,everyone\n", "line 2: unknown scope 'everyone'")]
    [InlineData("Admin,account,read,org\nAdmin,account,read,own\n", "line 3: duplicate row for Admin, account, read")]
    [InlineData("Admin,account,read\n", "line 2: expected 4 columns")]
    public void Ac4_Parse_rejects_an_unknown_value_a_duplicate_or_a_short_row(string rows, string message)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => PermissionSeeder.Parse(Header + rows));

        Assert.Contains(message, exception.Message);
    }

    [Theory]
    [InlineData("role,entity,action\nAdmin,account,read\n", "the header must be")]
    [InlineData("", "the file is empty")]
    public void Ac4_Parse_rejects_a_wrong_header_and_an_empty_file(string csv, string message)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => PermissionSeeder.Parse(csv));

        Assert.Contains(message, exception.Message);
    }

    [Fact]
    public void Ac4_Parse_reports_every_bad_line_at_once_and_accepts_windows_line_endings()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            PermissionSeeder.Parse(Header + "Intern,account,read,org\nAdmin,invoice,read,org\nAdmin,account,read,org\n"));

        Assert.Contains("line 2", exception.Message);
        Assert.Contains("line 3", exception.Message);
        Assert.DoesNotContain("line 4", exception.Message);
        Assert.Single(PermissionSeeder.Parse("role,entity,action,scope\r\nAdmin,account,read,org\r\n"));
    }

    [DbFact]
    public async Task Ac4_An_invalid_csv_writes_nothing()
    {
        await SeedAsync();
        var before = await StoredPermissionsAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SeedAsync(Header + "Admin,account,read,org\nAdmin,account,read,none\nIntern,lead,read,org\n"));

        Assert.Equal(before, await StoredPermissionsAsync());
        Assert.Equal(7, await CountAsync("role"));
    }

    [DbFact]
    public async Task Ac4_An_action_that_is_not_listed_has_no_row_which_means_none()
    {
        await SeedAsync(Header + "Salesperson,account,read,org\n");

        Assert.Equal(new[] { "Salesperson,account,read,org" }, await StoredPermissionsAsync());
        Assert.Equal(7, await CountAsync("role"));
        Assert.Equal(new[] { "none" }, await StringsAsync(
            "select coalesce((select scope from role_permission where role_key = 'Salesperson' and entity = 'account' and action = 'delete'), 'none')"));
    }

    // ---- AC 5: role keys ----

    private const string UnknownRoles =
        "select distinct r from app_user, unnest(roles) as r where r not in (select key from role)";

    [DbFact]
    public async Task Ac5_Roles_on_users_created_by_the_seeds_are_existing_role_keys()
    {
        await SeedAsync();
        fixture.RunSeedScript();

        Assert.Empty(await StringsAsync(UnknownRoles));

        // The check itself works: a role that is not a role key is reported (keys are case sensitive).
        await ExecAsync("insert into app_user (name, email, roles) values ('A', 'a@example.test', '{Admin,Salesperson}')");
        await ExecAsync("insert into app_user (name, email, roles) values ('B', 'b@example.test', '{Integration,admin}')");
        Assert.Equal(new[] { "Integration", "admin" }, Sorted(await StringsAsync(UnknownRoles)));
    }

    // ---- test database reset ----

    [DbFact]
    public async Task The_reset_between_tests_clears_the_role_tables_and_manager_links()
    {
        // Regression: Respawn must be able to clean role, role_permission and the app_user self reference.
        await SeedAsync();
        await InsertUserAsync("Report", await InsertUserAsync("Boss"));

        await fixture.ResetAsync();

        Assert.Equal(0, await CountAsync("role"));
        Assert.Equal(0, await CountAsync("role_permission"));
        Assert.Equal(0, await CountAsync("app_user"));
    }
}
