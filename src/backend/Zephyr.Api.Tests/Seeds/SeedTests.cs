using System.Diagnostics;
using Npgsql;
using Zephyr.Api.Tests.Support;

namespace Zephyr.Api.Tests.Seeds;

/// <summary>AC 1-4 of US-130 and the exchange-rate seed of US-134 against the migrated test database. Runs scripts/seed.mjs with Node.</summary>
[Collection("db")]
public class SeedTests(DbFixture db) : IAsyncLifetime
{
    private const string EkdalId = "00000000-0000-4000-8000-0000000d0002";

    private static readonly string[] AllTables =
    {
        "stage", "close_outcome", "close_reason", "call_outcome", "time_type",
        "country", "currency", "meddpicc_element", "account", "exchange_rate",
    };

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "package.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("package.json not found above the test binaries");
    }

    private void Seed(string? seedDir = null)
    {
        var b = new NpgsqlConnectionStringBuilder(db.ConnectionString);
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

    private T Scalar<T>(string sql)
    {
        using var conn = new NpgsqlConnection(db.ConnectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(sql, conn);
        return (T)cmd.ExecuteScalar()!;
    }

    private long Rows(string table) => Scalar<long>($"select count(*) from {table}");

    [DbFact]
    public void Ac1_ReferenceData_IsLoaded()
    {
        Seed();
        Assert.Equal(9, Rows("call_outcome"));
        Assert.Equal(11, Rows("time_type"));
        Assert.Equal(6, Rows("country"));
        Assert.Equal(6, Rows("currency"));
        Assert.Equal(1, Scalar<long>("select count(*) from currency where key = 'SEK'"));
        Assert.Equal(8, Rows("meddpicc_element"));
        Assert.Equal(
            "discover,qualify,validate,propose,close,won,lost",
            Scalar<string>("select string_agg(key, ',' order by sort_order) from stage"));
    }

    [DbFact]
    public void Ac2_Seed_IsIdempotent()
    {
        Seed();
        var before = AllTables.Select(Rows).ToArray();
        Seed();
        Assert.Equal(before, AllTables.Select(Rows).ToArray());
    }

    [DbFact]
    public void Ac3_ReferenceRow_IsUpdatedInPlace()
    {
        Seed();
        var id = Scalar<Guid>("select id from country where key = 'SE'");
        var temp = Path.Combine(Path.GetTempPath(), "seed_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), "Seed"), "*.sql"))
            {
                File.WriteAllText(
                    Path.Combine(temp, Path.GetFileName(file)),
                    File.ReadAllText(file).Replace("'Sweden'", "'Sverige'"));
            }

            Seed(temp);
            Assert.Equal(id, Scalar<Guid>("select id from country where key = 'SE'"));
            Assert.Equal("Sverige", Scalar<string>("select label from country where key = 'SE'"));
            Assert.Equal(6, Rows("country"));
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [DbFact]
    public void Ac4_DemoAccounts_AreNeverOverwritten()
    {
        Seed();
        Assert.Equal(3, Scalar<long>(
            "select count(*) from account where name in ('Lindholm Demo AB', 'Ekdal Demo AB', 'Bellini Demo Srl')"));
        Scalar<int>($"with u as (update account set name = 'Edited Name' where id = '{EkdalId}' returning 1) select count(*)::int from u");
        Seed();
        Assert.Equal("Edited Name", Scalar<string>($"select name from account where id = '{EkdalId}'"));
        Assert.Equal(3, Rows("account"));
    }

    [DbFact]
    public void US134_ExchangeRates_AreSeededForEveryMonthWithTheFixedRates_AndAdminEditsSurviveAReseed()
    {
        Seed();
        // Five currencies (SEK is implicit) x 132 months (2020-01 to 2030-12).
        Assert.Equal(5 * 132, Rows("exchange_rate"));
        Assert.Equal(0, Rows("exchange_rate where currency = 'SEK'"));
        Assert.Equal(
            "AED=2.720000,EUR=11.000000,GBP=12.800000,NOK=0.950000,USD=10.000000",
            Scalar<string>(
                "select string_agg(currency || '=' || rate_to_sek::text, ',' order by currency) " +
                "from exchange_rate where month = date '2026-11-01'"));

        Scalar<int>("with u as (update exchange_rate set rate_to_sek = 9.5 where currency = 'USD' and month = date '2026-11-01' returning 1) select count(*)::int from u");
        Seed();

        Assert.Equal(9.5m, Scalar<decimal>("select rate_to_sek from exchange_rate where currency = 'USD' and month = date '2026-11-01'"));
        Assert.Equal(5 * 132, Rows("exchange_rate"));
    }
}
