using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zephyr.Api.Common;
using Zephyr.Api.Data;
using Zephyr.Api.Permissions;
using Zephyr.Api.Tests.Support;

namespace Zephyr.Api.Tests.Permissions;

/// <summary>
/// Stands in for Keycloak in the API tests (the e2e suite signs in through the real realm). <c>X-Test-User</c> is the
/// <c>app_user</c> id and each <c>X-Test-Role</c> value becomes a role claim like the real session principal has.
/// It is registered only by <see cref="PermissionWorld"/>, never by the application.
/// </summary>
public sealed class PermissionAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new("sub", user.ToString()), new(HttpCurrentUser.ClaimType, user.ToString()) };
        foreach (var role in Request.Headers["X-Test-Role"])
        {
            claims.Add(new Claim("role", role ?? ""));
        }

        var identity = new ClaimsIdentity(claims, "Test", "name", "role");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
    }
}

/// <summary>
/// The real application over the test database with one seeded user per role. Built once for the test class: the matrix is
/// seeded, the seven users exist, and the stored matrix and the API's <c>/api/v1</c> routes are read once.
/// </summary>
public sealed class PermissionWorld : IAsyncLifetime
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private WebApplicationFactory<Program>? factory;
    private HttpClient? client;

    public IReadOnlyDictionary<string, Guid> Users { get; private set; } = new Dictionary<string, Guid>();

    public IReadOnlyDictionary<(string Role, string Entity, string Action), string> Stored { get; private set; } =
        new Dictionary<(string Role, string Entity, string Action), string>();

    /// <summary>Every <c>METHOD pattern</c> the API maps under <c>/api/v1</c>.</summary>
    public IReadOnlyList<string> Routes { get; private set; } = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        client?.Dispose();
        factory?.Dispose();
        gate.Dispose();
        return Task.CompletedTask;
    }

    public async Task EnsureAsync(DbFixture fixture)
    {
        await gate.WaitAsync();
        try
        {
            if (client is not null)
            {
                return;
            }

            await fixture.ResetAsync();
            var users = new Dictionary<string, Guid>();
            await using (var db = fixture.CreateContext())
            {
                await PermissionSeeder.SeedAsync(db);
                foreach (var (key, _) in PermissionSeeder.Roles)
                {
                    var user = new AppUser
                    {
                        Id = Guid.NewGuid(),
                        Name = $"{key} fixture",
                        Email = $"{key.ToLowerInvariant()}@permissions.example.test",
                        Roles = [key]
                    };
                    db.AppUsers.Add(user);
                    users[key] = user.Id;
                }

                await db.SaveChangesAsync();
                var rows = await db.Database
                    .SqlQuery<string>($"select role_key || '|' || entity || '|' || action || '|' || scope as \"Value\" from role_permission")
                    .ToListAsync();
                Stored = rows.Select(r => r.Split('|')).ToDictionary(c => (c[0], c[1], c[2]), c => c[3]);
            }

            Users = users;
            factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                    services.AddAuthentication(options => options.DefaultScheme = "Test")
                        .AddScheme<AuthenticationSchemeOptions, PermissionAuthHandler>("Test", null)));
            var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
            Routes = endpoints
                .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/") == true)
                .SelectMany(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"], (e, method) => $"{method} {e.RoutePattern.RawText}")
                .ToList();
            client = factory.CreateClient();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The status code of <paramref name="url"/> called as the fixture user of <paramref name="role"/>; anonymous when null.</summary>
    public async Task<int> StatusAsync(string? role, string method, string url, string? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, method == "PATCH" ? "application/merge-patch+json" : "application/json");
        }

        if (role is not null)
        {
            request.Headers.Add("X-Test-User", Users[role].ToString());
            request.Headers.Add("X-Test-Role", role);
        }

        using var response = await client!.SendAsync(request);
        return (int)response.StatusCode;
    }
}

/// <summary>
/// Permission tests for the seven roles (US-146). One theory case per row of <c>permission-matrix.csv</c> (role x entity x
/// action): the stored matrix equals the row and, where the API has a route for that entity and action, the real route answers
/// as the row says (scope <c>none</c> is exactly 403; anything else passes the permission check, so it is not 401, 403 or 5xx).
/// Probes use ids and bodies that pass the permission check and then fail validation or find nothing, so they write nothing.
/// </summary>
[Collection("db")]
public class MatrixCoverageTests(DbFixture fixture, PermissionWorld world) : IClassFixture<PermissionWorld>
{
    private const string Missing = "00000000-0000-0000-0000-00000000dead";

    private sealed record Probe(string Method, string Pattern, string Url, string? Body = null);

    private static readonly Probe AccountDelete = new("DELETE", "/api/v1/accounts/{id:guid}", $"/api/v1/accounts/{Missing}");
    private static readonly Probe UserPatch = new("PATCH", "/api/v1/users/{id}", $"/api/v1/users/{Missing}", """{"active":true}""");
    private static readonly Probe RatesGet = new("GET", "/api/v1/exchange-rates", "/api/v1/exchange-rates");
    private static readonly Probe RatesPut = new("PUT", "/api/v1/exchange-rates/{currency}/{month}", "/api/v1/exchange-rates/ZZZ/2026-11", """{"rateToSek":"x"}""");
    private static readonly Probe OpportunityList = new("GET", "/api/v1/opportunities", "/api/v1/opportunities");
    private static readonly Probe OpportunityCreate = new("POST", "/api/v1/opportunities", "/api/v1/opportunities", "{}");
    private static readonly Probe OpportunityPatch = new("PATCH", "/api/v1/opportunities/{id:guid}", $"/api/v1/opportunities/{Missing}", "{}");

    /// <summary>
    /// The API probe of an (entity, action). The PUT routes create and update, so they serve both actions. The account create
    /// and update cells have no probe: the normal POST and PATCH routes come with FT-113 (the by-external-id PUT is not one).
    /// </summary>
    private static readonly Dictionary<(string Entity, string Action), Probe> Probes = new()
    {
        [("account", "delete")] = AccountDelete,
        [("app_user", "update")] = UserPatch,
        [("reference_data", "read")] = RatesGet,
        [("reference_data", "create")] = RatesPut,
        [("reference_data", "update")] = RatesPut,
        [("opportunity", "read")] = OpportunityList,
        [("opportunity", "create")] = OpportunityCreate,
        [("opportunity", "update")] = OpportunityPatch
    };

    /// <summary>
    /// Routes that are not a cell of the matrix, so they are not compared with it: each has a reason. They still count as routes
    /// (a new route without a probe or an entry here fails the route test) and an anonymous caller must get 401 on each.
    /// </summary>
    private static readonly (Probe Route, string Reason)[] OutsideMatrix =
    [
        (new Probe("PUT", "/api/v1/accounts/by-external-id/{system}/{value}", "/api/v1/accounts/by-external-id/dynamics/probe", "{}"), "integration route, see US-135"),
        (new Probe("GET", "/api/v1/me/permissions", "/api/v1/me/permissions"), "any signed-in user, about the caller (US-144)"),
        (new Probe("GET", "/api/v1/search", "/api/v1/search?q=probe"), "any role, scoped per entity type by the read cells of the matrix (US-147)"),
        (new Probe("PUT", "/api/v1/projects/{id:guid}/approvers/{userId:guid}", $"/api/v1/projects/{Missing}/approvers/{Missing}"), "Admin by role until the project register, US-145"),
        (new Probe("DELETE", "/api/v1/projects/{id:guid}/approvers/{userId:guid}", $"/api/v1/projects/{Missing}/approvers/{Missing}"), "Admin by role until the project register, US-145"),
        (new Probe("POST", "/api/v1/delegations", "/api/v1/delegations", "{}"), "any role, scoped to the caller (US-145)"),
        (new Probe("GET", "/api/v1/delegations", "/api/v1/delegations"), "any role, scoped to the caller (US-145)"),
        (new Probe("DELETE", "/api/v1/delegations/{id:guid}", $"/api/v1/delegations/{Missing}"), "any role, scoped to the caller (US-145)"),
        (new Probe("GET", "/api/v1/opportunities/{id:guid}", $"/api/v1/opportunities/{Missing}"), "the opportunity read cell again: a cell holds one probe, the list (US-156)"),
        (new Probe("DELETE", "/api/v1/opportunities/{id:guid}", $"/api/v1/opportunities/{Missing}"), "Admin by role until the matrix defines opportunity delete, as for accounts (US-156)"),
        (new Probe("GET", "/api/v1/stages", "/api/v1/stages"), "the reference_data read cell again: that cell holds one probe, the rate list (US-156)")
    ];

    /// <summary>
    /// Entities with no API route yet, so no probe (a deviation from "every entity has a probe", approved by the product owner).
    /// The route test fails when a route appears, so an entry cannot outlive the missing route: add a probe and delete the entry.
    /// </summary>
    private static readonly Dictionary<string, string> Pending = new()
    {
        ["contact"] = "contact feature story (not built yet)",
        ["lead"] = "lead feature story (not built yet)",
        ["meddpicc"] = "MEDDPICC story, US-157 (not built yet)",
        ["activity"] = "activity feature story (not built yet)",
        ["project"] = "project register, US-161",
        ["project_team_member"] = "project register, US-161",
        ["skill"] = "skills feature story (not built yet)",
        ["time_type"] = "time feature stories (not built yet)",
        ["time_entry"] = "time entry reads and approval, US-166 and US-170",
        ["service_request"] = "service request feature story (not built yet)",
        ["api_token"] = "API token feature story (not built yet)",
        ["audit_log"] = "audit log read story (not built yet)",
        ["retention_rule"] = "retention feature story (not built yet)",
        ["import_run"] = "import feature story (not built yet)",
        ["dsar_request"] = "DSAR feature story (not built yet)"
    };

    public static IEnumerable<object[]> MatrixCases() =>
        PermissionSeeder.Parse(PermissionSeeder.EmbeddedCsv()).Select(r => new object[] { r.Role, r.Entity, r.Action, r.Scope });

    private static string[] Sorted(IEnumerable<string> values) => values.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    private static string Describe(Probe probe) => $"{probe.Method} {probe.Pattern}";

    /// <summary>The rule of AC 1: scope none is exactly 403; any other scope passes the permission check.</summary>
    private static bool Matches(string scope, int status) => scope == "none" ? status == 403 : status is not (401 or 403) and < 500;

    // ---- AC 1: matrix coverage ----

    [Fact]
    public void Ac1_Every_role_entity_and_action_has_exactly_one_case_from_the_csv()
    {
        var cases = MatrixCases().Select(c => $"{c[0]},{c[1]},{c[2]}").ToList();
        var combinations = PermissionSeeder.Roles.SelectMany(
            role => PermissionSeeder.Entities.SelectMany(entity => PermissionSeeder.Actions.Select(action => $"{role.Key},{entity},{action}")));

        Assert.Equal(Sorted(combinations), Sorted(cases));
        Assert.Equal(665, cases.Count);
        var theory = typeof(MatrixCoverageTests).GetMethod(nameof(Ac1_Each_matrix_row_is_stored_and_enforced_by_the_api))!;
        Assert.Equal(nameof(MatrixCases), Assert.Single(theory.GetCustomAttributes<MemberDataAttribute>()).MemberName);
    }

    [Fact]
    public void Ac1_Every_entity_has_an_api_probe_or_is_listed_as_pending_and_never_both()
    {
        var probed = Probes.Keys.Select(k => k.Entity).Distinct().ToArray();

        Assert.Empty(probed.Intersect(Pending.Keys));
        Assert.Equal(Sorted(PermissionSeeder.Entities), Sorted(probed.Concat(Pending.Keys)));
        Assert.All(Probes.Keys, key => Assert.Contains(key.Action, PermissionSeeder.Actions));
        Assert.All(OutsideMatrix, outside => Assert.False(string.IsNullOrWhiteSpace(outside.Reason), Describe(outside.Route)));
    }

    [DbFact]
    public async Task Ac1_The_api_has_no_route_without_a_probe_or_a_reason_and_no_probe_without_a_route()
    {
        await world.EnsureAsync(fixture);
        var probed = Probes.Values.Select(Describe).Distinct().ToArray();
        var outside = OutsideMatrix.Select(o => Describe(o.Route)).ToArray();

        Assert.Empty(probed.Intersect(outside));
        // A new /api/v1 route fails here until its entity has a probe above (and is removed from Pending), or it is listed
        // in OutsideMatrix with the reason it is not a cell of the matrix.
        Assert.Equal(Sorted(probed.Concat(outside)), Sorted(world.Routes));
    }

    [DbTheory]
    [MemberData(nameof(MatrixCases))]
    public async Task Ac1_Each_matrix_row_is_stored_and_enforced_by_the_api(string role, string entity, string action, string scope)
    {
        await world.EnsureAsync(fixture);

        Assert.Equal(scope, world.Stored[(role, entity, action)]);
        if (Probes.TryGetValue((entity, action), out var probe))
        {
            var status = await world.StatusAsync(role, probe.Method, probe.Url, probe.Body);

            Assert.True(Matches(scope, status), $"{role} {action} {entity}: the matrix says {scope}, {probe.Method} {probe.Url} answered {status}");
        }
    }

    [DbFact]
    public async Task Ac1_An_anonymous_caller_is_refused_on_every_route()
    {
        await world.EnsureAsync(fixture);

        foreach (var probe in Probes.Values.Concat(OutsideMatrix.Select(o => o.Route)).Distinct())
        {
            Assert.Equal(401, await world.StatusAsync(null, probe.Method, probe.Url, probe.Body));
        }
    }
}
