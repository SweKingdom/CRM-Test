using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zephyr.Api.Data;
using Zephyr.Api.Tests.Support;

namespace Zephyr.Api.Tests.Seeds;

/// <summary>
/// AC 1-3 of US-131 against the migrated test database: runs `scripts/seed.mjs --volume` with Node on whichever of the
/// entity tables exist (today only account). AC 4 is declared in targets.json and checked by tests/volume.test.mjs.
/// </summary>
[Collection("db")]
public class VolumeSeedTests(DbFixture db) : IAsyncLifetime
{
    private static readonly string[] VolumeArgs = ["--volume", "--seed", "7", "--anchor", "2026-06-15"];

    private readonly List<string> extraDatabases = [];

    public Task InitializeAsync() => db.ResetAsync();

    public async Task DisposeAsync()
    {
        if (extraDatabases.Count == 0)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        var admin = new NpgsqlConnectionStringBuilder(DbFixture.ServerConnectionString()) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        foreach (var name in extraDatabases)
        {
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "package.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("package.json not found above the test binaries");
    }

    /// <summary>Entity name and table from targets.json, in load order.</summary>
    private static List<(string Entity, string Table)> Entities()
    {
        var path = Path.Combine(RepoRoot(), "tools", "search-benchmark", "generate", "targets.json");
        using var targets = JsonDocument.Parse(File.ReadAllText(path));
        return targets.RootElement.GetProperty("entities").EnumerateObject()
            .Select(e => (e.Name, e.Value.GetProperty("table").GetString()!))
            .ToList();
    }

    private static (int ExitCode, string Output, string Error) Seed(string connectionString, params string[] args)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        var url = $"postgres://{Uri.EscapeDataString(b.Username!)}:{Uri.EscapeDataString(b.Password ?? "")}" +
                  $"@{b.Host}:{b.Port}/{b.Database}";
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("scripts/seed.mjs");
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["DATABASE_URL"] = url;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.GetAwaiter().GetResult(), error);
    }

    private static T Scalar<T>(string connectionString, string sql)
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(sql, conn);
        return (T)cmd.ExecuteScalar()!;
    }

    private static bool TableExists(string connectionString, string table) =>
        Scalar<bool>(connectionString, $"select to_regclass('public.{table}') is not null");

    /// <summary>Entities whose table exists in the database, with the table name.</summary>
    private static List<(string Entity, string Table)> Loaded(string connectionString) =>
        Entities().Where(e => TableExists(connectionString, e.Table)).ToList();

    /// <summary>
    /// Checksum per entity: md5(string_agg(id::text || name, ',' ORDER BY id)). Time entries have no name, so they use
    /// id, user_id, work_date, duration_minutes and time_type. Nullable columns are wrapped in coalesce, because a
    /// null would make the whole concatenated value null and string_agg would skip the row.
    /// </summary>
    private static string Checksum(string connectionString, string entity, string table)
    {
        var value = entity == "time_entry"
            ? "id::text || coalesce(user_id::text, '') || coalesce(work_date::text, '') || " +
              "coalesce(duration_minutes::text, '') || coalesce(time_type::text, '')"
            : "id::text || coalesce(name, '')";
        return Scalar<string>(
            connectionString,
            $"select md5(string_agg({value}, ',' order by id)) from {table}");
    }

    private async Task<string> CreateFreshDatabaseAsync()
    {
        var server = DbFixture.ServerConnectionString()!;
        var name = $"zephyr_test_{Guid.NewGuid():N}";
        extraDatabases.Add(name);
        var admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres" };
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
        var options = new DbContextOptionsBuilder<ZephyrDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var context = new ZephyrDbContext(options);
        await context.Database.MigrateAsync();
        return connectionString;
    }

    [DbFact]
    public void Ac1_PartialSchema_SkipsMissingEntitiesAndLoadsTheRest()
    {
        var result = Seed(db.ConnectionString, VolumeArgs);

        Assert.True(result.ExitCode == 0, result.Error);
        var loaded = Loaded(db.ConnectionString);
        Assert.Contains(loaded, e => e.Entity == "account");
        foreach (var (entity, table) in Entities())
        {
            var skipLine = $"SKIP {entity}: table missing";
            if (loaded.Any(e => e.Entity == entity))
            {
                Assert.DoesNotContain(skipLine, result.Output);
                Assert.True(Scalar<long>(db.ConnectionString, $"select count(*) from {table}") > 0, table);
            }
            else
            {
                Assert.Contains(skipLine, result.Output);
            }
        }

        Assert.Equal(6000, Scalar<long>(db.ConnectionString, "select count(*) from account"));
    }

    [DbFact]
    public async Task Ac2_SameSeedOnTwoFreshDatabases_GivesIdenticalChecksums()
    {
        var other = await CreateFreshDatabaseAsync();

        Assert.Equal(0, Seed(db.ConnectionString, VolumeArgs).ExitCode);
        Assert.Equal(0, Seed(other, VolumeArgs).ExitCode);

        foreach (var (entity, table) in Loaded(db.ConnectionString))
        {
            var first = Checksum(db.ConnectionString, entity, table);
            Assert.NotNull(first);
            Assert.Equal(first, Checksum(other, entity, table));
        }

        // A different seed must change the data, otherwise the checksum would prove nothing.
        var before = Checksum(db.ConnectionString, "account", "account");
        var changed = Seed(db.ConnectionString, "--volume", "--seed", "8", "--anchor", "2026-06-15", "--reset");
        Assert.True(changed.ExitCode == 0, changed.Error);
        Assert.NotEqual(before, Checksum(db.ConnectionString, "account", "account"));
    }

    [DbFact]
    public void Ac3_EveryRowIsMarked_SecondRunRefuses_ResetRemovesOnlySyntheticRows()
    {
        Scalar<long>(db.ConnectionString, "with i as (insert into account (name) values ('Keep Me AB') returning 1) select count(*) from i");

        Assert.Equal(0, Seed(db.ConnectionString, VolumeArgs).ExitCode);
        foreach (var (entity, table) in Loaded(db.ConnectionString))
        {
            // The one hand-made account is the only row without the marker.
            var unmarked = entity == "account" ? 1 : 0;
            Assert.Equal(
                unmarked,
                Scalar<long>(db.ConnectionString, $"select count(*) from {table} where custom->>'synthetic' is distinct from 'true'"));
        }

        Assert.Equal(6001, Scalar<long>(db.ConnectionString, "select count(*) from account"));

        var second = Seed(db.ConnectionString, VolumeArgs);
        Assert.NotEqual(0, second.ExitCode);
        Assert.Contains("--reset", second.Error);
        Assert.Equal(6001, Scalar<long>(db.ConnectionString, "select count(*) from account"));

        var reset = Seed(db.ConnectionString, VolumeArgs.Append("--reset").ToArray());
        Assert.True(reset.ExitCode == 0, reset.Error);
        Assert.Equal(6001, Scalar<long>(db.ConnectionString, "select count(*) from account"));
        Assert.Equal(1, Scalar<long>(db.ConnectionString, "select count(*) from account where name = 'Keep Me AB'"));
    }
}
