using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zephyr.Api.Common;
using Zephyr.Api.Data;
using Zephyr.Api.Permissions;
using Zephyr.Api.Tests.Support;

namespace Zephyr.Api.Tests.Common;

/// <summary>Stands in for the metadata validator that every write runs through (WriteHooks): <c>reject: true</c> fails the write.</summary>
public sealed class RejectFlagValidator : IWriteValidator
{
    public Task ValidateAsync(string entityType, JsonElement body, ModelState state)
    {
        if (body.TryGetProperty("reject", out var flag) && flag.ValueKind == JsonValueKind.True)
        {
            state.Add("reject", "REJECTED", "reject must not be true.");
        }

        return Task.CompletedTask;
    }
}

[Collection("db")]
public class UpsertTests(DbFixture fixture) : IAsyncLifetime
{
    private const string Body = """{"name":"Acme","countryCode":"SE"}""";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- test host: the real upsert and delete routes plus two test-only reads (the account reads belong to a later story) ----

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddApiConventions();
        builder.Services.AddScoped<IWriteValidator, RejectFlagValidator>();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", null);
        var app = builder.Build();
        app.UseApiConventions();
        app.UseAuthentication();
        app.MapExternalUpsert(EntityRegistry.Accounts);
        app.MapSoftDelete(EntityRegistry.Accounts);
        app.MapGet("/api/v1/accounts/{id:guid}", async Task<IResult> (Guid id, ZephyrDbContext db) =>
        {
            var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id);
            return account is null ? Results.NotFound() : Results.Ok(new { account.Id, account.Name });
        });
        app.MapGet("/api/v1/accounts", async (ZephyrDbContext db) => Results.Ok(await db.Accounts.AsNoTracking().Select(a => a.Id).ToListAsync()));
        await app.StartAsync();
        return app;
    }

    // ---- helpers ----

    private static string UpsertUrl(string system, string value) => $"/api/v1/accounts/by-external-id/{system}/{Uri.EscapeDataString(value)}";

    private static HttpRequestMessage Send(HttpMethod method, string url, Guid? user, string? json = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (user is not null)
        {
            request.Headers.Add("X-Test-User", user.ToString());
        }

        return request;
    }

    private static HttpRequestMessage Put(string url, Guid? user, string json = Body) => Send(HttpMethod.Put, url, user, json);

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private async Task<Guid> SeedUserAsync(string? role, bool active = true)
    {
        await using var db = fixture.CreateContext();
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Name = role ?? "No role",
            Email = $"{Guid.NewGuid():N}@example.test",
            Roles = role is null ? [] : [role],
            Active = active
        };
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<(int Accounts, int ExternalIds)> CountsAsync()
    {
        await using var db = fixture.CreateContext();
        return (await db.Accounts.IgnoreQueryFilters().CountAsync(), await db.ExternalIds.IgnoreQueryFilters().CountAsync());
    }

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string? code = null, string? field = null)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadJson(response);
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        if (code is not null)
        {
            Assert.Equal(code, problem.GetProperty("code").GetString());
        }

        if (field is not null)
        {
            Assert.Contains(problem.GetProperty("errors").EnumerateArray(), e => e.GetProperty("field").GetString() == field);
        }
    }

    // ---- AC 1: upsert twice ----

    [DbFact]
    public async Task Ac1_Upserting_twice_creates_once_then_updates_the_same_row()
    {
        var integration = await SeedUserAsync("Integration");
        var admin = await SeedUserAsync("Admin");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var url = UpsertUrl("dynamics", "guid-1");

        var first = await client.SendAsync(Put(url, integration));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var created = await ReadJson(first);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/accounts/{id}", first.Headers.Location!.ToString());
        Assert.NotNull(first.Headers.ETag);
        Assert.Equal("Acme", created.GetProperty("name").GetString());

        var second = await client.SendAsync(Put(url, admin, """{"name":"Acme AB","countryCode":"NO"}"""));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var updated = await ReadJson(second);
        Assert.Equal(id, updated.GetProperty("id").GetGuid());
        Assert.Equal("Acme AB", updated.GetProperty("name").GetString());
        Assert.NotEqual(first.Headers.ETag!.Tag, second.Headers.ETag!.Tag);

        Assert.Equal((1, 1), await CountsAsync());
        await using var db = fixture.CreateContext();
        var account = await db.Accounts.SingleAsync();
        Assert.Equal(("Acme AB", "NO"), (account.Name, account.CountryCode));
        Assert.Equal(integration, account.CreatedBy);
        Assert.Equal(admin, account.UpdatedBy);
        var mapping = await db.ExternalIds.SingleAsync();
        Assert.Equal((id, "account", "dynamics", "guid-1"), (mapping.EntityId, mapping.EntityType, mapping.System, mapping.Value));
    }

    [DbFact]
    public async Task Ac1_Resending_an_identical_body_is_a_200_that_changes_nothing()
    {
        var integration = await SeedUserAsync("Integration");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var url = UpsertUrl("fortnox", "100");

        var first = await client.SendAsync(Put(url, integration));
        var again = await client.SendAsync(Put(url, integration));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(first.Headers.ETag!.Tag, again.Headers.ETag!.Tag);
        Assert.Equal((1, 1), await CountsAsync());
    }

    [DbFact]
    public async Task Ac1_The_value_is_url_decoded_and_the_upsert_ignores_if_match()
    {
        var integration = await SeedUserAsync("Integration");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var url = UpsertUrl("dynamics", "{A1B2} c+d");

        await client.SendAsync(Put(url, integration));
        var request = Put(url, integration, """{"name":"Renamed"}""");
        request.Headers.TryAddWithoutValidation("If-Match", "\"stale\"");
        var second = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        await using var db = fixture.CreateContext();
        Assert.Equal("{A1B2} c+d", (await db.ExternalIds.SingleAsync()).Value);
        Assert.Null((await db.Accounts.SingleAsync()).CountryCode); // PUT replaces: an absent countryCode is cleared
    }

    // ---- AC 2: soft delete ----

    [DbFact]
    public async Task Ac2_Delete_is_204_hides_the_row_and_a_repeat_is_404()
    {
        var integration = await SeedUserAsync("Integration");
        var admin = await SeedUserAsync("Admin");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var id = (await ReadJson(await client.SendAsync(Put(UpsertUrl("dynamics", "d1"), integration)))).GetProperty("id").GetGuid();
        await client.SendAsync(Put(UpsertUrl("dynamics", "d2"), integration, """{"name":"Other"}"""));

        var deleted = await client.SendAsync(Send(HttpMethod.Delete, $"/api/v1/accounts/{id}", admin));

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/accounts/{id}")).StatusCode);
        var list = await ReadJson(await client.GetAsync("/api/v1/accounts"));
        Assert.Equal(1, list.GetArrayLength());
        Assert.DoesNotContain(list.EnumerateArray(), item => item.GetGuid() == id);
        await using (var db = fixture.CreateContext())
        {
            var row = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == id);
            Assert.NotNull(row.DeletedAt);
            Assert.Equal(admin, row.UpdatedBy);
        }

        var repeat = await client.SendAsync(Send(HttpMethod.Delete, $"/api/v1/accounts/{id}", admin));
        await AssertProblem(repeat, HttpStatusCode.NotFound);
        await AssertProblem(await client.SendAsync(Send(HttpMethod.Delete, $"/api/v1/accounts/{Guid.NewGuid()}", admin)), HttpStatusCode.NotFound);
    }

    [DbFact]
    public async Task Ac2_Only_an_admin_may_delete()
    {
        var integration = await SeedUserAsync("Integration");
        var salesperson = await SeedUserAsync("Salesperson");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var id = (await ReadJson(await client.SendAsync(Put(UpsertUrl("dynamics", "d1"), integration)))).GetProperty("id").GetGuid();

        foreach (var user in new Guid?[] { integration, salesperson, null })
        {
            await AssertProblem(await client.SendAsync(Send(HttpMethod.Delete, $"/api/v1/accounts/{id}", user)), HttpStatusCode.Forbidden, "FORBIDDEN");
        }

        await using var db = fixture.CreateContext();
        Assert.Null((await db.Accounts.SingleAsync(a => a.Id == id)).DeletedAt);
    }

    // ---- AC 3: concurrent upsert ----

    [DbFact]
    public async Task Ac3_Simultaneous_upserts_of_one_key_make_one_row_and_never_fail()
    {
        var integration = await SeedUserAsync("Integration");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var url = UpsertUrl("dynamics", "race");

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.SendAsync(Put(url, integration))));

        var statuses = responses.Select(r => r.StatusCode).ToList();
        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal((1, 1), await CountsAsync());
        var ids = await Task.WhenAll(responses.Select(async r => (await ReadJson(r)).GetProperty("id").GetGuid()));
        Assert.Single(ids.Distinct());
    }

    // ---- AC 4: same value, different scope ----

    [DbFact]
    public async Task Ac4_The_same_value_under_another_system_or_entity_type_is_a_separate_record()
    {
        var integration = await SeedUserAsync("Integration");
        await using (var db = fixture.CreateContext())
        {
            db.ExternalIds.Add(new ExternalId { EntityType = "lead", EntityId = Guid.NewGuid(), System = "dynamics", Value = "k1" });
            await db.SaveChangesAsync();
        }

        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var dynamics = await client.SendAsync(Put(UpsertUrl("dynamics", "k1"), integration)); // same value and system, other entity type
        var fortnox = await client.SendAsync(Put(UpsertUrl("fortnox", "k1"), integration)); // same value, other system

        Assert.Equal(HttpStatusCode.Created, dynamics.StatusCode);
        Assert.Equal(HttpStatusCode.Created, fortnox.StatusCode);
        Assert.NotEqual((await ReadJson(dynamics)).GetProperty("id").GetGuid(), (await ReadJson(fortnox)).GetProperty("id").GetGuid());
        Assert.Equal((2, 3), await CountsAsync());
    }

    // ---- AC 5: upsert of a deleted record ----

    [DbFact]
    public async Task Ac5_Upserting_a_deleted_record_is_409_and_revives_nothing()
    {
        var integration = await SeedUserAsync("Integration");
        var admin = await SeedUserAsync("Admin");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var url = UpsertUrl("dynamics", "gone");
        var id = (await ReadJson(await client.SendAsync(Put(url, integration)))).GetProperty("id").GetGuid();
        await client.SendAsync(Send(HttpMethod.Delete, $"/api/v1/accounts/{id}", admin));

        var response = await client.SendAsync(Put(url, integration, """{"name":"Back again"}"""));

        await AssertProblem(response, HttpStatusCode.Conflict, "RECORD_DELETED");
        Assert.Equal((1, 1), await CountsAsync());
        await using var db = fixture.CreateContext();
        var row = await db.Accounts.IgnoreQueryFilters().SingleAsync();
        Assert.NotNull(row.DeletedAt);
        Assert.Equal("Acme", row.Name);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/accounts/{id}")).StatusCode);
    }

    // ---- AC 6: who may upsert ----

    [DbFact]
    public async Task Ac6_Admin_and_integration_succeed_including_a_user_who_also_holds_other_roles()
    {
        var admin = await SeedUserAsync("Admin");
        var integration = await SeedUserAsync("Integration");
        var mixed = await SeedUserAsync("Salesperson");
        await using (var db = fixture.CreateContext())
        {
            var user = await db.AppUsers.SingleAsync(u => u.Id == mixed);
            user.Roles = ["Salesperson", "Integration"];
            await db.SaveChangesAsync();
        }

        await using var app = await StartAsync();
        var client = app.GetTestClient();

        foreach (var (user, key) in new[] { (admin, "a"), (integration, "i"), (mixed, "m") })
        {
            Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(Put(UpsertUrl("dynamics", key), user))).StatusCode);
        }
    }

    [DbFact]
    public async Task Ac6_Every_other_role_a_missing_role_an_inactive_user_and_no_user_get_403_and_nothing_changes()
    {
        var callers = new List<Guid?>();
        foreach (var role in new[] { "Salesperson", "BDR", "PM", "Consultant", "Finance", "Management", null })
        {
            callers.Add(await SeedUserAsync(role));
        }

        callers.Add(await SeedUserAsync("Integration", active: false));
        callers.Add(Guid.NewGuid()); // signed in, but not a known user
        callers.Add(null);
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        foreach (var caller in callers)
        {
            await AssertProblem(await client.SendAsync(Put(UpsertUrl("dynamics", "nope"), caller)), HttpStatusCode.Forbidden, "FORBIDDEN");
        }

        Assert.Equal((0, 0), await CountsAsync());
    }

    [DbFact]
    public async Task Ac6_A_forbidden_caller_cannot_change_an_existing_record()
    {
        var integration = await SeedUserAsync("Integration");
        var salesperson = await SeedUserAsync("Salesperson");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var url = UpsertUrl("dynamics", "k");
        await client.SendAsync(Put(url, integration));

        var response = await client.SendAsync(Put(url, salesperson, """{"name":"Hacked"}"""));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var db = fixture.CreateContext();
        Assert.Equal("Acme", (await db.Accounts.SingleAsync()).Name);
    }

    // ---- AC 7: validation ----

    [DbFact]
    public async Task Ac7_An_invalid_body_is_422_and_leaves_no_external_id_row()
    {
        var integration = await SeedUserAsync("Integration");
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        await AssertProblem(await client.SendAsync(Put(UpsertUrl("dynamics", "v1"), integration, """{"countryCode":"SE"}""")),
            (HttpStatusCode)422, "VALIDATION", "name");
        await AssertProblem(await client.SendAsync(Put(UpsertUrl("dynamics", "v2"), integration, """{"name":"  "}""")),
            (HttpStatusCode)422, "VALIDATION", "name");
        await AssertProblem(await client.SendAsync(Put(UpsertUrl("dynamics", "v3"), integration, """{"name":"X","countryCode":"swe"}""")),
            (HttpStatusCode)422, "VALIDATION", "countryCode");

        Assert.Equal((0, 0), await CountsAsync());
    }

    [DbFact]
    public async Task Ac7_Write_hooks_run_and_their_errors_are_422_with_nothing_written()
    {
        var integration = await SeedUserAsync("Integration");
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var response = await client.SendAsync(Put(UpsertUrl("dynamics", "hook"), integration, """{"name":"X","reject":true}"""));

        await AssertProblem(response, (HttpStatusCode)422, "VALIDATION", "reject");
        Assert.Equal((0, 0), await CountsAsync());
    }

    [DbFact]
    public async Task Ac7_An_invalid_update_of_an_existing_key_changes_nothing()
    {
        var integration = await SeedUserAsync("Integration");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var url = UpsertUrl("dynamics", "k");
        await client.SendAsync(Put(url, integration));

        var response = await client.SendAsync(Put(url, integration, """{"name":""}"""));

        await AssertProblem(response, (HttpStatusCode)422, "VALIDATION", "name");
        await using var db = fixture.CreateContext();
        Assert.Equal("Acme", (await db.Accounts.SingleAsync()).Name);
    }

    [DbFact]
    public async Task Ac7_An_unknown_system_a_too_long_value_and_a_malformed_body_are_rejected_before_anything_is_written()
    {
        var integration = await SeedUserAsync("Integration");
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        await AssertProblem(await client.SendAsync(Put(UpsertUrl("salesforce", "x"), integration)), (HttpStatusCode)422, "VALIDATION", "system");
        await AssertProblem(await client.SendAsync(Put(UpsertUrl("dynamics", new string('x', 201)), integration)), (HttpStatusCode)422, "VALIDATION", "value");
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(Put(UpsertUrl("dynamics", new string('x', 200)), integration))).StatusCode);
        await AssertProblem(await client.SendAsync(Put(UpsertUrl("dynamics", "m"), integration, """{"name":""")), HttpStatusCode.BadRequest, field: "body");

        Assert.Equal((1, 1), await CountsAsync());
    }

    // ---- wiring ----

    [DbFact]
    public void The_real_app_exposes_the_upsert_and_delete_routes_for_accounts()
    {
        using var factory = new WebApplicationFactory<Program>();

        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => (e.RoutePattern.RawText, Methods: string.Join(",", e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])))
            .ToList();

        Assert.Contains(("/api/v1/accounts/by-external-id/{system}/{value}", "PUT"), routes);
        Assert.Contains(("/api/v1/accounts/{id:guid}", "DELETE"), routes);
    }
}
