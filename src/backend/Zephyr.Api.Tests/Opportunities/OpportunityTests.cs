using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zephyr.Api.Data;
using Zephyr.Api.Permissions;
using Zephyr.Api.Tests.Permissions;
using Zephyr.Api.Tests.Support;

namespace Zephyr.Api.Tests.Opportunities;

/// <summary>The real application with the test sign-in of the permission tests, started once for the test class.</summary>
public sealed class OpportunityWorld : IAsyncLifetime
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private WebApplicationFactory<Program>? factory;
    private HttpClient? client;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        client?.Dispose();
        factory?.Dispose();
        gate.Dispose();
        return Task.CompletedTask;
    }

    public async Task<HttpClient> ClientAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (client is null)
            {
                factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                    builder.ConfigureTestServices(services =>
                        services.AddAuthentication(options => options.DefaultScheme = "Test")
                            .AddScheme<AuthenticationSchemeOptions, PermissionAuthHandler>("Test", null)));
                client = factory.CreateClient();
            }

            return client;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The engine keeps the matrix in memory: read it again after the test seeded it.</summary>
    public Task ReloadPermissionsAsync() => factory!.Services.GetRequiredService<IPermissionService>().ReloadAsync();
}

/// <summary>US-156: opportunity model, metadata-driven stages, CRUD with ETag, filters and scope. One test per acceptance criterion.</summary>
[Collection("db")]
public class OpportunityTests(DbFixture fixture, OpportunityWorld world) : IClassFixture<OpportunityWorld>, IAsyncLifetime
{
    private const string Url = "/api/v1/opportunities";

    private sealed record Caller(Guid Id, string Role);

    private HttpClient client = null!;
    private Guid accountId;
    private Caller sales = null!;
    private Caller salesIt = null!;
    private Caller finance = null!;
    private Caller admin = null!;
    private Caller consultant = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        // Reference rows first: a user's home country is a foreign key to country.
        await Sql(
            """
            insert into stage (key, label, sort_order, is_terminal, outcome) values
              ('discover', 'Discover', 1, false, null), ('qualify', 'Qualify', 2, false, null), ('won', 'Won', 6, true, 'won');
            insert into country (key, label, sort_order, iso2) values ('SE', 'Sweden', 1, 'SE'), ('IT', 'Italy', 2, 'IT');
            insert into currency (key, label, sort_order, iso3, decimals) values ('SEK', 'Swedish krona', 1, 'SEK', 2), ('EUR', 'Euro', 2, 'EUR', 2);
            insert into lead_source (key, label, sort_order) values ('web', 'Website', 1);
            """);
        accountId = Guid.NewGuid();
        await using (var db = fixture.CreateContext())
        {
            await PermissionSeeder.SeedAsync(db);
            db.Accounts.Add(new Account { Id = accountId, Name = "Acme", CountryCode = "SE" });
            sales = AddUser(db, "Salesperson", "SE");
            salesIt = AddUser(db, "Salesperson", "IT");
            finance = AddUser(db, "Finance", null);
            admin = AddUser(db, "Admin", null);
            consultant = AddUser(db, "Consultant", null);
            await db.SaveChangesAsync();
        }

        client = await world.ClientAsync();
        await world.ReloadPermissionsAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers ----

    private static Caller AddUser(ZephyrDbContext db, string role, string? homeCountry)
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Name = $"{role} {homeCountry}",
            Email = $"{Guid.NewGuid():N}@example.test",
            Roles = [role],
            HomeCountry = homeCountry
        };
        db.AppUsers.Add(user);
        return new Caller(user.Id, role);
    }

    private async Task<string> Sql(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? "";
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, Caller? who, string? json = null, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, method == HttpMethod.Patch ? "application/merge-patch+json" : "application/json");
        }

        if (who is not null)
        {
            request.Headers.Add("X-Test-User", who.Id.ToString());
            request.Headers.Add("X-Test-Role", who.Role);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>A valid opportunity body for the sales user in Sweden; <paramref name="extra"/> adds or replaces fields.</summary>
    private string Deal(params (string Key, object? Value)[] extra)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = "Deal",
            ["accountId"] = accountId,
            ["stageKey"] = "discover",
            ["contractingCountryKey"] = "SE"
        };
        foreach (var (key, value) in extra)
        {
            body[key] = value;
        }

        return JsonSerializer.Serialize(body);
    }

    private async Task<(string Id, string ETag)> CreateAsync(params (string Key, object? Value)[] extra)
    {
        using var response = await Send(HttpMethod.Post, Url, sales, Deal(extra));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJson(response);
        return (body.GetProperty("id").GetString()!, response.Headers.ETag!.Tag);
    }

    /// <summary>Asserts a 422 problem+json and returns the failing fields, sorted.</summary>
    private static async Task<string[]> FieldsOfProblem(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await ReadJson(response);
        return body.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("field").GetString()!).Order().ToArray();
    }

    private async Task<string[]> ListIds(string query, Caller who)
    {
        using var response = await Send(HttpMethod.Get, $"{Url}?{query}", who);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJson(response);
        return body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).Order().ToArray();
    }

    private async Task<Dictionary<string, string>> StageLabels(Caller who)
    {
        using var response = await Send(HttpMethod.Get, "/api/v1/stages", who);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadJson(response)).EnumerateArray().ToDictionary(s => s.GetProperty("key").GetString()!, s => s.GetProperty("label").GetString()!);
    }

    private static string[] Sorted(params string[] ids) => ids.Order().ToArray();

    // ---- AC 1: required fields ----

    [DbFact]
    public async Task Ac1_Post_without_an_account_and_with_an_unknown_stage_is_422_with_one_error_per_field()
    {
        using var response = await Send(HttpMethod.Post, Url, sales, """{"name":"Deal","stageKey":"nope"}""");

        Assert.Equal(new[] { "accountId", "stageKey" }, await FieldsOfProblem(response));
        Assert.Equal("0", await Sql("select count(*) from opportunity"));
    }

    [DbFact]
    public async Task Ac1_Post_without_a_stage_key_or_a_name_is_422()
    {
        using var missing = await Send(HttpMethod.Post, Url, sales, JsonSerializer.Serialize(new { accountId }));
        using var empty = await Send(HttpMethod.Post, Url, sales, Deal(("stageKey", null)));
        using var unknownAccount = await Send(HttpMethod.Post, Url, sales, Deal(("accountId", Guid.NewGuid())));

        Assert.Equal(new[] { "name", "stageKey" }, await FieldsOfProblem(missing));
        Assert.Equal(new[] { "stageKey" }, await FieldsOfProblem(empty));
        Assert.Equal(new[] { "accountId" }, await FieldsOfProblem(unknownAccount));
    }

    [DbFact]
    public async Task Ac1_Patch_with_a_null_or_unknown_account_is_422_and_changes_nothing()
    {
        var (id, etag) = await CreateAsync();

        using var cleared = await Send(HttpMethod.Patch, $"{Url}/{id}", sales, """{"accountId":null}""", etag);
        using var unknown = await Send(HttpMethod.Patch, $"{Url}/{id}", sales, JsonSerializer.Serialize(new { accountId = Guid.NewGuid() }), etag);

        Assert.Equal(new[] { "accountId" }, await FieldsOfProblem(cleared));
        Assert.Equal(new[] { "accountId" }, await FieldsOfProblem(unknown));
        Assert.Equal(accountId.ToString(), await Sql($"select account_id from opportunity where id = '{id}'"));
    }

    // ---- AC 2: stage by key ----

    [DbFact]
    public async Task Ac2_Renaming_a_stage_label_changes_no_opportunity_row_and_the_stage_list_shows_the_new_label()
    {
        var (id, etag) = await CreateAsync(("stageKey", "qualify"));
        var before = await Sql($"select to_jsonb(o)::text from opportunity o where id = '{id}'");

        await Sql("update stage set label = 'Qualification' where key = 'qualify'");

        Assert.Equal(before, await Sql($"select to_jsonb(o)::text from opportunity o where id = '{id}'"));
        var labels = await StageLabels(consultant);
        Assert.Equal("Qualification", labels["qualify"]);
        Assert.Equal(new[] { "discover", "qualify", "won" }, labels.Keys.ToArray());
        using var read = await Send(HttpMethod.Get, $"{Url}/{id}", sales);
        Assert.Equal("qualify", (await ReadJson(read)).GetProperty("stageKey").GetString());
        Assert.Equal(etag, read.Headers.ETag!.Tag);
    }

    [DbFact]
    public async Task Ac2_Stage_outcome_is_only_allowed_on_a_terminal_stage()
    {
        await Assert.ThrowsAsync<PostgresException>(() => Sql("update stage set outcome = 'won' where key = 'discover'"));
        await Assert.ThrowsAsync<PostgresException>(() => Sql("update stage set outcome = 'maybe' where key = 'won'"));
    }

    // ---- AC 3: CRUD with concurrency ----

    [DbFact]
    public async Task Ac3_Create_read_patch_and_delete_work_with_etags_and_a_deleted_row_is_hidden()
    {
        using var created = await Send(HttpMethod.Post, Url, sales, Deal(
            ("amount", new { amount = "1000.50", currency = "EUR" }), ("hourlyRate", "1200.00"), ("estHours", 40), ("forecastCategory", "commit"),
            ("leadSourceKey", "web"), ("nextStep", "Send offer"), ("nextStepDate", "2026-12-01"), ("expectedCloseDate", "2027-01-31"),
            ("customPlatform", "Salesforce"), ("deliveryTeam", "Team A"), ("proposedPo", "Anna")));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await ReadJson(created);
        var id = body.GetProperty("id").GetString()!;
        var etag = created.Headers.ETag!.Tag;
        Assert.Equal($"{Url}/{id}", created.Headers.Location!.OriginalString);
        Assert.Equal("discover", body.GetProperty("stageKey").GetString());
        Assert.Equal(sales.Id.ToString(), body.GetProperty("ownerUserId").GetString());
        Assert.Equal("1000.50", body.GetProperty("amount").GetProperty("amount").GetString());
        Assert.Equal("EUR", body.GetProperty("amount").GetProperty("currency").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("amount").GetProperty("amountSEK").ValueKind);
        Assert.Equal("40.00", body.GetProperty("estHours").GetString());

        using var read = await Send(HttpMethod.Get, $"{Url}/{id}", sales);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(etag, read.Headers.ETag!.Tag);

        using var noIfMatch = await Send(HttpMethod.Patch, $"{Url}/{id}", sales, """{"name":"Renamed"}""");
        Assert.Equal(HttpStatusCode.PreconditionRequired, noIfMatch.StatusCode);

        using var patched = await Send(HttpMethod.Patch, $"{Url}/{id}", sales, """{"name":"Renamed","nextStep":null,"amount":null}""", etag);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var patchedBody = await ReadJson(patched);
        Assert.Equal("Renamed", patchedBody.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, patchedBody.GetProperty("nextStep").ValueKind);
        Assert.Equal(JsonValueKind.Null, patchedBody.GetProperty("amount").ValueKind);
        Assert.Equal("Salesforce", patchedBody.GetProperty("customPlatform").GetString());
        Assert.NotEqual(etag, patched.Headers.ETag!.Tag);

        using var stale = await Send(HttpMethod.Patch, $"{Url}/{id}", sales, """{"name":"Again"}""", etag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

        using var bySales = await Send(HttpMethod.Delete, $"{Url}/{id}", sales);
        Assert.Equal(HttpStatusCode.Forbidden, bySales.StatusCode);
        using var byAdmin = await Send(HttpMethod.Delete, $"{Url}/{id}", admin);
        Assert.Equal(HttpStatusCode.NoContent, byAdmin.StatusCode);
        using var gone = await Send(HttpMethod.Get, $"{Url}/{id}", finance);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.DoesNotContain(id, await ListIds("", finance));
        using var deletedAgain = await Send(HttpMethod.Delete, $"{Url}/{id}", admin);
        Assert.Equal(HttpStatusCode.NotFound, deletedAgain.StatusCode);
        Assert.Equal("1", await Sql($"select count(*) from opportunity where id = '{id}' and deleted_at is not null"));
    }

    [DbFact]
    public async Task Ac3_Invalid_values_are_422_with_one_error_per_field()
    {
        var (id, etag) = await CreateAsync();

        using var response = await Send(HttpMethod.Patch, $"{Url}/{id}", sales, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hourlyRate"] = "-1",
            ["estHours"] = "abc",
            ["nextStepDate"] = "2026-13-40",
            ["forecastCategory"] = "maybe",
            ["contractingCountryKey"] = "XX",
            ["leadSourceKey"] = "unknown",
            ["ownerUserId"] = Guid.NewGuid(),
            ["amount"] = new { amount = "1.234", currency = "zz" },
            ["name"] = ""
        }), etag);
        using var unknownCurrency = await Send(HttpMethod.Patch, $"{Url}/{id}", sales, """{"amount":{"amount":"1.00","currency":"ZZZ"}}""", etag);

        Assert.Equal(
            new[]
            {
                "amount.amount", "amount.currency", "contractingCountryKey", "estHours", "forecastCategory", "hourlyRate", "leadSourceKey",
                "name", "nextStepDate", "ownerUserId"
            },
            await FieldsOfProblem(response));
        Assert.Equal(new[] { "amount.currency" }, await FieldsOfProblem(unknownCurrency));
    }

    // ---- AC 4: filters and scope ----

    [DbFact]
    public async Task Ac4_List_filters_by_stage_owner_close_date_and_open_status()
    {
        var (a, _) = await CreateAsync(("expectedCloseDate", "2026-12-01"));
        var (b, _) = await CreateAsync(("stageKey", "qualify"), ("expectedCloseDate", "2027-01-15"));
        var (c, _) = await CreateAsync(("stageKey", "qualify"), ("expectedCloseDate", "2027-03-01"));
        await Sql($"update opportunity set closed_at = now() where id = '{c}'");

        Assert.Equal(Sorted(a, b, c), await ListIds("", finance));
        Assert.Equal(Sorted(b, c), await ListIds("filter[stageKey][eq]=qualify", finance));
        Assert.Equal(Sorted(a, b, c), await ListIds($"filter[ownerUserId][eq]={sales.Id}", finance));
        Assert.Empty(await ListIds($"filter[ownerUserId][eq]={Guid.NewGuid()}", finance));
        Assert.Equal(Sorted(b), await ListIds("filter[expectedCloseDate][gte]=2027-01-01&filter[expectedCloseDate][lte]=2027-02-01", finance));
        Assert.Equal(Sorted(a, b), await ListIds("filter[closedAt][eq]=null", finance));
        Assert.Equal(Sorted(c), await ListIds("filter[closedAt][ne]=null", finance));
        using var unknown = await Send(HttpMethod.Get, $"{Url}?filter[bogus][eq]=x", finance);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [DbFact]
    public async Task Ac4_A_user_only_sees_opportunities_in_their_scope_and_without_a_permission_gets_403()
    {
        var (id, etag) = await CreateAsync();

        // Salesperson reads by country: the Italian salesperson sees nothing of a Swedish deal, and cannot change it.
        Assert.Empty(await ListIds("", salesIt));
        using var hidden = await Send(HttpMethod.Get, $"{Url}/{id}", salesIt);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var hiddenPatch = await Send(HttpMethod.Patch, $"{Url}/{id}", salesIt, """{"name":"Mine now"}""", etag);
        Assert.Equal(HttpStatusCode.NotFound, hiddenPatch.StatusCode);
        Assert.Equal(new[] { id }, await ListIds("", sales));
        Assert.Equal(new[] { id }, await ListIds("", finance));

        // Finance reads everything but changes nothing; the consultant has no opportunity permission at all.
        using var financePatch = await Send(HttpMethod.Patch, $"{Url}/{id}", finance, """{"name":"Finance"}""", etag);
        Assert.Equal(HttpStatusCode.Forbidden, financePatch.StatusCode);
        using var financePost = await Send(HttpMethod.Post, Url, finance, Deal());
        Assert.Equal(HttpStatusCode.Forbidden, financePost.StatusCode);
        using var consultantList = await Send(HttpMethod.Get, Url, consultant);
        Assert.Equal(HttpStatusCode.Forbidden, consultantList.StatusCode);
        using var consultantRead = await Send(HttpMethod.Get, $"{Url}/{id}", consultant);
        Assert.Equal(HttpStatusCode.Forbidden, consultantRead.StatusCode);
        using var anonymous = await Send(HttpMethod.Get, Url, null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // Create is "own": the owner cannot be someone else.
        using var foreignOwner = await Send(HttpMethod.Post, Url, sales, Deal(("ownerUserId", salesIt.Id)));
        Assert.Equal(HttpStatusCode.Forbidden, foreignOwner.StatusCode);
        Assert.Equal("1", await Sql("select count(*) from opportunity"));
    }

    // ---- AC 5: PATCH cannot move the stage ----

    [DbFact]
    public async Task Ac5_Patch_with_a_stage_or_close_fields_is_422_pointing_to_the_dedicated_endpoints()
    {
        var (id, etag) = await CreateAsync();

        using var stage = await Send(HttpMethod.Patch, $"{Url}/{id}", sales, """{"stageKey":"qualify"}""", etag);
        var stageBody = await ReadJson(stage);
        Assert.Equal(new[] { "stageKey" }, await FieldsOfProblem(stage));
        Assert.Contains(":change-stage", stageBody.GetProperty("errors")[0].GetProperty("message").GetString());

        using var closed = await Send(
            HttpMethod.Patch, $"{Url}/{id}", sales,
            """{"closedAt":"2026-11-01T00:00:00Z","closedOutcome":"won","closedReasonKey":"won_best_fit","competitor":"Rival"}""", etag);
        var closedBody = await ReadJson(closed);
        Assert.Equal(new[] { "closedAt", "closedOutcome", "closedReasonKey", "competitor" }, await FieldsOfProblem(closed));
        Assert.All(closedBody.GetProperty("errors").EnumerateArray(), e => Assert.Contains(":close", e.GetProperty("message").GetString()));

        Assert.Equal("discover", await Sql($"select stage_key from opportunity where id = '{id}'"));
        Assert.Equal("", await Sql($"select closed_outcome from opportunity where id = '{id}'"));
    }
}
