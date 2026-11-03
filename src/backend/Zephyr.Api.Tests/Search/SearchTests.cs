using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zephyr.Api.Common;
using Zephyr.Api.Data;
using Zephyr.Api.Permissions;
using Zephyr.Api.Search;
using Zephyr.Api.Tests.Common;
using Zephyr.Api.Tests.Support;

namespace Zephyr.Api.Tests.Search;

[Collection("db")]
public class SearchTests(DbFixture fixture) : IAsyncLifetime
{
    private static readonly string[] All = ["country", "hidden", "nowhere", "owned", "team"];
    private static readonly string[] Owned = ["owned"];
    private static readonly string[] OwnedAndTeam = ["owned", "team"];
    private static readonly string[] CountryOnly = ["country"];
    private static readonly string[] Nothing = [];

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        // The search scope comes from the permission matrix, which the reset empties.
        await using var db = fixture.CreateContext();
        await PermissionSeeder.SeedAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- test host: the real search route behind the test sign-in ----

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddApiConventions();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", null);
        var app = builder.Build();
        app.UseApiConventions();
        app.UseAuthentication();
        app.MapSearch();
        await app.StartAsync();
        return app;
    }

    // ---- helpers ----

    private async Task<Guid> SeedUserAsync(string? role, string? home = "SE", bool active = true, Guid? manager = null)
    {
        await using var db = fixture.CreateContext();
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Name = role ?? "No role",
            Email = $"{Guid.NewGuid():N}@example.test",
            Roles = role is null ? [] : [role],
            HomeCountry = home,
            Active = active,
            ManagerId = manager
        };
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> AddAccountAsync(string name, string? country = "SE")
    {
        await using var db = fixture.CreateContext();
        var account = new Account { Name = name, CountryCode = country };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private async Task<long> DocumentCountAsync(Guid id) =>
        (long)(await ScalarAsync($"select count(*) from search_document where entity_id = '{id}'"))!;

    private static string Join(IEnumerable<string> titles) => string.Join(" | ", titles);

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string query, Guid? user)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{SearchEndpoints.Url}?{query}");
        if (user is not null)
        {
            request.Headers.Add("X-Test-User", user.ToString());
        }

        return await client.SendAsync(request);
    }

    private static async Task<(List<string> Titles, bool Fallback)> SearchAsync(HttpClient client, Guid user, string q, string extra = "")
    {
        var response = await GetAsync(client, $"q={Uri.EscapeDataString(q)}{extra}", user);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var titles = document.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!).ToList();
        return (titles, document.RootElement.GetProperty("fallback").GetBoolean());
    }

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string? field = null)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)status, problem.RootElement.GetProperty("status").GetInt32());
        if (field is not null)
        {
            Assert.Contains(problem.RootElement.GetProperty("errors").EnumerateArray(), e => e.GetProperty("field").GetString() == field);
        }
    }

    // ---- AC 1: accent-insensitive ----

    [DbFact]
    public async Task Ac1_Searching_malmo_finds_Malmo_with_the_umlaut_in_the_top_5()
    {
        var user = await SeedUserAsync("Management");
        foreach (var name in new[] { "Malmö Stad", "Lund AB", "Umeå AB", "Örebro AB" })
        {
            await AddAccountAsync(name);
        }

        await using var app = await StartAsync();
        var client = app.GetTestClient();

        Assert.Contains("Malmö Stad", (await SearchAsync(client, user, "malmo")).Titles.Take(5));
        Assert.Contains("Malmö Stad", (await SearchAsync(client, user, "MALMÖ")).Titles.Take(5));
        Assert.Contains("Örebro AB", (await SearchAsync(client, user, "orebro")).Titles.Take(5));
    }

    // ---- AC 2: prefix and typo ----

    [DbFact]
    public async Task Ac2_A_three_letter_prefix_and_a_name_with_one_typo_find_the_record_in_the_top_5()
    {
        var user = await SeedUserAsync("Management");
        foreach (var name in new[] { "Ericsson AB", "Volvo Group", "Saab Defence", "Scania CV" })
        {
            await AddAccountAsync(name);
        }

        await using var app = await StartAsync();
        var client = app.GetTestClient();

        Assert.Contains("Ericsson AB", (await SearchAsync(client, user, "eri")).Titles.Take(5));
        foreach (var (typo, expected) in new[] { ("Ericsonn", "Ericsson AB"), ("Voolvo", "Volvo Group"), ("Scanya", "Scania CV") })
        {
            var result = await SearchAsync(client, user, typo);
            Assert.Contains(expected, result.Titles.Take(5));
            Assert.True(result.Fallback); // no full-text hit: the trigram step found it
        }
    }

    // ---- AC 3: fallback ----

    [DbFact]
    public async Task Ac3_Fewer_than_5_full_text_hits_run_the_trigram_step_and_5_or_more_never_do()
    {
        var user = await SeedUserAsync("Management");
        foreach (var name in new[] { "Nordic Alpha", "Nordic Bravo", "Nordic Charlie", "Nordic Delta", "Nordic Echo", "Nordic Foxtrot", "Nordik Hotel" })
        {
            await AddAccountAsync(name);
        }

        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var enough = await SearchAsync(client, user, "nordic");
        Assert.False(enough.Fallback);
        Assert.Equal(6, enough.Titles.Count);
        Assert.DoesNotContain("Nordik Hotel", enough.Titles); // only the trigram step could add it

        var few = await SearchAsync(client, user, "nordik");
        Assert.True(few.Fallback);
        Assert.Equal("Nordik Hotel", few.Titles[0]); // the full-text hit first, the trigram matches after it
        Assert.Contains("Nordic Alpha", few.Titles);
        Assert.Equal(few.Titles.Count, few.Titles.Distinct().Count());

        var small = await SearchAsync(client, user, "nordic", "&limit=3");
        Assert.False(small.Fallback);
        Assert.Equal(3, small.Titles.Count);
    }

    [DbFact]
    public async Task Ac3_Four_hits_run_the_trigram_step_and_exactly_5_do_not()
    {
        var user = await SeedUserAsync("Management");
        foreach (var name in new[] { "Polar One", "Polar Two", "Polar Three", "Polar Four", "Pular Hotel" })
        {
            await AddAccountAsync(name);
        }

        await using var app = await StartAsync();
        var client = app.GetTestClient();

        Assert.True((await SearchAsync(client, user, "polar")).Fallback); // 4 full-text hits
        await AddAccountAsync("Polar Five");
        var five = await SearchAsync(client, user, "polar"); // 5 full-text hits
        Assert.False(five.Fallback);
        Assert.DoesNotContain("Pular Hotel", five.Titles);
    }

    // ---- AC 4: permissions ----

    [DbFact]
    public async Task Ac4_Every_entity_type_is_filtered_by_the_scope_the_matrix_gives_the_caller_in_both_steps()
    {
        // Expected tags per role and type follow permission-matrix.csv (read): org, own, team, country or none.
        var callers = new (string Role, string? Home, string[][] ByType)[]
        {
            // account, contact, lead, opportunity, project
            ("Salesperson", "SE", [All, All, CountryOnly, CountryOnly, Owned]),
            ("Salesperson", null, [All, All, Nothing, Nothing, Owned]), // no home country: a country scope matches nothing
            ("BDR", "SE", [All, All, OwnedAndTeam, Owned, Nothing]),
            ("PM", "SE", [All, All, Nothing, Owned, Owned]),
            ("Consultant", "SE", [All, All, Nothing, Nothing, Owned]),
            ("Finance", "SE", [All, All, Nothing, All, All]),
            ("Management", "SE", [All, All, All, All, All]),
            ("Admin", "SE", [All, All, All, All, All])
        };
        var manager = await SeedUserAsync("Management");
        var teammate = await SeedUserAsync("PM", manager: manager);
        var other = Guid.NewGuid();
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        foreach (var (role, home, byType) in callers)
        {
            var caller = await SeedUserAsync(role, home, manager: manager);
            await ScalarAsync("delete from search_document");
            foreach (var type in SearchEndpoints.Types)
            {
                await ScalarAsync($"select search_upsert('{type}', '{Guid.NewGuid()}', 'Leak Probe {type} owned', null, '', '{caller}', null, 'NO', null, now())");
                await ScalarAsync($"select search_upsert('{type}', '{Guid.NewGuid()}', 'Leak Probe {type} team', null, '', '{teammate}', null, 'NO', null, now())");
                await ScalarAsync($"select search_upsert('{type}', '{Guid.NewGuid()}', 'Leak Probe {type} country', null, '', '{other}', null, 'SE', null, now())");
                await ScalarAsync($"select search_upsert('{type}', '{Guid.NewGuid()}', 'Leak Probe {type} hidden', null, '', '{other}', null, 'NO', null, now())");
                await ScalarAsync($"select search_upsert('{type}', '{Guid.NewGuid()}', 'Leak Probe {type} nowhere', null, '', null, null, null, null, now())");
            }

            for (var i = 0; i < SearchEndpoints.Types.Length; i++)
            {
                var type = SearchEndpoints.Types[i];
                var expected = Join(byType[i].Select(tag => $"Leak Probe {type} {tag}").Order());
                foreach (var query in new[] { "leak probe", "leek probe" }) // the second one only matches in the trigram step
                {
                    var found = Join((await SearchAsync(client, caller, query, $"&types={type}")).Titles.Order());
                    Assert.True(expected == found, $"{role} (home {home ?? "none"}) searching {type} for '{query}': expected [{expected}] but found [{found}]");
                }
            }
        }
    }

    [DbFact]
    public async Task Ac4_Accounts_through_the_real_trigger_carry_the_creator_as_owner_and_an_account_without_country_is_found_by_org_scopes()
    {
        var creator = Guid.NewGuid();
        await using (var db = fixture.CreateContext(creator))
        {
            db.Accounts.Add(new Account { Name = "Owned By Creator", CountryCode = "SE" });
            await db.SaveChangesAsync();
        }

        await AddAccountAsync("Leak Probe SE", "SE");
        await AddAccountAsync("Leak Probe NO", "NO");
        await AddAccountAsync("Leak Probe None", null);
        var seller = await SeedUserAsync("Salesperson", "SE");
        var management = await SeedUserAsync("Management", null);
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        Assert.Equal(creator, await ScalarAsync("select owner_id from search_document where title = 'Owned By Creator'"));
        foreach (var query in new[] { "leak probe", "leek probe" })
        {
            Assert.Equal(3, (await SearchAsync(client, seller, query)).Titles.Count); // Salesperson reads accounts org-wide
            Assert.Equal(3, (await SearchAsync(client, management, query)).Titles.Count);
        }
    }

    [DbFact]
    public async Task Ac4_Callers_without_a_role_that_may_search_are_refused_and_a_type_filter_never_reveals_hidden_types()
    {
        await AddAccountAsync("Visible Corp");
        var callers = new List<Guid?>
        {
            await SeedUserAsync(null),
            await SeedUserAsync("Unknown role"),
            await SeedUserAsync("Integration"), // an API client, not a matrix role
            await SeedUserAsync("Management", active: false),
            Guid.NewGuid(),
            null // no session: the real app answers 401 before the route (MatrixCoverageTests), this host has no default-deny
        };
        var consultant = await SeedUserAsync("Consultant");
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        foreach (var caller in callers)
        {
            await AssertProblem(await GetAsync(client, "q=visible", caller), HttpStatusCode.Forbidden);
        }

        // A Consultant reads no leads: asking for leads only is an empty list, not a 403.
        var none = await SearchAsync(client, consultant, "visible", "&types=lead");
        Assert.Empty(none.Titles);
        Assert.Contains("Visible Corp", (await SearchAsync(client, consultant, "visible")).Titles);
    }

    // ---- AC 5: speed (the 100-query benchmark is tools/search-benchmark/bench.mjs) ----

    [DbFact]
    public async Task Ac5_Both_search_steps_can_use_their_GIN_index()
    {
        await AddAccountAsync("Indexed Corp");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using (var off = new NpgsqlCommand("set enable_seqscan = off", connection))
        {
            await off.ExecuteNonQueryAsync();
        }

        async Task<string> Plan(string where)
        {
            await using var command = new NpgsqlCommand($"explain select 1 from search_document where {where}", connection);
            await using var reader = await command.ExecuteReaderAsync();
            var lines = new List<string>();
            while (await reader.ReadAsync())
            {
                lines.Add(reader.GetString(0));
            }

            return string.Join('\n', lines);
        }

        Assert.Contains("ix_search_document_tsv", await Plan("tsv @@ to_tsquery('simple', 'ind:*')"));
        Assert.Contains("ix_search_document_text_trgm", await Plan("'indxed' <% text"));
    }

    // ---- AC 6: freshness ----

    [DbFact]
    public async Task Ac6_A_record_is_searchable_in_the_transaction_that_creates_it()
    {
        var id = Guid.NewGuid();
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var insert = new NpgsqlCommand($"insert into account (id, name, country_code) values ('{id}', 'Fresh Account', 'SE')", connection, transaction))
            {
                await insert.ExecuteNonQueryAsync();
            }

            await using var count = new NpgsqlCommand($"select count(*) from search_document where entity_id = '{id}'", connection, transaction);
            Assert.Equal(1L, await count.ExecuteScalarAsync());
            await transaction.RollbackAsync();
        }

        Assert.Equal(0, await DocumentCountAsync(id));
    }

    [DbFact]
    public async Task Ac6_A_created_and_a_renamed_account_are_found_at_once()
    {
        var user = await SeedUserAsync("Management");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var account = await AddAccountAsync("Before Rename");
        Assert.Contains("Before Rename", (await SearchAsync(client, user, "before")).Titles);

        await using (var db = fixture.CreateContext())
        {
            (await db.Accounts.SingleAsync(a => a.Id == account)).Name = "After Rename";
            await db.SaveChangesAsync();
        }

        Assert.Contains("After Rename", (await SearchAsync(client, user, "after")).Titles);
        Assert.DoesNotContain("Before Rename", (await SearchAsync(client, user, "before")).Titles);
    }

    // ---- AC 7: soft delete and anonymise ----

    [DbFact]
    public async Task Ac7_Soft_deleting_an_account_removes_it_and_restoring_it_adds_it_again()
    {
        var id = await AddAccountAsync("Vanishing Corp");
        Assert.Equal(1, await DocumentCountAsync(id));

        await ScalarAsync($"update account set deleted_at = now() where id = '{id}'");
        Assert.Equal(0, await DocumentCountAsync(id));

        await ScalarAsync($"update account set deleted_at = null where id = '{id}'");
        Assert.Equal(1, await DocumentCountAsync(id));

        await ScalarAsync($"delete from account where id = '{id}'");
        Assert.Equal(0, await DocumentCountAsync(id));
    }

    [DbFact]
    public async Task Ac7_The_trigger_helper_also_hides_anonymised_records_and_never_restores_them()
    {
        // Contacts and leads arrive with later stories and install their trigger like this probe table does.
        await ScalarAsync(
            "create table search_probe (id uuid primary key default gen_random_uuid(), name text not null, " +
            "updated_at timestamptz not null default now(), deleted_at timestamptz null, anonymised_at timestamptz null)");
        try
        {
            await ScalarAsync(
                "select search_install_trigger('search_probe', 'contact', 'new.name', 'null', " +
                "p_hidden => 'new.deleted_at is not null or new.anonymised_at is not null')");
            var id = (Guid)(await ScalarAsync("insert into search_probe (name) values ('Jane Probe') returning id"))!;
            Assert.Equal(1, await DocumentCountAsync(id));

            await ScalarAsync($"update search_probe set deleted_at = now() where id = '{id}'");
            Assert.Equal(0, await DocumentCountAsync(id));
            await ScalarAsync($"update search_probe set deleted_at = null where id = '{id}'");
            Assert.Equal(1, await DocumentCountAsync(id));

            await ScalarAsync($"update search_probe set anonymised_at = now() where id = '{id}'");
            Assert.Equal(0, await DocumentCountAsync(id));
            await ScalarAsync($"update search_probe set deleted_at = now() where id = '{id}'");
            await ScalarAsync($"update search_probe set deleted_at = null where id = '{id}'");
            Assert.Equal(0, await DocumentCountAsync(id)); // anonymised: stays out of search
        }
        finally
        {
            await ScalarAsync("drop table if exists search_probe");
            await ScalarAsync("drop function if exists search_sync_search_probe()");
        }
    }

    // ---- AC 8: input ----

    [DbFact]
    public async Task Ac8_A_query_shorter_than_2_or_longer_than_100_characters_is_a_400_problem()
    {
        var user = await SeedUserAsync("Management");
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        await AssertProblem(await GetAsync(client, "q=a", user), HttpStatusCode.BadRequest, "q");
        await AssertProblem(await GetAsync(client, "q=%20a%20", user), HttpStatusCode.BadRequest, "q");
        await AssertProblem(await GetAsync(client, "", user), HttpStatusCode.BadRequest, "q");
        await AssertProblem(await GetAsync(client, $"q={new string('a', 101)}", user), HttpStatusCode.BadRequest, "q");
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "q=ab", user)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, $"q={new string('a', 100)}", user)).StatusCode);
    }

    [DbFact]
    public async Task Ac8_A_bad_limit_types_or_parameter_is_a_400_and_a_query_without_words_finds_nothing()
    {
        var user = await SeedUserAsync("Management");
        await AddAccountAsync("Anything AB");
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        await AssertProblem(await GetAsync(client, "q=ab&limit=51", user), HttpStatusCode.BadRequest, "limit");
        await AssertProblem(await GetAsync(client, "q=ab&limit=0", user), HttpStatusCode.BadRequest, "limit");
        await AssertProblem(await GetAsync(client, "q=ab&limit=x", user), HttpStatusCode.BadRequest, "limit");
        await AssertProblem(await GetAsync(client, "q=ab&types=account,person", user), HttpStatusCode.BadRequest, "types");
        await AssertProblem(await GetAsync(client, "q=ab&page=2", user), HttpStatusCode.BadRequest, "page");
        await AssertProblem(await GetAsync(client, "q=ab&q=cd", user), HttpStatusCode.BadRequest, "q");
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "q=ab&limit=50&types=account,contact", user)).StatusCode);

        var none = await SearchAsync(client, user, "!?");
        Assert.Empty(none.Titles);
        Assert.False(none.Fallback);
    }

    // ---- ranking (PO answer #76) and safety ----

    [DbFact]
    public async Task Ranking_puts_the_exact_name_first_then_prefix_then_other_matches_and_a_recent_record_ahead_of_an_old_one()
    {
        var user = await SeedUserAsync("Management");
        foreach (var name in new[] { "Global Acme Partners", "Acme Industries", "Acme", "Beta Zorbo", "Gamma Zorbo" })
        {
            await AddAccountAsync(name);
        }

        await ScalarAsync("update account set updated_at = now() - interval '2 years' where name = 'Beta Zorbo'");
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        Assert.Equal("Acme | Acme Industries | Global Acme Partners", Join((await SearchAsync(client, user, "acme")).Titles));
        Assert.Equal("Gamma Zorbo | Beta Zorbo", Join((await SearchAsync(client, user, "zorbo")).Titles));
    }

    [DbFact]
    public async Task Hostile_text_in_a_query_is_treated_as_text()
    {
        var user = await SeedUserAsync("Management");
        await AddAccountAsync("O'Brien & Sons");
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        Assert.Contains("O'Brien & Sons", (await SearchAsync(client, user, "o'brien & sons")).Titles);
        Assert.Empty((await SearchAsync(client, user, "'; drop table search_document; --")).Titles);
        Assert.Equal(1L, await ScalarAsync("select count(*) from search_document"));
    }

    [DbFact]
    public async Task Resetting_the_test_database_still_works_with_the_search_triggers()
    {
        var id = await AddAccountAsync("Reset Me");
        Assert.Equal(1, await DocumentCountAsync(id));

        await fixture.ResetAsync();

        Assert.Equal(0L, await ScalarAsync("select count(*) from account"));
        Assert.Equal(0L, await ScalarAsync("select count(*) from search_document"));
    }

    [DbFact]
    public void The_real_app_exposes_the_search_route()
    {
        using var factory = new WebApplicationFactory<Program>();

        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        Assert.Contains(SearchEndpoints.Url, routes);
    }
}
