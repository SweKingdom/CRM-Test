using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zephyr.Api.Data;

namespace Zephyr.Api.Permissions;

/// <summary>One cell of the permission matrix: what <paramref name="Role"/> may do (<paramref name="Action"/>) to <paramref name="Entity"/>, and how widely.</summary>
public sealed record PermissionRow(string Role, string Entity, string Action, string Scope);

/// <summary>
/// The roles, entities, actions and scopes of the permission model (US-143) and the seeder that loads
/// <c>permission-matrix.csv</c> into <c>role</c> and <c>role_permission</c>. The CSV is the single source of truth: it is
/// validated before the database is touched, and after a seed the tables hold exactly the CSV (no admin edits them).
/// A (role, entity, action) that is absent from the tables means <c>none</c>.
/// </summary>
public static class PermissionSeeder
{
    /// <summary>The seven roles. <c>Integration</c> is an API client, not a user role, so it is not here. Keys match <c>app_user.roles</c>.</summary>
    public static readonly (string Key, string Label)[] Roles =
    [
        ("Salesperson", "Salesperson"),
        ("BDR", "Business development representative"),
        ("PM", "Project manager and approver"),
        ("Consultant", "Consultant"),
        ("Finance", "Finance"),
        ("Management", "Management"),
        ("Admin", "Administrator")
    ];

    public static readonly string[] Entities =
    [
        "account", "contact", "lead", "opportunity", "meddpicc", "activity", "project", "project_team_member", "skill",
        "time_type", "time_entry", "service_request", "app_user", "api_token", "reference_data", "audit_log",
        "retention_rule", "import_run", "dsar_request"
    ];

    public static readonly string[] Actions = ["read", "create", "update", "delete", "approve"];

    public static readonly string[] Scopes = ["none", "own", "team", "country", "org"];

    private const string Header = "role,entity,action,scope";
    private const string ResourceName = "permission-matrix.csv";

    /// <summary>The CSV embedded in the API assembly.</summary>
    public static string EmbeddedCsv()
    {
        using var stream = typeof(PermissionSeeder).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"{ResourceName} is not embedded in the API assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The rows of <paramref name="csv"/>. Throws <see cref="InvalidOperationException"/> naming every bad line when the header is
    /// wrong, a row does not have four columns, a role, entity, action or scope is unknown, or a (role, entity, action) repeats.
    /// </summary>
    public static IReadOnlyList<PermissionRow> Parse(string csv)
    {
        var errors = new List<string>();
        var rows = new List<PermissionRow>();
        var seen = new HashSet<(string, string, string)>();
        var sawHeader = false;
        var number = 0;
        foreach (var raw in csv.Split('\n'))
        {
            number++;
            var line = raw.Trim();
            if (line == "")
            {
                continue;
            }

            if (!sawHeader)
            {
                sawHeader = true;
                if (line != Header)
                {
                    errors.Add($"line {number}: the header must be '{Header}'.");
                }

                continue;
            }

            var cells = line.Split(',').Select(c => c.Trim()).ToArray();
            if (cells.Length != 4)
            {
                errors.Add($"line {number}: expected 4 columns (role, entity, action, scope) but found {cells.Length}.");
                continue;
            }

            var (role, entity, action, scope) = (cells[0], cells[1], cells[2], cells[3]);
            var before = errors.Count;
            if (!Roles.Any(r => r.Key == role))
            {
                errors.Add($"line {number}: unknown role '{role}'. Known roles: {string.Join(", ", Roles.Select(r => r.Key))}.");
            }

            if (!Entities.Contains(entity))
            {
                errors.Add($"line {number}: unknown entity '{entity}'.");
            }

            if (!Actions.Contains(action))
            {
                errors.Add($"line {number}: unknown action '{action}'. Known actions: {string.Join(", ", Actions)}.");
            }

            if (!Scopes.Contains(scope))
            {
                errors.Add($"line {number}: unknown scope '{scope}'. Known scopes: {string.Join(", ", Scopes)}.");
            }

            if (errors.Count > before)
            {
                continue;
            }

            if (!seen.Add((role, entity, action)))
            {
                errors.Add($"line {number}: duplicate row for {role}, {entity}, {action}.");
                continue;
            }

            rows.Add(new PermissionRow(role, entity, action, scope));
        }

        if (!sawHeader)
        {
            errors.Add($"the file is empty; it must start with '{Header}'.");
        }

        return errors.Count == 0
            ? rows
            : throw new InvalidOperationException($"{ResourceName} is invalid:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
    }

    public static Task SeedAsync(ZephyrDbContext db, CancellationToken ct = default) => SeedAsync(db, EmbeddedCsv(), ct);

    /// <summary>
    /// Makes <c>role</c> and <c>role_permission</c> equal to <paramref name="csv"/> in one transaction. An invalid CSV throws before
    /// anything is written. Running it again with the same CSV writes nothing (unchanged rows are not touched).
    /// </summary>
    public static async Task SeedAsync(ZephyrDbContext db, string csv, CancellationToken ct = default)
    {
        var rows = Parse(csv);
        var keys = Roles.Select(r => r.Key).ToArray();
        var labels = Roles.Select(r => r.Label).ToArray();

        // A parameter object belongs to one command, so every statement gets its own.
        NpgsqlParameter Column(string name, Func<PermissionRow, string> value) => new(name, rows.Select(value).ToArray());
        NpgsqlParameter RoleKeys() => new("k", keys);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            """
            insert into role (key, label)
            select * from unnest(@k::text[], @l::text[])
            on conflict (key) do update set label = excluded.label where role.label is distinct from excluded.label
            """,
            new object[] { RoleKeys(), new NpgsqlParameter("l", labels) }, ct);
        await db.Database.ExecuteSqlRawAsync(
            """
            insert into role_permission (role_key, entity, action, scope)
            select * from unnest(@r::text[], @e::text[], @a::text[], @s::text[])
            on conflict (role_key, entity, action) do update set scope = excluded.scope
            where role_permission.scope is distinct from excluded.scope
            """,
            new object[] { Column("r", x => x.Role), Column("e", x => x.Entity), Column("a", x => x.Action), Column("s", x => x.Scope) }, ct);
        // Rows that are no longer in the CSV go, then roles that are no longer defined.
        await db.Database.ExecuteSqlRawAsync(
            """
            delete from role_permission rp
            where not exists (
                select 1 from unnest(@r::text[], @e::text[], @a::text[]) as t (r, e, a)
                where t.r = rp.role_key and t.e = rp.entity and t.a = rp.action)
            """,
            new object[] { Column("r", x => x.Role), Column("e", x => x.Entity), Column("a", x => x.Action) }, ct);
        await db.Database.ExecuteSqlRawAsync("delete from role where key <> all(@k::text[])", new object[] { RoleKeys() }, ct);
        await transaction.CommitAsync(ct);
    }
}
