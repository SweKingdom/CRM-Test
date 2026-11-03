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
using Npgsql;
using Zephyr.Api.Common;
using Zephyr.Api.Data;
using Zephyr.Api.Permissions;
using Zephyr.Api.Tests.Common;
using Zephyr.Api.Tests.Support;

namespace Zephyr.Api.Tests.Permissions;

/// <summary>
/// US-145. The approver and delegation endpoints are tested through HTTP. Time-entry reads and the approval action do not exist yet
/// (US-166, US-170), so AC 3 and AC 4 are proven on the helpers they will call: <c>EnsureNotSelfApproval</c> and <c>ApproverScopeAsync</c>.
/// </summary>
[Collection("db")]
public class ApproverTests(DbFixture fixture) : IAsyncLifetime
{
    private static readonly DateOnly Day = new(2026, 11, 3);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- test host: the real approver and delegation routes ----

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddApiConventions();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", null);
        var app = builder.Build();
        app.UseApiConventions();
        app.UseAuthentication();
        app.MapApprovers();
        await app.StartAsync();
        return app;
    }

    // ---- helpers ----

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

    private static string ApproverUrl(Guid project, Guid user) => $"/api/v1/projects/{project}/approvers/{user}";

    private static string DelegationJson(Guid delegateId, string from, string to, Guid? delegatorId = null)
    {
        var extra = delegatorId is null ? "" : $",\"delegatorId\":\"{delegatorId}\"";
        return $"{{\"delegateId\":\"{delegateId}\",\"fromDate\":\"{from}\",\"toDate\":\"{to}\"{extra}}}";
    }

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

    private async Task SeedApproverAsync(Guid project, Guid user)
    {
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"insert into project_approver (project_id, user_id) values ({project}, {user})");
    }

    private async Task<IReadOnlyList<Guid>> ScopeAsync(Guid user, DateOnly day)
    {
        await using var db = fixture.CreateContext();
        return await Approvers.ApproverScopeAsync(db, user, day);
    }

    private async Task<int> CountAsync(string sql)
    {
        await using var db = fixture.CreateContext();
        return await db.Database.SqlQueryRaw<int>(sql).SingleAsync();
    }

    private async Task<int> AuditCountAsync(Guid entityId, string action)
    {
        await using var db = fixture.CreateContext();
        return await db.AuditLogs.CountAsync(a => a.EntityId == entityId && a.Action == action);
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

    private static async Task<Guid> CreateDelegationAsync(HttpClient client, Guid caller, Guid delegateId, string from, string to)
    {
        var response = await client.SendAsync(Send(HttpMethod.Post, "/api/v1/delegations", caller, DelegationJson(delegateId, from, to)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetGuid();
    }

    // ---- AC 1: approver set ----

    [DbFact]
    public async Task Ac1_Admin_adds_an_approver_who_then_has_the_project_in_scope_and_delete_removes_it()
    {
        var admin = await SeedUserAsync("Admin");
        var pm = await SeedUserAsync("PM");
        var project = Guid.NewGuid();
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var put = await client.SendAsync(Send(HttpMethod.Put, ApproverUrl(project, pm), admin));
        var again = await client.SendAsync(Send(HttpMethod.Put, ApproverUrl(project, pm), admin));

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(1, await CountAsync($"""select count(*)::int as "Value" from project_approver where project_id = '{project}'"""));
        Assert.Equal(project, Assert.Single(await ScopeAsync(pm, Day)));
        await using (var db = fixture.CreateContext())
        {
            Assert.True(await Approvers.IsApproverOfAsync(db, pm, project, Day));
            Assert.False(await Approvers.IsApproverOfAsync(db, pm, Guid.NewGuid(), Day));
        }

        Assert.Equal(1, await AuditCountAsync(project, "approver_added"));

        var delete = await client.SendAsync(Send(HttpMethod.Delete, ApproverUrl(project, pm), admin));
        var deleteAgain = await client.SendAsync(Send(HttpMethod.Delete, ApproverUrl(project, pm), admin));

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleteAgain.StatusCode);
        Assert.Empty(await ScopeAsync(pm, Day));
        Assert.Equal(1, await AuditCountAsync(project, "approver_removed"));
    }

    [DbFact]
    public async Task Ac1_Only_an_admin_changes_approvers_and_an_unknown_user_is_404()
    {
        var admin = await SeedUserAsync("Admin");
        var pm = await SeedUserAsync("PM");
        var consultant = await SeedUserAsync("Consultant");
        var project = Guid.NewGuid();
        await SeedApproverAsync(project, pm);
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        foreach (var caller in new Guid?[] { pm, consultant, Guid.NewGuid(), null })
        {
            await AssertProblem(await client.SendAsync(Send(HttpMethod.Put, ApproverUrl(project, consultant), caller)), HttpStatusCode.Forbidden, "FORBIDDEN");
            await AssertProblem(await client.SendAsync(Send(HttpMethod.Delete, ApproverUrl(project, pm), caller)), HttpStatusCode.Forbidden, "FORBIDDEN");
        }

        await AssertProblem(await client.SendAsync(Send(HttpMethod.Put, ApproverUrl(project, Guid.NewGuid()), admin)), HttpStatusCode.NotFound);
        Assert.Equal(1, await CountAsync("""select count(*)::int as "Value" from project_approver"""));
        Assert.Empty(await ScopeAsync(consultant, Day));
    }

    // ---- AC 2: delegation ----

    [DbFact]
    public async Task Ac2_A_delegate_holds_the_delegators_projects_from_fromDate_to_toDate_inclusive()
    {
        var pm = await SeedUserAsync("PM");
        var delegateId = await SeedUserAsync("PM");
        var (p1, p2) = (Guid.NewGuid(), Guid.NewGuid());
        await SeedApproverAsync(p1, pm);
        await SeedApproverAsync(p2, pm);
        await SeedApproverAsync(Guid.NewGuid(), delegateId); // the delegate's own project stays theirs
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var response = await client.SendAsync(Send(HttpMethod.Post, "/api/v1/delegations", pm, DelegationJson(delegateId, "2026-11-10", "2026-11-12")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadJson(response);
        Assert.NotEqual(Guid.Empty, created.GetProperty("id").GetGuid());
        Assert.Equal(pm, created.GetProperty("delegatorId").GetGuid());
        Assert.Equal(delegateId, created.GetProperty("delegateId").GetGuid());
        Assert.Equal("2026-11-10", created.GetProperty("fromDate").GetString());
        Assert.Equal("2026-11-12", created.GetProperty("toDate").GetString());

        Assert.Single(await ScopeAsync(delegateId, new DateOnly(2026, 11, 9)));
        foreach (var day in new[] { new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 12) })
        {
            var scope = await ScopeAsync(delegateId, day);
            Assert.Equal(3, scope.Count);
            Assert.Contains(p1, scope);
            Assert.Contains(p2, scope);
        }

        Assert.Single(await ScopeAsync(delegateId, new DateOnly(2026, 11, 13)));
        Assert.Equal(2, (await ScopeAsync(pm, new DateOnly(2026, 11, 13))).Count);
        await using var db = fixture.CreateContext();
        Assert.False(await Approvers.IsApproverOfAsync(db, delegateId, p1, new DateOnly(2026, 11, 9)));
        Assert.True(await Approvers.IsApproverOfAsync(db, delegateId, p1, new DateOnly(2026, 11, 12)));
        Assert.Equal(1, await AuditCountAsync(created.GetProperty("id").GetGuid(), "delegation_created"));
    }

    [DbFact]
    public async Task Ac2_Invalid_delegations_are_422_and_nothing_is_stored()
    {
        var pm = await SeedUserAsync("PM");
        var colleague = await SeedUserAsync("PM");
        var plain = await SeedUserAsync("Consultant");
        await SeedApproverAsync(Guid.NewGuid(), pm);
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        async Task Reject(Guid caller, string json, string field) =>
            await AssertProblem(await client.SendAsync(Send(HttpMethod.Post, "/api/v1/delegations", caller, json)), (HttpStatusCode)422, "VALIDATION", field);

        await Reject(pm, DelegationJson(colleague, "2026-11-12", "2026-11-10"), "toDate");
        await Reject(pm, DelegationJson(pm, "2026-11-10", "2026-11-12"), "delegateId");
        await Reject(plain, DelegationJson(colleague, "2026-11-10", "2026-11-12"), "delegatorId");
        await Reject(pm, DelegationJson(Guid.NewGuid(), "2026-11-10", "2026-11-12"), "delegateId");
        await Reject(pm, """{"fromDate":"2026-11-10","toDate":"2026-11-12"}""", "delegateId");
        await Reject(pm, $$"""{"delegateId":"{{colleague}}","toDate":"2026-11-12"}""", "fromDate");
        await Reject(pm, $$"""{"delegateId":"{{colleague}}","fromDate":"10/11/2026","toDate":"2026-11-12"}""", "fromDate");
        await Reject(pm, """{"delegateId":"not-a-guid","fromDate":"2026-11-10","toDate":"2026-11-12"}""", "delegateId");

        Assert.Equal(0, await CountAsync("""select count(*)::int as "Value" from approval_delegation"""));
        await AssertProblem(await client.SendAsync(Send(HttpMethod.Post, "/api/v1/delegations", pm, "[1]")), HttpStatusCode.BadRequest, field: "body");
    }

    [DbFact]
    public async Task Ac2_A_one_day_delegation_is_valid()
    {
        var pm = await SeedUserAsync("PM");
        var colleague = await SeedUserAsync("PM");
        await SeedApproverAsync(Guid.NewGuid(), pm);
        await using var app = await StartAsync();

        await CreateDelegationAsync(app.GetTestClient(), pm, colleague, "2026-11-10", "2026-11-10");

        Assert.Single(await ScopeAsync(colleague, new DateOnly(2026, 11, 10)));
    }

    [DbFact]
    public async Task Ac2_An_admin_may_delegate_on_behalf_of_an_approver_but_other_users_may_not()
    {
        var admin = await SeedUserAsync("Admin");
        var pm = await SeedUserAsync("PM");
        var colleague = await SeedUserAsync("PM");
        await SeedApproverAsync(Guid.NewGuid(), pm);
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var onBehalf = await client.SendAsync(Send(HttpMethod.Post, "/api/v1/delegations", admin, DelegationJson(colleague, "2026-11-10", "2026-11-12", pm)));
        var forged = await client.SendAsync(Send(HttpMethod.Post, "/api/v1/delegations", colleague, DelegationJson(admin, "2026-11-10", "2026-11-12", pm)));
        var adminAlone = await client.SendAsync(Send(HttpMethod.Post, "/api/v1/delegations", admin, DelegationJson(colleague, "2026-11-10", "2026-11-12")));

        Assert.Equal(HttpStatusCode.Created, onBehalf.StatusCode);
        Assert.Equal(pm, (await ReadJson(onBehalf)).GetProperty("delegatorId").GetGuid());
        await AssertProblem(forged, HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblem(adminAlone, (HttpStatusCode)422, "VALIDATION", "delegatorId"); // the admin approves no project
        Assert.Equal(1, await CountAsync("""select count(*)::int as "Value" from approval_delegation"""));
    }

    [DbFact]
    public async Task Ac2_Callers_who_are_not_active_users_get_403_on_every_delegation_route()
    {
        var inactive = await SeedUserAsync("PM", active: false);
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        foreach (var caller in new Guid?[] { inactive, Guid.NewGuid(), null })
        {
            await AssertProblem(await client.SendAsync(Send(HttpMethod.Post, "/api/v1/delegations", caller, DelegationJson(Guid.NewGuid(), "2026-11-10", "2026-11-12"))), HttpStatusCode.Forbidden, "FORBIDDEN");
            await AssertProblem(await client.SendAsync(Send(HttpMethod.Get, "/api/v1/delegations", caller)), HttpStatusCode.Forbidden, "FORBIDDEN");
            await AssertProblem(await client.SendAsync(Send(HttpMethod.Delete, $"/api/v1/delegations/{Guid.NewGuid()}", caller)), HttpStatusCode.Forbidden, "FORBIDDEN");
        }
    }

    // ---- AC 3: self-approval ----

    [Fact]
    public void Ac3_Approving_an_entry_of_your_own_is_403_and_someone_elses_is_allowed()
    {
        var caller = Guid.NewGuid();

        var refused = Assert.Throws<ProblemException>(() => Approvers.EnsureNotSelfApproval(caller, caller));

        Assert.Equal(StatusCodes.Status403Forbidden, refused.Status);
        Assert.Equal(ErrorCodes.Forbidden, refused.Code);
        Approvers.EnsureNotSelfApproval(Guid.NewGuid(), caller);
    }

    // ---- AC 4: consultant isolation ----

    [DbFact]
    public async Task Ac4_A_user_who_is_not_an_approver_has_no_approver_scope()
    {
        var consultant = await SeedUserAsync("Consultant");
        var pm = await SeedUserAsync("PM");
        var project = Guid.NewGuid();
        await SeedApproverAsync(project, pm);

        Assert.Empty(await ScopeAsync(consultant, Day));
        await using var db = fixture.CreateContext();
        Assert.False(await Approvers.IsApproverOfAsync(db, consultant, project, Day));
    }

    // ---- AC 5: revoke ----

    [DbFact]
    public async Task Ac5_Revoking_ends_the_delegation_at_once_and_a_second_delete_is_404()
    {
        var pm = await SeedUserAsync("PM");
        var delegateId = await SeedUserAsync("PM");
        await SeedApproverAsync(Guid.NewGuid(), pm);
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var id = await CreateDelegationAsync(client, pm, delegateId, "2026-11-01", "2026-11-30");
        Assert.Single(await ScopeAsync(delegateId, Day));

        var revoked = await client.SendAsync(Send(HttpMethod.Delete, $"/api/v1/delegations/{id}", pm));

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Empty(await ScopeAsync(delegateId, Day));
        Assert.Equal(1, await CountAsync($"""select count(*)::int as "Value" from approval_delegation where id = '{id}' and revoked_at is not null"""));
        await AssertProblem(await client.SendAsync(Send(HttpMethod.Delete, $"/api/v1/delegations/{id}", pm)), HttpStatusCode.NotFound);
        await AssertProblem(await client.SendAsync(Send(HttpMethod.Delete, $"/api/v1/delegations/{Guid.NewGuid()}", pm)), HttpStatusCode.NotFound);
        Assert.Equal(1, await AuditCountAsync(id, "delegation_revoked"));
    }

    [DbFact]
    public async Task Ac5_Only_the_delegator_or_an_admin_can_revoke()
    {
        var admin = await SeedUserAsync("Admin");
        var pm = await SeedUserAsync("PM");
        var delegateId = await SeedUserAsync("PM");
        var stranger = await SeedUserAsync("Consultant");
        await SeedApproverAsync(Guid.NewGuid(), pm);
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var id = await CreateDelegationAsync(client, pm, delegateId, "2026-11-01", "2026-11-30");

        foreach (var caller in new[] { stranger, delegateId })
        {
            await AssertProblem(await client.SendAsync(Send(HttpMethod.Delete, $"/api/v1/delegations/{id}", caller)), HttpStatusCode.NotFound);
        }

        Assert.Single(await ScopeAsync(delegateId, Day));
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Send(HttpMethod.Delete, $"/api/v1/delegations/{id}", admin))).StatusCode);
        Assert.Empty(await ScopeAsync(delegateId, Day));
    }

    // ---- AC 6: not transitive ----

    [DbFact]
    public async Task Ac6_A_delegate_cannot_delegate_the_rights_they_were_delegated()
    {
        var pm = await SeedUserAsync("PM");
        var delegateId = await SeedUserAsync("PM");
        var third = await SeedUserAsync("PM");
        await SeedApproverAsync(Guid.NewGuid(), pm);
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        await CreateDelegationAsync(client, pm, delegateId, "2026-11-01", "2026-11-30");
        Assert.Single(await ScopeAsync(delegateId, Day));

        var response = await client.SendAsync(Send(HttpMethod.Post, "/api/v1/delegations", delegateId, DelegationJson(third, "2026-11-01", "2026-11-30")));

        await AssertProblem(response, (HttpStatusCode)422, "VALIDATION", "delegatorId");
        Assert.Empty(await ScopeAsync(third, Day));
        Assert.Equal(1, await CountAsync("""select count(*)::int as "Value" from approval_delegation"""));
    }

    [DbFact]
    public async Task Ac6_A_chain_of_delegations_stored_directly_still_gives_only_the_first_delegators_own_projects()
    {
        var a = await SeedUserAsync("PM");
        var b = await SeedUserAsync("PM");
        var c = await SeedUserAsync("PM");
        await SeedApproverAsync(Guid.NewGuid(), a);
        await using (var db = fixture.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"insert into approval_delegation (delegator_id, delegate_id, from_date, to_date) values ({a}, {b}, '2026-11-01', '2026-11-30'), ({b}, {c}, '2026-11-01', '2026-11-30')");
        }

        Assert.Single(await ScopeAsync(b, Day));
        Assert.Empty(await ScopeAsync(c, Day));
    }

    // ---- AC 7: listing ----

    [DbFact]
    public async Task Ac7_Listing_returns_the_callers_own_delegations_and_an_admin_sees_all()
    {
        var admin = await SeedUserAsync("Admin");
        var pm = await SeedUserAsync("PM");
        var delegateId = await SeedUserAsync("PM");
        var otherPm = await SeedUserAsync("PM");
        var other = await SeedUserAsync("PM");
        var bystander = await SeedUserAsync("Consultant");
        await SeedApproverAsync(Guid.NewGuid(), pm);
        await SeedApproverAsync(Guid.NewGuid(), otherPm);
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var mine = await CreateDelegationAsync(client, pm, delegateId, "2026-11-01", "2026-11-30");
        var theirs = await CreateDelegationAsync(client, otherPm, other, "2026-12-01", "2026-12-31");
        await client.SendAsync(Send(HttpMethod.Delete, $"/api/v1/delegations/{theirs}", otherPm));

        async Task<List<Guid>> IdsFor(Guid caller)
        {
            var response = await client.SendAsync(Send(HttpMethod.Get, "/api/v1/delegations", caller));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await ReadJson(response)).EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        }

        Assert.Equal(mine, Assert.Single(await IdsFor(pm)));
        Assert.Equal(mine, Assert.Single(await IdsFor(delegateId)));
        Assert.Equal(theirs, Assert.Single(await IdsFor(other)));
        Assert.Empty(await IdsFor(bystander));
        Assert.Equal(new[] { mine, theirs }.Order(), (await IdsFor(admin)).Order());

        var item = (await ReadJson(await client.SendAsync(Send(HttpMethod.Get, "/api/v1/delegations", pm)))).EnumerateArray().Single();
        Assert.Equal(pm, item.GetProperty("delegatorId").GetGuid());
        Assert.Equal(delegateId, item.GetProperty("delegateId").GetGuid());
        Assert.Equal("2026-11-01", item.GetProperty("fromDate").GetString());
        Assert.Equal("2026-11-30", item.GetProperty("toDate").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("revokedAt").ValueKind);
        var revoked = (await ReadJson(await client.SendAsync(Send(HttpMethod.Get, "/api/v1/delegations", other)))).EnumerateArray().Single();
        Assert.Equal(JsonValueKind.String, revoked.GetProperty("revokedAt").ValueKind);
    }

    // ---- data model and wiring ----

    [DbFact]
    public async Task The_database_refuses_a_reversed_range_a_self_delegation_and_a_second_approver_row_and_the_reset_still_works()
    {
        var a = await SeedUserAsync("PM");
        var b = await SeedUserAsync("PM");
        var project = Guid.NewGuid();
        await SeedApproverAsync(project, a);

        async Task<string?> Violation(FormattableString sql)
        {
            await using var db = fixture.CreateContext();
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(sql));
            return error.ConstraintName;
        }

        Assert.Equal("ck_approval_delegation_dates", await Violation($"insert into approval_delegation (delegator_id, delegate_id, from_date, to_date) values ({a}, {b}, '2026-11-12', '2026-11-10')"));
        Assert.Equal("ck_approval_delegation_not_self", await Violation($"insert into approval_delegation (delegator_id, delegate_id, from_date, to_date) values ({a}, {a}, '2026-11-10', '2026-11-12')"));
        Assert.Equal("pk_project_approver", await Violation($"insert into project_approver (project_id, user_id) values ({project}, {a})"));

        await fixture.ResetAsync();

        Assert.Equal(0, await CountAsync("""select count(*)::int as "Value" from project_approver"""));
    }

    [DbFact]
    public void The_real_app_exposes_the_approver_and_delegation_routes()
    {
        using var factory = new WebApplicationFactory<Program>();

        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => (e.RoutePattern.RawText, Methods: string.Join(",", e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])))
            .ToList();

        Assert.Contains(("/api/v1/projects/{id:guid}/approvers/{userId:guid}", "PUT"), routes);
        Assert.Contains(("/api/v1/projects/{id:guid}/approvers/{userId:guid}", "DELETE"), routes);
        Assert.Contains(("/api/v1/delegations", "POST"), routes);
        Assert.Contains(("/api/v1/delegations", "GET"), routes);
        Assert.Contains(("/api/v1/delegations/{id:guid}", "DELETE"), routes);
    }
}
