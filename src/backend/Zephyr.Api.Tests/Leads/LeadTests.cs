using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zephyr.Api.Common;
using Zephyr.Api.Data;
using Zephyr.Api.Leads;
using Zephyr.Api.Permissions;
using Zephyr.Api.Tests.Common;
using Zephyr.Api.Tests.Support;

namespace Zephyr.Api.Tests.Leads;

/// <summary>
/// US-153: the lead API behind the test sign-in, over the real permission matrix. The lead source picklist has no seeded values
/// (they are not confirmed), so every test inserts its own: <c>test_source_a</c> is active, <c>test_source_b</c> is not.
/// </summary>
[Collection("db")]
public class LeadTests(DbFixture fixture) : IAsyncLifetime
{
    private const string Url = LeadEndpoints.Url;
    private const string Body = """{"lastName":"Berg","companyName":"Berg Demo AB","leadSource":"test_source_a"}""";

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await using var db = fixture.CreateContext();
        await PermissionSeeder.SeedAsync(db);
        await db.Database.ExecuteSqlRawAsync(
            """
            insert into lead_source (key, label, sort_order, active)
                values ('test_source_a', 'Test source A', 1, true), ('test_source_b', 'Test source B', 2, false);
            insert into country (key, label, sort_order, active, iso2) values ('SE', 'Sweden', 1, true, 'SE');
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- test host: the real lead routes behind the test sign-in ----

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddApiConventions();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", null);
        var app = builder.Build();
        app.UseApiConventions();
        app.UseAuthentication();
        app.MapLeadEndpoints();
        await app.StartAsync();
        return app;
    }

    // ---- helpers ----

    private async Task<Guid> SeedUserAsync(string role, string? home = "SE")
    {
        await using var db = fixture.CreateContext();
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Name = role,
            Email = $"{Guid.NewGuid():N}@example.test",
            Roles = [role],
            HomeCountry = home
        };
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static HttpRequestMessage Send(HttpMethod method, string url, Guid? user, string? json = null, string? etag = null, string? ifNoneMatch = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, method == HttpMethod.Patch ? "application/merge-patch+json" : "application/json");
        }

        if (user is not null)
        {
            request.Headers.Add("X-Test-User", user.ToString());
        }

        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        return request;
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> CreateAsync(HttpClient client, Guid user, string json = Body)
    {
        var response = await client.SendAsync(Send(HttpMethod.Post, Url, user, json));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadJson(response);
    }

    private static async Task<string> ETagOfAsync(HttpClient client, Guid id, Guid user) =>
        (await client.SendAsync(Send(HttpMethod.Get, $"{Url}/{id}", user))).Headers.ETag!.Tag;

    private static async Task<JsonElement> AssertProblem(HttpResponseMessage response, HttpStatusCode status, params string[] fields)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadJson(response);
        var failing = new List<string?>();
        if (problem.TryGetProperty("errors", out var errors))
        {
            failing.AddRange(errors.EnumerateArray().Select(e => e.GetProperty("field").GetString()));
        }

        Assert.All(fields, field => Assert.Contains(field, failing));
        return problem;
    }

    private async Task<int> LeadCountAsync()
    {
        await using var db = fixture.CreateContext();
        return await db.Leads.IgnoreQueryFilters().CountAsync();
    }

    private async Task<int> SearchDocumentCountAsync()
    {
        await using var db = fixture.CreateContext();
        return await db.Database.SqlQuery<int>($"select count(*)::int as \"Value\" from search_document where entity_type = 'lead'").SingleAsync();
    }

    // ---- AC 1: lead source required ----

    [DbFact]
    public async Task Ac1_A_lead_without_a_lead_source_is_422_on_leadSource_and_nothing_is_saved()
    {
        var bdr = await SeedUserAsync("BDR");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var bodies = new[]
        {
            """{"lastName":"Berg","companyName":"Berg Demo AB"}""",
            """{"lastName":"Berg","companyName":"Berg Demo AB","leadSource":null}""",
            """{"lastName":"Berg","companyName":"Berg Demo AB","leadSource":"  "}"""
        };

        foreach (var json in bodies)
        {
            var problem = await AssertProblem(await client.SendAsync(Send(HttpMethod.Post, Url, bdr, json)), HttpStatusCode.UnprocessableEntity, "leadSource");

            Assert.Equal("VALIDATION", problem.GetProperty("code").GetString());
            var error = problem.GetProperty("errors").EnumerateArray().Single(e => e.GetProperty("field").GetString() == "leadSource");
            Assert.Equal("REQUIRED", error.GetProperty("code").GetString());
        }

        Assert.Equal(0, await LeadCountAsync());
    }

    [DbFact]
    public async Task Ac1_An_unknown_or_inactive_lead_source_is_422_on_leadSource()
    {
        var bdr = await SeedUserAsync("BDR");
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        foreach (var source in new[] { "no_such_source", "test_source_b" })
        {
            var json = $$"""{"lastName":"Berg","companyName":"Berg Demo AB","leadSource":"{{source}}"}""";
            await AssertProblem(await client.SendAsync(Send(HttpMethod.Post, Url, bdr, json)), HttpStatusCode.UnprocessableEntity, "leadSource");
        }

        Assert.Equal(0, await LeadCountAsync());
    }

    [DbFact]
    public async Task Ac1_Last_name_and_company_are_required_and_every_invalid_field_is_reported_together()
    {
        var bdr = await SeedUserAsync("BDR");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        const string invalid =
            """
            {"lastName":"Berg","companyName":"Berg Demo AB","leadSource":"test_source_a","email":"not-an-email",
             "website":"example.com","countryCode":"ZZ","nextStepDate":"tomorrow","phone":5,"accountId":"00000000-0000-0000-0000-00000000dead"}
            """;

        await AssertProblem(await client.SendAsync(Send(HttpMethod.Post, Url, bdr, """{"leadSource":"test_source_a"}""")),
            HttpStatusCode.UnprocessableEntity, "lastName", "companyName");
        await AssertProblem(await client.SendAsync(Send(HttpMethod.Post, Url, bdr, invalid)), HttpStatusCode.UnprocessableEntity,
            "email", "website", "countryCode", "nextStepDate", "phone", "accountId");
        await AssertProblem(await client.SendAsync(Send(HttpMethod.Post, Url, bdr, "[1]")), HttpStatusCode.BadRequest, "body");

        Assert.Equal(0, await LeadCountAsync());
    }

    [DbFact]
    public async Task Ac1_A_client_cannot_set_call_tries_last_call_outcome_status_or_owner()
    {
        var bdr = await SeedUserAsync("BDR");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var extras = new[]
        {
            ("callTries", "5"),
            ("lastCallOutcome", "\"no_answer\""),
            ("status", "\"converted\""),
            ("ownerUserId", $"\"{Guid.NewGuid()}\"")
        };

        foreach (var (field, value) in extras)
        {
            var json = Body[..^1] + $",\"{field}\":{value}" + "}";
            var response = await client.SendAsync(Send(HttpMethod.Post, Url, bdr, json));

            var problem = await AssertProblem(response, HttpStatusCode.UnprocessableEntity, field);
            var error = problem.GetProperty("errors").EnumerateArray().Single(e => e.GetProperty("field").GetString() == field);
            Assert.Equal("NOT_WRITABLE", error.GetProperty("code").GetString());
        }

        Assert.Equal(0, await LeadCountAsync());
    }

    // ---- AC 2: account nullable ----

    [DbFact]
    public async Task Ac2_A_lead_is_created_without_an_account_and_the_caller_owns_it()
    {
        var bdr = await SeedUserAsync("BDR");
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var response = await client.SendAsync(Send(HttpMethod.Post, Url, bdr, Body));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadJson(response);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal($"{Url}/{id}", response.Headers.Location!.ToString());
        Assert.NotNull(response.Headers.ETag);
        Assert.Equal(JsonValueKind.Null, created.GetProperty("accountId").ValueKind);
        Assert.Equal(bdr, created.GetProperty("ownerUserId").GetGuid());
        Assert.Equal("open", created.GetProperty("status").GetString());
        Assert.Equal(0, created.GetProperty("callTries").GetInt32());
        await using var db = fixture.CreateContext();
        var lead = await db.Leads.SingleAsync();
        Assert.Null(lead.AccountId);
        Assert.Null(lead.FirstName);
        Assert.Equal(bdr, lead.OwnerUserId);
        Assert.Equal(bdr, lead.CreatedBy);
        Assert.Equal("test_source_a", lead.LeadSource);
    }

    [DbFact]
    public async Task Ac2_A_lead_can_be_linked_to_an_existing_account_and_the_account_can_be_cleared_again()
    {
        var bdr = await SeedUserAsync("BDR");
        Guid accountId;
        await using (var db = fixture.CreateContext())
        {
            var account = new Account { Name = "Lindholm Demo AB", CountryCode = "SE" };
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            accountId = account.Id;
        }

        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var created = await CreateAsync(client, bdr, $$"""{"lastName":"Berg","companyName":"Berg Demo AB","leadSource":"test_source_a","accountId":"{{accountId}}"}""");
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(accountId, created.GetProperty("accountId").GetGuid());

        var cleared = await client.SendAsync(Send(HttpMethod.Patch, $"{Url}/{id}", bdr, """{"accountId":null}""", await ETagOfAsync(client, id, bdr)));

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ReadJson(cleared)).GetProperty("accountId").ValueKind);
    }

    // ---- AC 3: CRUD per the API conventions ----

    [DbFact]
    public async Task Ac3_Read_returns_the_lead_with_an_etag_and_304_for_an_unchanged_one()
    {
        var bdr = await SeedUserAsync("BDR");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var id = (await CreateAsync(client, bdr)).GetProperty("id").GetGuid();

        var read = await client.SendAsync(Send(HttpMethod.Get, $"{Url}/{id}", bdr));
        var again = await client.SendAsync(Send(HttpMethod.Get, $"{Url}/{id}", bdr, ifNoneMatch: read.Headers.ETag!.Tag));

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("Berg Demo AB", (await ReadJson(read)).GetProperty("companyName").GetString());
        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
        await AssertProblem(await client.SendAsync(Send(HttpMethod.Get, $"{Url}/{Guid.NewGuid()}", bdr)), HttpStatusCode.NotFound);
    }

    [DbFact]
    public async Task Ac3_A_lead_outside_the_callers_scope_is_a_404_and_absent_from_the_list()
    {
        var owner = await SeedUserAsync("BDR");
        var otherTeam = await SeedUserAsync("BDR");
        var management = await SeedUserAsync("Management");
        var pm = await SeedUserAsync("PM");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var id = (await CreateAsync(client, owner)).GetProperty("id").GetGuid();

        await AssertProblem(await client.SendAsync(Send(HttpMethod.Get, $"{Url}/{id}", otherTeam)), HttpStatusCode.NotFound);
        await AssertProblem(await client.SendAsync(Send(HttpMethod.Patch, $"{Url}/{id}", otherTeam, """{"jobTitle":"x"}""", "\"any\"")), HttpStatusCode.NotFound);
        Assert.Empty((await ReadJson(await client.SendAsync(Send(HttpMethod.Get, Url, otherTeam)))).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Send(HttpMethod.Get, $"{Url}/{id}", management))).StatusCode);
        Assert.Single((await ReadJson(await client.SendAsync(Send(HttpMethod.Get, Url, management)))).GetProperty("items").EnumerateArray());
        await AssertProblem(await client.SendAsync(Send(HttpMethod.Get, Url, pm)), HttpStatusCode.Forbidden);
    }

    [DbFact]
    public async Task Ac3_List_uses_keyset_paging_filters_and_leaves_out_deleted_leads()
    {
        var bdr = await SeedUserAsync("BDR");
        var admin = await SeedUserAsync("Admin");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var ids = new List<Guid>();
        foreach (var company in new[] { "One AB", "Two AB", "Three AB" })
        {
            ids.Add((await CreateAsync(client, bdr, Body.Replace("Berg Demo AB", company))).GetProperty("id").GetGuid());
        }

        var first = await ReadJson(await client.SendAsync(Send(HttpMethod.Get, $"{Url}?limit=2", bdr)));
        var cursor = first.GetProperty("nextCursor").GetString()!;
        var second = await ReadJson(await client.SendAsync(Send(HttpMethod.Get, $"{Url}?limit=2&cursor={Uri.EscapeDataString(cursor)}", bdr)));

        var paged = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray())
            .Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        Assert.Equal(ids.OrderBy(i => i).ToList(), paged.OrderBy(i => i).ToList());

        var bySource = await ReadJson(await client.SendAsync(Send(HttpMethod.Get, $"{Url}?filter%5BleadSource%5D%5Beq%5D=test_source_b", bdr)));
        var byStatus = await ReadJson(await client.SendAsync(Send(HttpMethod.Get, $"{Url}?filter%5Bstatus%5D%5Beq%5D=open", bdr)));
        Assert.Empty(bySource.GetProperty("items").EnumerateArray());
        Assert.Equal(3, byStatus.GetProperty("items").GetArrayLength());

        await client.SendAsync(Send(HttpMethod.Delete, $"{Url}/{ids[0]}", admin));
        var afterDelete = await ReadJson(await client.SendAsync(Send(HttpMethod.Get, Url, bdr)));
        Assert.DoesNotContain(afterDelete.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == ids[0]);
        Assert.Equal(2, afterDelete.GetProperty("items").GetArrayLength());
    }

    [DbFact]
    public async Task Ac3_A_bad_cursor_an_over_limit_page_size_and_an_unknown_filter_field_are_400()
    {
        var bdr = await SeedUserAsync("BDR");
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        await AssertProblem(await client.SendAsync(Send(HttpMethod.Get, $"{Url}?cursor=not-a-cursor", bdr)), HttpStatusCode.BadRequest, "cursor");
        await AssertProblem(await client.SendAsync(Send(HttpMethod.Get, $"{Url}?limit=201", bdr)), HttpStatusCode.BadRequest, "limit");
        await AssertProblem(await client.SendAsync(Send(HttpMethod.Get, $"{Url}?filter%5BcallTries%5D%5Beq%5D=1", bdr)), HttpStatusCode.BadRequest);
    }

    [DbFact]
    public async Task Ac3_Update_with_if_match_changes_the_lead_and_gives_a_new_etag_that_the_next_read_matches()
    {
        var bdr = await SeedUserAsync("BDR");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var id = (await CreateAsync(client, bdr, """{"lastName":"Berg","companyName":"Berg Demo AB","leadSource":"test_source_a","jobTitle":"CTO"}""")).GetProperty("id").GetGuid();
        await using (var db = fixture.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"update lead set call_tries = 3 where id = {id}");
        }

        var etag = await ETagOfAsync(client, id, bdr);
        const string patch = """{"jobTitle":null,"email":" anna@example.com ","website":"https://example.com","countryCode":"SE","nextStep":"Call back","nextStepDate":"2026-11-05T10:00:00+01:00"}""";
        var response = await client.SendAsync(Send(HttpMethod.Patch, $"{Url}/{id}", bdr, patch, etag));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadJson(response);
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("jobTitle").ValueKind);
        Assert.Equal("anna@example.com", updated.GetProperty("email").GetString());
        Assert.Equal("SE", updated.GetProperty("countryCode").GetString());
        Assert.Equal("Berg", updated.GetProperty("lastName").GetString());
        Assert.Equal(new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc), updated.GetProperty("nextStepDate").GetDateTime().ToUniversalTime());
        Assert.Equal(3, updated.GetProperty("callTries").GetInt32());
        Assert.NotEqual(etag, response.Headers.ETag!.Tag);
        Assert.Equal(response.Headers.ETag!.Tag, await ETagOfAsync(client, id, bdr));
    }

    [DbFact]
    public async Task Ac3_Update_without_if_match_is_428_and_with_a_stale_etag_is_412_and_nothing_changes()
    {
        var bdr = await SeedUserAsync("BDR");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var id = (await CreateAsync(client, bdr)).GetProperty("id").GetGuid();
        var stale = await ETagOfAsync(client, id, bdr);
        var fresh = await client.SendAsync(Send(HttpMethod.Patch, $"{Url}/{id}", bdr, """{"jobTitle":"CTO"}""", stale));
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);

        var missing = await client.SendAsync(Send(HttpMethod.Patch, $"{Url}/{id}", bdr, """{"jobTitle":"CEO"}"""));
        var old = await client.SendAsync(Send(HttpMethod.Patch, $"{Url}/{id}", bdr, """{"jobTitle":"CEO"}""", stale));

        await AssertProblem(missing, (HttpStatusCode)428);
        await AssertProblem(old, HttpStatusCode.PreconditionFailed);
        await using var db = fixture.CreateContext();
        Assert.Equal("CTO", (await db.Leads.SingleAsync()).JobTitle);
    }

    [DbFact]
    public async Task Ac3_An_invalid_update_is_422_and_a_client_cannot_change_status_or_call_tries()
    {
        var bdr = await SeedUserAsync("BDR");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var id = (await CreateAsync(client, bdr)).GetProperty("id").GetGuid();
        var etag = await ETagOfAsync(client, id, bdr);

        await AssertProblem(await client.SendAsync(Send(HttpMethod.Patch, $"{Url}/{id}", bdr, """{"leadSource":"test_source_b"}""", etag)),
            HttpStatusCode.UnprocessableEntity, "leadSource");
        await AssertProblem(await client.SendAsync(Send(HttpMethod.Patch, $"{Url}/{id}", bdr, """{"lastName":null,"companyName":""}""", etag)),
            HttpStatusCode.UnprocessableEntity, "lastName", "companyName");
        await AssertProblem(await client.SendAsync(Send(HttpMethod.Patch, $"{Url}/{id}", bdr, """{"status":"disqualified","callTries":9}""", etag)),
            HttpStatusCode.UnprocessableEntity, "status", "callTries");

        await using var db = fixture.CreateContext();
        var lead = await db.Leads.SingleAsync();
        Assert.Equal("open", lead.Status);
        Assert.Equal(0, lead.CallTries);
        Assert.Equal("Berg", lead.LastName);
        Assert.Equal("test_source_a", lead.LeadSource);
    }

    [DbFact]
    public async Task Ac3_Delete_is_a_soft_delete_for_an_admin_and_the_lead_disappears_from_reads_and_search()
    {
        var bdr = await SeedUserAsync("BDR");
        var admin = await SeedUserAsync("Admin");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var id = (await CreateAsync(client, bdr)).GetProperty("id").GetGuid();
        Assert.Equal(1, await SearchDocumentCountAsync());

        var deleted = await client.SendAsync(Send(HttpMethod.Delete, $"{Url}/{id}", admin));

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await AssertProblem(await client.SendAsync(Send(HttpMethod.Get, $"{Url}/{id}", admin)), HttpStatusCode.NotFound);
        Assert.Empty((await ReadJson(await client.SendAsync(Send(HttpMethod.Get, Url, admin)))).GetProperty("items").EnumerateArray());
        await AssertProblem(await client.SendAsync(Send(HttpMethod.Delete, $"{Url}/{id}", admin)), HttpStatusCode.NotFound);
        Assert.Equal(0, await SearchDocumentCountAsync());
        await using var db = fixture.CreateContext();
        var row = await db.Leads.IgnoreQueryFilters().SingleAsync();
        Assert.NotNull(row.DeletedAt);
        Assert.Equal(admin, row.UpdatedBy);
    }

    [DbFact]
    public async Task Ac3_Delete_is_refused_for_everyone_but_an_admin()
    {
        var bdr = await SeedUserAsync("BDR");
        var management = await SeedUserAsync("Management");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var id = (await CreateAsync(client, bdr)).GetProperty("id").GetGuid();

        foreach (var caller in new Guid?[] { bdr, management, null })
        {
            await AssertProblem(await client.SendAsync(Send(HttpMethod.Delete, $"{Url}/{id}", caller)), HttpStatusCode.Forbidden);
        }

        await using var db = fixture.CreateContext();
        Assert.Null((await db.Leads.SingleAsync()).DeletedAt);
    }

    // ---- who may write ----

    [DbFact]
    public async Task Create_and_update_follow_the_permission_matrix()
    {
        var bdr = await SeedUserAsync("BDR");
        var salesperson = await SeedUserAsync("Salesperson");
        var management = await SeedUserAsync("Management");
        var admin = await SeedUserAsync("Admin");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var id = (await CreateAsync(client, bdr)).GetProperty("id").GetGuid();
        var etag = await ETagOfAsync(client, id, management);

        foreach (var caller in new Guid?[] { salesperson, management, admin, null })
        {
            var create = await client.SendAsync(Send(HttpMethod.Post, Url, caller, Body));
            Assert.Equal(caller is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, create.StatusCode);
        }

        foreach (var caller in new Guid?[] { management, admin })
        {
            await AssertProblem(await client.SendAsync(Send(HttpMethod.Patch, $"{Url}/{id}", caller, """{"jobTitle":"Hacked"}""", etag)), HttpStatusCode.Forbidden);
        }

        Assert.Equal(1, await LeadCountAsync());
        await using var db = fixture.CreateContext();
        Assert.Null((await db.Leads.SingleAsync()).JobTitle);
    }

    // ---- audit and data model ----

    [DbFact]
    public async Task Writes_are_audited_with_the_user_and_the_field_names_but_not_the_values()
    {
        var bdr = await SeedUserAsync("BDR");
        var admin = await SeedUserAsync("Admin");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var id = (await CreateAsync(client, bdr)).GetProperty("id").GetGuid();
        await client.SendAsync(Send(HttpMethod.Patch, $"{Url}/{id}", bdr, """{"jobTitle":"Secret title"}""", await ETagOfAsync(client, id, bdr)));
        await client.SendAsync(Send(HttpMethod.Delete, $"{Url}/{id}", admin));

        await using var db = fixture.CreateContext();
        var entries = await db.AuditLogs.Where(a => a.EntityId == id).OrderBy(a => a.At).ToListAsync();

        Assert.Equal(new[] { "lead_created", "lead_updated", "lead_deleted" }, entries.Select(e => e.Action));
        Assert.Equal(new[] { bdr, bdr, admin }, entries.Select(e => e.UserId!.Value));
        Assert.All(entries, e => Assert.Equal("lead", e.EntityType));
        Assert.Contains("jobTitle", entries[1].Detail);
        Assert.All(entries, e => Assert.DoesNotContain("Secret title", e.Detail));
        Assert.All(entries, e => Assert.DoesNotContain("Berg", e.Detail));
    }

    [DbFact]
    public async Task The_database_refuses_a_negative_call_tries_an_unknown_status_and_an_unknown_lead_source()
    {
        var bdr = await SeedUserAsync("BDR");
        var changes = new Action<Lead>[] { l => l.CallTries = -1, l => l.Status = "bogus", l => l.LeadSource = "no_such_source" };

        foreach (var change in changes)
        {
            await using var db = fixture.CreateContext();
            var lead = new Lead { LastName = "Berg", CompanyName = "Berg Demo AB", LeadSource = "test_source_a", OwnerUserId = bdr };
            change(lead);
            db.Leads.Add(lead);

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [DbFact]
    public async Task Reset_between_tests_works_while_leads_and_their_search_documents_exist()
    {
        // Regression: the lead table has a search trigger and foreign keys; Respawn must still empty everything.
        var bdr = await SeedUserAsync("BDR");
        await using var app = await StartAsync();
        await CreateAsync(app.GetTestClient(), bdr);
        Assert.Equal(1, await SearchDocumentCountAsync());

        await fixture.ResetAsync();

        Assert.Equal(0, await LeadCountAsync());
        Assert.Equal(0, await SearchDocumentCountAsync());
    }
}
