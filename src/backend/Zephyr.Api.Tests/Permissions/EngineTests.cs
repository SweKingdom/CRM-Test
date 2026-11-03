using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Zephyr.Api.Auth;
using Zephyr.Api.Common;
using Zephyr.Api.Data;
using Zephyr.Api.Permissions;
using Zephyr.Api.Tests.Support;

namespace Zephyr.Api.Tests.Permissions;

/// <summary>
/// Stands in for a personal API token: <c>X-Test-User</c> is the token's owner (the <c>zephyr_user_id</c> claim) and
/// <c>X-Test-Claimed-Role</c> is a role the token itself claims, which the engine must ignore.
/// </summary>
public sealed class TokenAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(HttpCurrentUser.ClaimType, user.ToString()) };
        if (Request.Headers.TryGetValue("X-Test-Claimed-Role", out var role))
        {
            claims.Add(new Claim("role", role.ToString()));
        }

        var identity = new ClaimsIdentity(claims, "Test", "name", "role");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
    }
}

/// <summary>
/// The permission engine (US-144): scopes, filtering, single-record judgement, the permissions endpoint, role changes with a fake
/// clock, tokens, and the endpoint inventory. The HTTP tests run the engine behind test-only account routes, because the
/// account reads belong to a later story; they need PostgreSQL. The engine and inventory tests run without a database.
/// </summary>
[Collection("db")]
public class EngineTests(DbFixture fixture) : IAsyncLifetime
{
    private const string Accounts = "/api/v1/accounts";

    private static readonly string[] MeRoutes = ["/auth/me", "/auth/logout"];

    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero));

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers: the engine ----

    private static IReadOnlyList<PermissionRow> Matrix() => PermissionSeeder.Parse(PermissionSeeder.EmbeddedCsv());

    /// <summary>The account cells of one role: what it may read, create, update and delete, and how widely.</summary>
    private static PermissionRow[] Rows(string role, string read, string create = "none", string update = "none", string delete = "none") =>
        new[] { ("read", read), ("create", create), ("update", update), ("delete", delete) }
            .Select(cell => new PermissionRow(role, "account", cell.Item1, cell.Item2))
            .ToArray();

    private static PermissionService Engine(IEnumerable<PermissionRow> rows)
    {
        var engine = new PermissionService();
        engine.Load(rows);
        return engine;
    }

    private static UserAccess Access(string[] roles, string? country = null, Guid? id = null, params Guid[] team)
    {
        var self = id ?? Guid.NewGuid();
        return new UserAccess(self, roles, country, team.Length == 0 ? [self] : team);
    }

    private static Account Owned(Guid? owner, string name = "A", string? country = null) =>
        new() { Id = Guid.NewGuid(), Name = name, CreatedBy = owner, CountryCode = country };

    // ---- helpers: the HTTP host ----

    /// <summary>Test-only account routes that use the engine the way an entity feature will: filter, find, decide.</summary>
    private static void MapAccounts(WebApplication app)
    {
        var spec = new ListSpec<Account>("name", "updatedAt");

        app.MapGet(Accounts, async (HttpContext context, ZephyrDbContext db, IPermissionService permissions) =>
        {
            var user = await context.CurrentUserAsync();
            var query = ListQuery<Account>.Parse(context.Request.Query, spec);
            var page = await query.ToPageAsync(permissions.Filter(db.Accounts.AsNoTracking(), user, "account"), context.RequestAborted);
            return Results.Ok(page.Select(a => new { a.Id, a.Name }));
        }).RequirePermission("account", "read");

        app.MapGet(Accounts + "/search", async (string q, int? limit, HttpContext context, ZephyrDbContext db, IPermissionService permissions) =>
        {
            var user = await context.CurrentUserAsync();
            var found = permissions.Filter(db.Accounts.AsNoTracking(), user, "account").Where(a => EF.Functions.ILike(a.Name, "%" + q + "%"));
            var total = await found.CountAsync(context.RequestAborted);
            var items = await found.OrderBy(a => a.Name).Take(limit ?? 50).Select(a => a.Name).ToListAsync(context.RequestAborted);
            return Results.Ok(new { total, items });
        }).RequirePermission("account", "read");

        app.MapGet(Accounts + "/{id:guid}", async (Guid id, HttpContext context, ZephyrDbContext db, IPermissionService permissions) =>
        {
            var user = await context.CurrentUserAsync();
            var account = await permissions.FindAsync(db.Accounts.AsNoTracking(), user, "account", "read", a => a.Id == id, context.RequestAborted);
            return Results.Ok(new { account.Id, account.Name });
        }).RequirePermission("account", "read");

        app.MapPost(Accounts, async (HttpContext context, ZephyrDbContext db) =>
        {
            var account = new Account { Name = "Created" };
            db.Accounts.Add(account);
            await db.SaveChangesAsync(context.RequestAborted);
            return Results.Created($"{Accounts}/{account.Id}", new { account.Id });
        }).RequirePermission("account", "create");

        app.MapPatch(Accounts + "/{id:guid}", async (Guid id, HttpContext context, ZephyrDbContext db, IPermissionService permissions) =>
        {
            var user = await context.CurrentUserAsync();
            var account = await permissions.FindAsync(db.Accounts, user, "account", "update", a => a.Id == id, context.RequestAborted);
            account.Name += "!";
            await db.SaveChangesAsync(context.RequestAborted);
            return Results.Ok(new { account.Id, account.Name });
        }).RequirePermission("account", "update");

        app.MapDelete(Accounts + "/{id:guid}", async (Guid id, HttpContext context, ZephyrDbContext db, IPermissionService permissions) =>
        {
            var user = await context.CurrentUserAsync();
            var account = await permissions.FindAsync(db.Accounts, user, "account", "delete", a => a.Id == id, context.RequestAborted);
            db.Accounts.Remove(account);
            await db.SaveChangesAsync(context.RequestAborted);
            return Results.NoContent();
        }).RequirePermission("account", "delete");
    }

    /// <summary>
    /// The engine over <paramref name="rows"/> (the product matrix when null) behind the account routes and
    /// <c>/api/v1/me/permissions</c>. A token sign-in by default; <paramref name="cookie"/> uses the real session cookie.
    /// </summary>
    private async Task<WebApplication> StartAsync(IEnumerable<PermissionRow>? rows = null, bool cookie = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddApiConventions();
        if (cookie)
        {
            builder.AddZephyrAuth();
        }
        else
        {
            builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TokenAuthHandler>("Test", null);
        }

        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton<IPermissionService>(Engine(rows ?? Matrix()));
        var app = builder.Build();
        app.UseApiConventions();
        if (cookie)
        {
            app.UseZephyrAuth();
        }
        else
        {
            app.UseAuthentication();
        }

        app.MapPermissionEndpoints();
        MapAccounts(app);
        await app.StartAsync();
        return app;
    }

    private string CookieFor(WebApplication app, AppUser user)
    {
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthModule.CookieScheme);
        var now = clock.GetUtcNow();
        var properties = new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = now + options.ExpireTimeSpan };
        SessionRules.Stamp(properties, now);
        var ticket = new AuthenticationTicket(UserMatching.ToPrincipal(user), properties, AuthModule.CookieScheme);
        return $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}";
    }

    /// <summary>Calls the API as <paramref name="user"/> (anonymous when null): with a token, or with a session cookie.</summary>
    private Task<HttpResponseMessage> Call(
        WebApplication app, AppUser? user, string method, string url, bool cookie = false, string? claimedRole = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (user is not null && cookie)
        {
            request.Headers.Add("Cookie", CookieFor(app, user));
            request.Headers.Add(AuthModule.CsrfHeader, "1");
        }
        else if (user is not null)
        {
            request.Headers.Add("X-Test-User", user.Id.ToString());
            if (claimedRole is not null)
            {
                request.Headers.Add("X-Test-Claimed-Role", claimedRole);
            }
        }

        return app.GetTestClient().SendAsync(request);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static string[] Names(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).Order().ToArray();

    private static string[] Roles(JsonElement permissions) =>
        permissions.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToArray();

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadJson(response);
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.Equal(code, problem.GetProperty("code").GetString());
    }

    private async Task<AppUser> AddUser(string role, string? country = null, Guid? manager = null)
    {
        var user = new AppUser { Name = role, Email = $"{Guid.NewGuid():N}@example.test", Roles = [role], HomeCountry = country, ManagerId = manager };
        await using var db = fixture.CreateContext();
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>Saves an account owned by <paramref name="owner"/> (the audit interceptor sets <c>created_by</c> from the signed-in user).</summary>
    private async Task<Guid> AddAccount(string name, Guid owner, string? country = null)
    {
        await using var db = fixture.CreateContext(owner);
        var account = new Account { Name = name, CountryCode = country };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<string[]> StoredNames()
    {
        await using var db = fixture.CreateContext();
        return await db.Accounts.AsNoTracking().OrderBy(a => a.Name).Select(a => a.Name).ToArrayAsync();
    }

    // ---- AC 1: own scope ----

    [DbFact]
    public async Task Ac1_Own_scope_lists_only_the_callers_rows()
    {
        var me = await AddUser("Salesperson");
        var other = await AddUser("Salesperson");
        await AddAccount("mine 1", me.Id);
        await AddAccount("mine 2", me.Id);
        await AddAccount("theirs", other.Id);
        await using var app = await StartAsync(Rows("Salesperson", read: "own"));

        var response = await Call(app, me, "GET", Accounts);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "mine 1", "mine 2" }, Names(await ReadJson(response)));
    }

    [DbFact]
    public async Task Ac1_Another_users_record_is_404_to_a_caller_without_read_on_it_and_their_own_is_200()
    {
        var me = await AddUser("Salesperson");
        var other = await AddUser("Salesperson");
        var mine = await AddAccount("mine", me.Id);
        var theirs = await AddAccount("theirs", other.Id);
        await using var app = await StartAsync(Rows("Salesperson", read: "own", update: "own"));

        Assert.Equal(HttpStatusCode.OK, (await Call(app, me, "GET", $"{Accounts}/{mine}")).StatusCode);
        await AssertProblem(await Call(app, me, "GET", $"{Accounts}/{theirs}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblem(await Call(app, me, "PATCH", $"{Accounts}/{theirs}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblem(await Call(app, me, "GET", $"{Accounts}/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(new[] { "mine", "theirs" }, await StoredNames());
    }

    [DbFact]
    public async Task Ac1_A_visible_record_the_caller_may_not_change_is_403_and_nothing_changes()
    {
        var me = await AddUser("Salesperson");
        var other = await AddUser("Salesperson");
        var mine = await AddAccount("mine", me.Id);
        var theirs = await AddAccount("theirs", other.Id);
        await using var app = await StartAsync(Rows("Salesperson", read: "org", create: "own", update: "own"));

        await AssertProblem(await Call(app, me, "PATCH", $"{Accounts}/{theirs}"), HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal(HttpStatusCode.OK, (await Call(app, me, "PATCH", $"{Accounts}/{mine}")).StatusCode);
        // No delete scope at all: 403 for any record, theirs or mine.
        await AssertProblem(await Call(app, me, "DELETE", $"{Accounts}/{theirs}"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblem(await Call(app, me, "DELETE", $"{Accounts}/{mine}"), HttpStatusCode.Forbidden, "FORBIDDEN");

        Assert.Equal(new[] { "mine!", "theirs" }, await StoredNames());
    }

    // ---- AC 2: country scope ----

    [DbFact]
    public async Task Ac2_Country_scope_lists_only_records_in_the_users_country_and_an_out_of_scope_id_is_404()
    {
        var me = await AddUser("Salesperson", "SE");
        var owner = await AddUser("Salesperson");
        await AddAccount("se 1", owner.Id, "SE");
        await AddAccount("se 2", owner.Id, "SE");
        var norway = await AddAccount("no 1", owner.Id, "NO");
        var nowhere = await AddAccount("no country", owner.Id);
        await using var app = await StartAsync(Rows("Salesperson", read: "country"));

        var list = await Call(app, me, "GET", Accounts);

        Assert.Equal(new[] { "se 1", "se 2" }, Names(await ReadJson(list)));
        await AssertProblem(await Call(app, me, "GET", $"{Accounts}/{norway}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblem(await Call(app, me, "GET", $"{Accounts}/{nowhere}"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [DbFact]
    public async Task Ac2_A_user_without_a_home_country_has_no_country_scope_even_for_records_without_a_country()
    {
        var me = await AddUser("Salesperson");
        await AddAccount("no country", me.Id);
        await AddAccount("se", me.Id, "SE");
        await using var app = await StartAsync(Rows("Salesperson", read: "country"));

        var list = await Call(app, me, "GET", Accounts);

        Assert.Empty(Names(await ReadJson(list)));
    }

    [DbFact]
    public async Task Team_scope_lists_the_rows_of_the_manager_and_everyone_who_reports_to_that_manager()
    {
        var manager = await AddUser("PM");
        var first = await AddUser("PM", manager: manager.Id);
        var second = await AddUser("PM", manager: manager.Id);
        var outsider = await AddUser("PM");
        await AddAccount("by manager", manager.Id);
        await AddAccount("by first", first.Id);
        await AddAccount("by second", second.Id);
        await AddAccount("by outsider", outsider.Id);
        await using var app = await StartAsync(Rows("PM", read: "team"));

        Assert.Equal(new[] { "by first", "by manager", "by second" }, Names(await ReadJson(await Call(app, first, "GET", Accounts))));
        Assert.Equal(new[] { "by outsider" }, Names(await ReadJson(await Call(app, outsider, "GET", Accounts))));
    }

    // ---- AC 3: organisation scope ----

    [DbFact]
    public async Task Ac3_Management_reads_every_record_and_every_write_is_403_with_a_problem_body()
    {
        var management = await AddUser("Management");
        var owner = await AddUser("Salesperson");
        var first = await AddAccount("first", owner.Id, "SE");
        await AddAccount("second", owner.Id, "NO");
        await AddAccount("third", management.Id);
        await using var app = await StartAsync();

        var list = await Call(app, management, "GET", Accounts);
        Assert.Equal(new[] { "first", "second", "third" }, Names(await ReadJson(list)));
        Assert.Equal(HttpStatusCode.OK, (await Call(app, management, "GET", $"{Accounts}/{first}")).StatusCode);

        await AssertProblem(await Call(app, management, "POST", Accounts), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblem(await Call(app, management, "PATCH", $"{Accounts}/{first}"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblem(await Call(app, management, "DELETE", $"{Accounts}/{first}"), HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal(new[] { "first", "second", "third" }, await StoredNames());
    }

    // ---- AC 4: UI parity ----

    [DbFact]
    public async Task Ac4_The_permissions_endpoint_returns_roles_permissions_and_routes_and_is_never_cached()
    {
        var pm = await AddUser("PM");
        await using var app = await StartAsync();

        var response = await Call(app, pm, "GET", "/api/v1/me/permissions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        var body = await ReadJson(response);
        Assert.Equal(new[] { "PM" }, Roles(body));
        var permissions = body.GetProperty("permissions").EnumerateArray()
            .Select(p => $"{p.GetProperty("entity").GetString()},{p.GetProperty("action").GetString()},{p.GetProperty("scope").GetString()}")
            .Order()
            .ToArray();
        var expected = Matrix().Where(r => r.Role == "PM" && r.Scope != "none").Select(r => $"{r.Entity},{r.Action},{r.Scope}").Order().ToArray();
        Assert.Equal(expected, permissions);
        Assert.Contains("time_entry,approve,team", permissions);
        var routes = body.GetProperty("routes").EnumerateArray().Select(r => r.GetString()!).ToList();
        Assert.Contains("/approvals", routes);
        Assert.Contains("/projects", routes);
        Assert.DoesNotContain("/admin/users", routes);
        Assert.DoesNotContain("/leads", routes);
    }

    [DbFact]
    public async Task Ac4_What_the_UI_is_offered_is_what_the_API_allows()
    {
        await using var app = await StartAsync();

        foreach (var role in new[] { "Salesperson", "Admin", "Management" })
        {
            var user = await AddUser(role);
            var body = await ReadJson(await Call(app, user, "GET", "/api/v1/me/permissions"));
            var offered = body.GetProperty("permissions").EnumerateArray()
                .Any(p => p.GetProperty("entity").GetString() == "account" && p.GetProperty("action").GetString() == "create");

            var created = await Call(app, user, "POST", Accounts);

            Assert.Equal(offered ? HttpStatusCode.Created : HttpStatusCode.Forbidden, created.StatusCode);
            Assert.Equal(role == "Salesperson", offered);
        }
    }

    [DbFact]
    public async Task Ac4_The_permissions_endpoint_needs_a_signed_in_user()
    {
        await using var app = await StartAsync(cookie: true);

        var response = await Call(app, null, "GET", "/api/v1/me/permissions", cookie: true);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // ---- AC 5: admin changes ----

    [DbTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ac5_A_role_change_takes_effect_within_300_seconds_for_token_and_cookie_sessions(bool cookie)
    {
        var user = await AddUser("Salesperson");
        await using var app = await StartAsync(cookie: cookie);
        Assert.Equal(HttpStatusCode.Created, (await Call(app, user, "POST", Accounts, cookie)).StatusCode);

        await using (var db = fixture.CreateContext())
        {
            var row = await db.AppUsers.SingleAsync(u => u.Id == user.Id);
            row.Roles = ["Management"];
            await db.SaveChangesAsync();
        }

        // Inside the window the cached roles still apply.
        clock.Advance(TimeSpan.FromSeconds(299));
        Assert.Equal(HttpStatusCode.Created, (await Call(app, user, "POST", Accounts, cookie)).StatusCode);

        // Just after it the status is read again: Management may not create.
        clock.Advance(TimeSpan.FromSeconds(2));
        await AssertProblem(await Call(app, user, "POST", Accounts, cookie), HttpStatusCode.Forbidden, "FORBIDDEN");
        var body = await ReadJson(await Call(app, user, "GET", "/api/v1/me/permissions", cookie));
        Assert.Equal(new[] { "Management" }, Roles(body));
    }

    // ---- AC 6: search ----

    [DbFact]
    public async Task Ac6_Search_filters_by_scope_before_the_limit_and_counts_only_visible_rows()
    {
        var me = await AddUser("Salesperson");
        var other = await AddUser("Salesperson");
        // Their names sort first: a filter applied after the limit would return nothing of mine.
        foreach (var n in new[] { "1", "2", "3", "4" })
        {
            await AddAccount("Acme " + n, other.Id);
        }

        foreach (var n in new[] { "7", "8", "9" })
        {
            await AddAccount("Acme " + n, me.Id);
        }

        await using var app = await StartAsync(Rows("Salesperson", read: "own"));

        var response = await Call(app, me, "GET", $"{Accounts}/search?q=Acme&limit=2");

        var body = await ReadJson(response);
        Assert.Equal(3, body.GetProperty("total").GetInt32());
        Assert.Equal(new[] { "Acme 7", "Acme 8" }, body.GetProperty("items").EnumerateArray().Select(i => i.GetString()!));
    }

    [DbFact]
    public async Task Ac6_Paged_lists_walk_only_the_visible_rows_and_a_caller_without_read_gets_403()
    {
        var me = await AddUser("Salesperson");
        var other = await AddUser("Salesperson");
        foreach (var n in new[] { "a", "b", "c" })
        {
            await AddAccount("mine " + n, me.Id);
            await AddAccount("theirs " + n, other.Id);
        }

        await using var app = await StartAsync(Rows("Salesperson", read: "own"));

        var first = await ReadJson(await Call(app, me, "GET", $"{Accounts}?limit=2"));
        var cursor = first.GetProperty("nextCursor").GetString();
        var second = await ReadJson(await Call(app, me, "GET", $"{Accounts}?limit=2&cursor={cursor}"));

        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        Assert.Equal(1, second.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        Assert.Equal(new[] { "mine a", "mine b", "mine c" }, Names(first).Concat(Names(second)).Order());

        await using var closed = await StartAsync(Rows("Salesperson", read: "none"));
        await AssertProblem(await Call(closed, me, "GET", $"{Accounts}/search?q=mine"), HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    // ---- AC 7: tokens ----

    [DbFact]
    public async Task Ac7_A_token_has_exactly_its_owners_permissions_whatever_roles_it_claims()
    {
        var owner = await AddUser("Consultant");
        await using var app = await StartAsync();

        var plain = await Call(app, owner, "GET", "/api/v1/me/permissions");
        var claimingAdmin = await Call(app, owner, "GET", "/api/v1/me/permissions", claimedRole: "Admin");

        Assert.Equal(await plain.Content.ReadAsStringAsync(), await claimingAdmin.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "Consultant" }, Roles(await ReadJson(claimingAdmin)));
        await AssertProblem(await Call(app, owner, "POST", Accounts, claimedRole: "Admin"), HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Empty(await StoredNames());
    }

    // ---- the engine without a database ----

    [Fact]
    public void Scope_is_the_widest_across_the_roles_and_none_when_nothing_is_granted()
    {
        var engine = Engine(Matrix());

        Assert.Equal("country", engine.Scope(Access(["Salesperson"]), "lead", "read"));
        Assert.Equal("org", engine.Scope(Access(["Salesperson", "Management"]), "lead", "read"));
        Assert.Equal("org", engine.Scope(Access(["Management", "Salesperson"]), "lead", "read"));
        Assert.Equal("none", engine.Scope(Access(["Consultant"]), "lead", "read"));
        Assert.Equal("none", engine.Scope(Access(["Nobody"]), "lead", "read"));
        Assert.Equal("none", engine.Scope(Access([]), "account", "read"));
        Assert.Equal("none", engine.Scope(Access(["Admin"]), "account", "create"));
        Assert.Equal("org", engine.Scope(Access(["Admin", "Consultant"]), "app_user", "update"));
    }

    [Fact]
    public async Task Scope_before_the_matrix_is_loaded_fails_loudly_and_a_loaded_engine_needs_no_database()
    {
        var empty = new PermissionService();

        Assert.Throws<InvalidOperationException>(() => empty.Scope(Access(["Admin"]), "account", "read"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => empty.ReloadAsync());
        await Engine(Matrix()).EnsureLoadedAsync();
    }

    [Fact]
    public void Filter_keeps_only_the_rows_each_scope_allows()
    {
        var me = Guid.NewGuid();
        var colleague = Guid.NewGuid();
        var accounts = new List<Account> { Owned(me, "mine", "SE"), Owned(colleague, "colleague", "NO"), Owned(Guid.NewGuid(), "stranger", "SE"), Owned(null, "unowned") }.AsQueryable();
        var user = Access(["Salesperson"], "SE", me, me, colleague);

        string[] Visible(string scope) =>
            Engine(Rows("Salesperson", read: scope)).Filter(accounts, user, "account").Select(a => a.Name).Order().ToArray();

        Assert.Equal(new[] { "mine" }, Visible("own"));
        Assert.Equal(new[] { "colleague", "mine" }, Visible("team"));
        Assert.Equal(new[] { "mine", "stranger" }, Visible("country"));
        Assert.Equal(new[] { "colleague", "mine", "stranger", "unowned" }, Visible("org"));
        Assert.Empty(Visible("none"));
    }

    [Fact]
    public void Can_judges_one_record_with_the_same_predicate_as_Filter()
    {
        var me = Guid.NewGuid();
        var user = Access(["Salesperson"], "SE", me);
        var engine = Engine(Rows("Salesperson", read: "org", create: "own", update: "own", delete: "country"));

        Assert.True(engine.Can(user, "account", "read", Owned(Guid.NewGuid())));
        Assert.True(engine.Can(user, "account", "update", Owned(me)));
        Assert.False(engine.Can(user, "account", "update", Owned(Guid.NewGuid())));
        Assert.True(engine.Can(user, "account", "delete", Owned(Guid.NewGuid(), country: "SE")));
        Assert.False(engine.Can(user, "account", "delete", Owned(me, country: "NO")));
        Assert.False(engine.Can(user, "account", "approve", Owned(me)));
    }

    [Fact]
    public void Nobody_approves_their_own_work_even_with_an_organisation_scope()
    {
        var me = Guid.NewGuid();
        var engine = Engine([new PermissionRow("PM", "account", "approve", "org")]);
        var user = Access(["PM"], id: me);

        Assert.False(engine.Can(user, "account", "approve", Owned(me)));
        Assert.True(engine.Can(user, "account", "approve", Owned(Guid.NewGuid())));
    }

    [Fact]
    public void An_entity_with_an_own_scope_but_no_declared_columns_fails_loudly()
    {
        var engine = Engine([new PermissionRow("Salesperson", "contact", "read", "own")]);
        var accounts = new List<Account> { Owned(null) }.AsQueryable();

        var error = Assert.Throws<InvalidOperationException>(() => engine.Filter(accounts, Access(["Salesperson"]), "contact"));

        Assert.Contains("contact", error.Message);
    }

    [Fact]
    public void Grants_lists_every_cell_with_a_scope_other_than_none()
    {
        var engine = Engine(Matrix());

        var grants = engine.Grants(Access(["Management"]));

        var expected = Matrix().Where(r => r.Role == "Management" && r.Scope != "none").Select(r => new PermissionGrant(r.Entity, r.Action, r.Scope));
        Assert.Equal(expected.OrderBy(g => g.Entity).ThenBy(g => g.Action), grants.OrderBy(g => g.Entity).ThenBy(g => g.Action));
        Assert.Contains(new PermissionGrant("time_entry", "create", "own"), grants);
        Assert.DoesNotContain(grants, g => g.Scope == "none");
    }

    // ---- AC 8: default deny and the anonymous allow-list ----

    /// <summary>The route table of the real API (<c>Program</c>): built, not started, so no database is needed.</summary>
    private static List<RouteEndpoint> RealEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddApiConventions();
        builder.AddZephyrAuth();
        builder.Services.AddHealthChecks();
        var app = builder.Build();
        app.UseApiConventions();
        app.UseZephyrAuth();
        app.MapZephyrEndpoints();
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
    }

    private static string Describe(RouteEndpoint endpoint) =>
        $"{string.Join(",", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"])} {endpoint.RoutePattern.RawText}";

    private static bool IsAnonymous(RouteEndpoint endpoint) => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;

    private static bool IsMe(RouteEndpoint endpoint) => endpoint.Metadata.GetMetadata<MeEndpointMarker>() is not null;

    /// <summary>
    /// The endpoints that break default deny: an anonymous endpoint that is not on the allow-list, or any other endpoint that
    /// declares no permission, role requirement or <c>me</c> marker (or that sits on an anonymous path without being anonymous).
    /// </summary>
    private static List<string> Undeclared(IEnumerable<RouteEndpoint> endpoints) =>
        endpoints.Where(e =>
        {
            var listed = AnonymousAllowList.Allows(e.RoutePattern.RawText ?? "", e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []);
            var declared = e.Metadata.GetMetadata<PermissionRequirement>() is not null
                || e.Metadata.GetMetadata<RoleRequirement>() is not null
                || IsMe(e);
            return IsAnonymous(e) ? !listed : listed || !declared;
        }).Select(Describe).ToList();

    [Fact]
    public void Ac8_Every_endpoint_of_the_real_API_declares_a_permission_or_is_on_the_allow_list()
    {
        var endpoints = RealEndpoints();

        Assert.Empty(Undeclared(endpoints));
        var described = endpoints.Select(Describe).ToList();
        Assert.Contains("PATCH /api/v1/users/{id}", described);
        Assert.Contains("GET /api/v1/me/permissions", described);
        Assert.Contains("GET /api/v1/exchange-rates", described);
        Assert.Contains("PUT /api/v1/accounts/by-external-id/{system}/{value}", described);
        Assert.Contains("DELETE /api/v1/accounts/{id:guid}", described);
    }

    [Fact]
    public void Ac8_Only_the_allow_list_is_anonymous_and_only_auth_me_logout_and_the_me_routes_are_marked_me()
    {
        var endpoints = RealEndpoints();

        Assert.Equal(
            new[] { "/auth/denied", "/auth/login", "/health" },
            endpoints.Where(IsAnonymous).Select(e => e.RoutePattern.RawText!).Order().ToArray());
        var me = endpoints.Where(IsMe).Select(e => e.RoutePattern.RawText!).ToList();
        Assert.All(me, route => Assert.True(MeRoutes.Contains(route) || route.StartsWith("/api/v1/me/", StringComparison.Ordinal), route));
        Assert.Contains("/api/v1/me/permissions", me);
        Assert.Contains("/auth/me", me);
        Assert.Contains("/auth/logout", me);
    }

    [Fact]
    public void Ac8_The_allow_list_is_exactly_the_agreed_set_and_matches_by_method_and_prefix()
    {
        Assert.Equal(
            new[] { "GET /auth/denied", "GET /auth/login", "GET /health", "POST /api/v1/public/*", "/signin-oidc-*" }.Order(),
            AnonymousAllowList.Entries.Order());

        Assert.True(AnonymousAllowList.Allows("/health", ["GET"]));
        Assert.True(AnonymousAllowList.Allows("/health", []));
        Assert.False(AnonymousAllowList.Allows("/health", ["POST"]));
        Assert.True(AnonymousAllowList.Allows("/signin-oidc-keycloak", ["GET", "POST"]));
        Assert.True(AnonymousAllowList.Allows("/api/v1/public/contact", ["POST"]));
        Assert.False(AnonymousAllowList.Allows("/api/v1/public/contact", ["GET"]));
        Assert.False(AnonymousAllowList.Allows("/api/v1/accounts", ["GET"]));
        Assert.False(AnonymousAllowList.Allows("/health/deep", ["GET"]));
    }

    [Fact]
    public void Ac8_The_inventory_flags_an_undeclared_endpoint_and_an_anonymous_one_that_is_not_listed()
    {
        var app = WebApplication.CreateBuilder().Build();
        app.MapGet("/api/v1/undeclared", () => Results.Ok());
        app.MapGet("/api/v1/open", () => Results.Ok()).AllowAnonymous();
        app.MapGet("/api/v1/guarded", () => Results.Ok()).RequirePermission("account", "read");
        app.MapGet("/api/v1/by-role", () => Results.Ok()).RequireRoles("Admin");
        app.MapGet("/api/v1/me/anything", () => Results.Ok()).AsMeEndpoint();
        app.MapGet("/health", () => Results.Ok()).AllowAnonymous();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();

        var flagged = Undeclared(endpoints);

        Assert.Equal(new[] { "GET /api/v1/open", "GET /api/v1/undeclared" }, flagged.Order());
    }
}
