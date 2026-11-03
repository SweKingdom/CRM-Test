using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Respawn;
using Respawn.Graph;
using Zephyr.Api.Data;

namespace Zephyr.Api.Tests.Support;

public sealed record FakeUser(Guid? UserId) : ICurrentUser;

/// <summary>
/// A test that needs a PostgreSQL server (ZEPHYR_TEST_DB, falling back to ZEPHYR_DB). Without one it is reported as
/// skipped, except when ZEPHYR_REQUIRE_DB is 1 or true (CI sets it): there a missing server fails the run.
/// </summary>
public sealed class DbFactAttribute : FactAttribute
{
    public DbFactAttribute()
    {
        if (DbFixture.ServerConnectionString() is null && !DbFixture.DbRequired())
        {
            Skip = "No PostgreSQL server configured. Set ZEPHYR_TEST_DB (e.g. the Compose db service) to run this test.";
        }
    }
}

/// <summary>A <see cref="DbFactAttribute"/> for a theory: skipped without a PostgreSQL server, failing when ZEPHYR_REQUIRE_DB is set.</summary>
public sealed class DbTheoryAttribute : TheoryAttribute
{
    public DbTheoryAttribute()
    {
        if (DbFixture.ServerConnectionString() is null && !DbFixture.DbRequired())
        {
            Skip = "No PostgreSQL server configured. Set ZEPHYR_TEST_DB (e.g. the Compose db service) to run this test.";
        }
    }
}

/// <summary>
/// One throw-away database per test run: created, migrated once and dropped at the end; Respawn resets data between tests.
/// The server comes from ZEPHYR_TEST_DB (falls back to ZEPHYR_DB); only the database name is replaced.
/// </summary>
public sealed class DbFixture : IAsyncLifetime
{
    private string serverConnectionString = "";
    private string databaseName = "";
    private Respawner? respawner;

    public string ConnectionString { get; private set; } = "";

    public static string? ServerConnectionString() =>
        Environment.GetEnvironmentVariable("ZEPHYR_TEST_DB") ?? Environment.GetEnvironmentVariable("ZEPHYR_DB");

    public static bool DbRequired() =>
        Environment.GetEnvironmentVariable("ZEPHYR_REQUIRE_DB") is { } value
        && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>The repository root: the first directory above the test binaries that holds package.json.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "package.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("package.json not found above the test binaries");
    }

    public async Task InitializeAsync()
    {
        if (ServerConnectionString() is not { } server)
        {
            // Every DbFact is skipped when no server is configured, so there is nothing to set up.
            if (DbRequired())
            {
                throw new InvalidOperationException("ZEPHYR_REQUIRE_DB is set but ZEPHYR_TEST_DB is not: set it to the PostgreSQL connection string.");
            }

            return;
        }

        serverConnectionString = server;
        databaseName = $"zephyr_test_{Guid.NewGuid():N}";

        var admin = new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = "postgres" };
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = databaseName }.ConnectionString;
        // The API reads its connection string from ZEPHYR_DB; point it at the test database for this run.
        Environment.SetEnvironmentVariable("ZEPHYR_DB", ConnectionString);

        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        await using var respawnConnection = new NpgsqlConnection(ConnectionString);
        await respawnConnection.OpenAsync();
        respawner = await Respawner.CreateAsync(respawnConnection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            // audit_log is append-only: its BEFORE DELETE trigger would make Respawn's DELETE FROM fail.
            // The throw-away database is dropped at the end of the run, so the rows need no cleaning.
            // role and role_permission (US-143) are cleaned like any table: a test that needs them seeds them.
            TablesToIgnore = [new Table("__EFMigrationsHistory"), new Table("audit_log")]
        });
    }

    public async Task DisposeAsync()
    {
        if (databaseName == "")
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        var admin = new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task ResetAsync()
    {
        if (respawner is null)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await respawner.ResetAsync(connection);
    }

    /// <summary>Runs <c>scripts/seed.mjs</c> (Node) against the test database, optionally with another seed directory.</summary>
    public void RunSeedScript(string? seedDir = null)
    {
        var b = new NpgsqlConnectionStringBuilder(ConnectionString);
        var url = $"postgres://{Uri.EscapeDataString(b.Username!)}:{Uri.EscapeDataString(b.Password ?? "")}" +
                  $"@{b.Host}:{b.Port}/{b.Database}";
        var start = new ProcessStartInfo("node") { WorkingDirectory = RepoRoot(), RedirectStandardError = true };
        start.ArgumentList.Add("scripts/seed.mjs");
        if (seedDir != null)
        {
            start.ArgumentList.Add("--dir");
            start.ArgumentList.Add(seedDir);
        }
        start.Environment["DATABASE_URL"] = url;
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    /// <summary>A context for the test database; <paramref name="userId"/> null means no signed-in user.</summary>
    public ZephyrDbContext CreateContext(Guid? userId = null)
    {
        var options = new DbContextOptionsBuilder<ZephyrDbContext>();
        ZephyrDbContext.Configure(options, new AuditInterceptor(userId is null ? null : new FakeUser(userId)));
        return new ZephyrDbContext(options.Options);
    }
}

[CollectionDefinition("db")]
public class DbCollection : ICollectionFixture<DbFixture>
{
}
