using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Zephyr.Api.Auth;
using Zephyr.Api.Common;
using Zephyr.Api.Data;
using Zephyr.Api.Tests.Support;

namespace Zephyr.Api.Tests.Auth;

/// <summary>Settings of the Compose Keycloak, read from the environment (the values of .env plus ZEPHYR_TEST_KEYCLOAK).</summary>
public sealed record KeycloakEnv(string BaseUrl, string ClientSecret, string AdminSubject, string SalesPassword, string UserBPassword)
{
    public const string SalesSubject = "00000000-0000-4000-8000-000000000002";
    public const string UserBSubject = "00000000-0000-4000-8000-0000000000b1";

    public string Issuer(string realm) => $"{BaseUrl}/realms/{realm}";

    public static KeycloakEnv? Read()
    {
        static string? Get(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

        var url = Get("ZEPHYR_TEST_KEYCLOAK");
        var secret = Get("OIDC_CLIENT_SECRET");
        var admin = Get("ZEPHYR_BOOTSTRAP_ADMIN_SUBJECT");
        var sales = Get("KC_USER_SALESPERSON_PASSWORD");
        var userB = Get("KC_USER_B_PASSWORD");
        return url is null || secret is null || admin is null || sales is null || userB is null
            ? null
            : new KeycloakEnv(url.TrimEnd('/'), secret, admin, sales, userB);
    }
}

/// <summary>
/// A test that signs in against the Compose Keycloak (realms zephyr and zephyr-b) and needs PostgreSQL. Skipped unless
/// ZEPHYR_TEST_KEYCLOAK (e.g. http://localhost:8180) and the .env values it reads are set in the environment.
/// </summary>
public sealed class KeycloakFactAttribute : FactAttribute
{
    public KeycloakFactAttribute()
    {
        if (KeycloakEnv.Read() is null)
        {
            Skip = "Keycloak is not configured. Set ZEPHYR_TEST_KEYCLOAK and load .env (OIDC_CLIENT_SECRET, KC_USER_*, ZEPHYR_BOOTSTRAP_ADMIN_SUBJECT).";
        }
        else if (DbFixture.ServerConnectionString() is null)
        {
            Skip = "No PostgreSQL server configured. Set ZEPHYR_TEST_DB to run this test.";
        }
    }
}

[Collection("db")]
public class OidcSignInTests(DbFixture fixture) : IAsyncLifetime
{
    private const string Issuer = "https://idp.example/realms/test";
    private const string Things = "/api/v1/things";

    private static readonly AuthProvider Trusted = new()
    {
        Name = "test",
        Authority = Issuer,
        ClientId = "zephyr-web",
        ClientSecret = "secret",
        EmailTrusted = true
    };

    private static readonly AuthProvider Untrusted = new()
    {
        Name = "test",
        Authority = Issuer,
        ClientId = "zephyr-web",
        ClientSecret = "secret",
        EmailTrusted = false
    };

    private static readonly BootstrapAdmin NoBootstrap = new(null, null);

    private int writes;

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers: matching ----

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>These tests are about sign-in and CSRF, not the status recheck (SessionTests), so every session user counts as active.</summary>
    private sealed class AlwaysActive : IUserStatus
    {
        public Task<bool> IsActiveAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(true);

        public void Forget(Guid userId)
        {
        }
    }

    /// <summary>The claims the OIDC handler hands over after validating an id token (MapInboundClaims off).</summary>
    private static ClaimsPrincipal External(string subject, string? email = null, bool? verified = null, string issuer = Issuer)
    {
        var claims = new List<Claim> { new("iss", issuer), new("sub", subject), new("name", "Idp Name") };
        if (email is not null)
        {
            claims.Add(new Claim("email", email));
        }

        if (verified is not null)
        {
            claims.Add(new Claim("email_verified", verified.Value ? "true" : "false"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private async Task<AppUser> AddUser(string email, Action<AppUser>? configure = null)
    {
        var user = new AppUser { Name = "Pre Provisioned", Email = email, Roles = ["Salesperson"] };
        configure?.Invoke(user);
        await using var db = fixture.CreateContext();
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<AppUser> Reload(Guid id)
    {
        await using var db = fixture.CreateContext();
        return await db.AppUsers.AsNoTracking().IgnoreQueryFilters().SingleAsync(u => u.Id == id);
    }

    private async Task<int> CountUsers()
    {
        await using var db = fixture.CreateContext();
        return await db.AppUsers.IgnoreQueryFilters().CountAsync();
    }

    private async Task<AppUser?> Match(AuthProvider provider, BootstrapAdmin bootstrap, ClaimsPrincipal external, ListLogger? log = null)
    {
        await using var db = fixture.CreateContext();
        return await UserMatching.MatchAsync(db, provider, bootstrap, external, DateTime.UtcNow, log ?? new ListLogger());
    }

    // ---- AC 1: sign-in, no password anywhere ----

    [DbFact]
    public async Task Ac1_App_user_has_no_password_or_secret_column()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select column_name from information_schema.columns where table_name = 'app_user' order by ordinal_position", connection);
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        Assert.DoesNotContain(columns, c => Regex.IsMatch(c, "pass|secret|credential|hash|token", RegexOptions.IgnoreCase));
        foreach (var expected in new[] { "name", "email", "issuer", "subject", "home_country", "roles", "active", "last_login_at" })
        {
            Assert.Contains(expected, columns);
        }
    }

    [DbFact]
    public async Task Ac1_Email_is_unique_ignoring_case_and_issuer_subject_is_unique_when_set()
    {
        await AddUser("Pat@Example.com");
        await Assert.ThrowsAsync<DbUpdateException>(() => AddUser("pat@example.com"));

        await AddUser("a@example.com", u => (u.Issuer, u.Subject) = (Issuer, "s1"));
        await Assert.ThrowsAsync<DbUpdateException>(() => AddUser("b@example.com", u => (u.Issuer, u.Subject) = (Issuer, "s1")));

        // Several pre-provisioned users without a link are fine.
        await AddUser("c@example.com");
        await AddUser("d@example.com");
    }

    [DbFact]
    public async Task Ac1_A_linked_user_signs_in_by_issuer_and_subject_and_last_login_is_set()
    {
        var user = await AddUser("pat@example.com", u => (u.Issuer, u.Subject) = (Issuer, "sub-1"));

        // The email at the provider changed: the match is on issuer + subject, not on email.
        var matched = await Match(Untrusted, NoBootstrap, External("sub-1", "other@example.com", false));

        Assert.Equal(user.Id, matched?.Id);
        Assert.NotNull((await Reload(user.Id)).LastLoginAt);
    }

    [Fact]
    public void Ac1_Session_principal_carries_the_app_user_id_roles_and_issuer()
    {
        var user = new AppUser { Id = Guid.NewGuid(), Name = "Pat", Email = "pat@example.com", Issuer = Issuer, Roles = ["PM", "Finance"] };

        var principal = UserMatching.ToPrincipal(user);

        Assert.True(principal.Identity?.IsAuthenticated);
        Assert.Equal(user.Id.ToString(), principal.FindFirst("sub")?.Value);
        Assert.Equal(user.Id.ToString(), principal.FindFirst(HttpCurrentUser.ClaimType)?.Value);
        Assert.Equal(Issuer, principal.FindFirst("issuer")?.Value);
        Assert.Equal(new[] { "Finance", "PM" }, principal.FindAll("role").Select(c => c.Value).Order());
        Assert.True(principal.IsInRole("PM"));
    }

    [DbFact]
    public async Task Ac1_An_inactive_user_is_refused()
    {
        await AddUser("pat@example.com", u => (u.Issuer, u.Subject, u.Active) = (Issuer, "sub-1", false));

        Assert.Null(await Match(Trusted, NoBootstrap, External("sub-1", "pat@example.com", true)));
    }

    [DbFact]
    public async Task Ac1_First_sign_in_links_a_pre_provisioned_user_on_a_verified_trusted_email_ignoring_case()
    {
        var user = await AddUser("Pat@Example.com");

        var matched = await Match(Trusted, NoBootstrap, External("sub-1", "pat@example.COM", true));

        Assert.Equal(user.Id, matched?.Id);
        var saved = await Reload(user.Id);
        Assert.Equal((Issuer, "sub-1"), (saved.Issuer, saved.Subject));
        Assert.NotNull(saved.LastLoginAt);
    }

    // ---- AC 2: unverified or untrusted email ----

    [DbFact]
    public async Task Ac2_An_unverified_email_does_not_link_even_from_a_trusted_provider()
    {
        var user = await AddUser("pat@example.com");
        var log = new ListLogger();

        Assert.Null(await Match(Trusted, NoBootstrap, External("sub-1", "pat@example.com", false), log));
        Assert.Null(await Match(Trusted, NoBootstrap, External("sub-1", "pat@example.com"), log));

        var saved = await Reload(user.Id);
        Assert.Null(saved.Issuer);
        Assert.Null(saved.Subject);
        Assert.Null(saved.LastLoginAt);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not verified"));
    }

    [DbFact]
    public async Task Ac2_A_verified_email_from_a_provider_that_is_not_trusted_does_not_link()
    {
        var user = await AddUser("pat@example.com");

        Assert.Null(await Match(Untrusted, NoBootstrap, External("sub-1", "pat@example.com", true)));

        var saved = await Reload(user.Id);
        Assert.Null(saved.Issuer);
        Assert.Null(saved.Subject);
    }

    [DbFact]
    public async Task Ac2_An_email_already_linked_to_a_different_subject_is_refused()
    {
        var user = await AddUser("pat@example.com", u => (u.Issuer, u.Subject) = (Issuer, "sub-1"));

        Assert.Null(await Match(Trusted, NoBootstrap, External("sub-2", "pat@example.com", true)));

        Assert.Equal("sub-1", (await Reload(user.Id)).Subject);
    }

    [DbFact]
    public async Task Ac2_An_inactive_pre_provisioned_user_is_not_linked()
    {
        var user = await AddUser("pat@example.com", u => u.Active = false);

        Assert.Null(await Match(Trusted, NoBootstrap, External("sub-1", "pat@example.com", true)));

        Assert.Null((await Reload(user.Id)).Subject);
    }

    // ---- AC 3: no self sign-up ----

    [DbFact]
    public async Task Ac3_A_valid_user_that_is_not_in_zephyr_is_refused_and_no_row_is_created()
    {
        var log = new ListLogger();

        Assert.Null(await Match(Trusted, NoBootstrap, External("sub-9", "stranger@example.com", true), log));
        Assert.Null(await Match(Trusted, NoBootstrap, External("sub-9")));

        Assert.Equal(0, await CountUsers());
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("no Zephyr user"));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("stranger@example.com"));
    }

    // ---- AC 4: bootstrap admin ----

    [DbFact]
    public async Task Ac4_With_no_admin_the_bootstrap_subject_becomes_admin_and_the_event_is_logged_and_audited()
    {
        var log = new ListLogger();

        var admin = await Match(Untrusted, new BootstrapAdmin(Issuer, "boot-sub"), External("boot-sub", "boss@example.com"), log);

        Assert.NotNull(admin);
        var saved = await Reload(admin.Id);
        Assert.Equal(new[] { "Admin" }, saved.Roles);
        Assert.True(saved.Active);
        Assert.Equal("boss@example.com", saved.Email);
        Assert.Equal((Issuer, "boot-sub"), (saved.Issuer, saved.Subject));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("bootstrap admin used") && e.Message.Contains("boot-sub"));
        await using var db = fixture.CreateContext();
        var audit = await db.AuditLogs.SingleAsync(a => a.EntityId == admin.Id);
        Assert.Equal("bootstrap_admin_used", audit.Action);
        Assert.Contains("boot-sub", audit.Detail);
    }

    [DbFact]
    public async Task Ac4_Once_an_active_admin_exists_the_bootstrap_variables_are_ignored()
    {
        await AddUser("root@example.com", u => (u.Roles, u.Issuer, u.Subject) = (["Admin"], Issuer, "root-sub"));

        var result = await Match(Untrusted, new BootstrapAdmin(Issuer, "boot-sub"), External("boot-sub", "boss@example.com"));

        Assert.Null(result);
        Assert.Equal(1, await CountUsers());
    }

    [DbFact]
    public async Task Ac4_Only_the_configured_issuer_and_subject_can_bootstrap()
    {
        var bootstrap = new BootstrapAdmin(Issuer, "boot-sub");

        Assert.Null(await Match(Untrusted, bootstrap, External("someone-else", "x@example.com")));
        Assert.Null(await Match(Untrusted, bootstrap, External("boot-sub", "x@example.com", issuer: "https://other.example/realms/x")));
        Assert.Null(await Match(Untrusted, new BootstrapAdmin(null, null), External("boot-sub", "x@example.com")));

        Assert.Equal(0, await CountUsers());
    }

    [DbFact]
    public async Task Ac4_Reset_between_tests_works_while_app_user_and_audit_rows_exist()
    {
        // Regression: audit_log is append-only (Respawn ignores it); app_user has a foreign key to country.
        await Match(Untrusted, new BootstrapAdmin(Issuer, "boot-sub"), External("boot-sub", "boss@example.com"));

        await fixture.ResetAsync();

        Assert.Equal(0, await CountUsers());
        await using var db = fixture.CreateContext();
        Assert.NotEmpty(await db.AuditLogs.Where(a => a.Action == "bootstrap_admin_used").ToListAsync());
    }

    // ---- host: the real auth module over a small API ----

    private async Task<WebApplication> StartAsync(params (string Key, string Value)[] settings)
    {
        // Provider settings from a developer's loaded .env must not leak into the test.
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.StartsWith("ZEPHYR_AUTH__")))
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));
        builder.Services.AddApiConventions();
        builder.AddZephyrAuth();
        builder.Services.AddScoped<IUserStatus, AlwaysActive>();
        var app = builder.Build();
        app.UseApiConventions();
        app.UseZephyrAuth();
        app.MapGet(Things, () => Results.Ok());
        app.MapMethods(Things, ["POST", "PUT", "PATCH", "DELETE"], () =>
        {
            Interlocked.Increment(ref writes);
            return Results.Ok();
        });
        app.MapPost("/api/v1/public/ping", () => Results.Ok()).AllowAnonymous();
        await app.StartAsync();
        return app;
    }

    private static (string Key, string Value)[] Provider(int index, string name, string authority, bool emailTrusted = true) =>
    [
        ($"Auth:Providers:{index}:Name", name),
        ($"Auth:Providers:{index}:Authority", authority),
        ($"Auth:Providers:{index}:ClientId", "zephyr-web"),
        ($"Auth:Providers:{index}:ClientSecret", "secret"),
        ($"Auth:Providers:{index}:EmailTrusted", emailTrusted ? "true" : "false")
    ];

    /// <summary>A valid session cookie, made with the app's own cookie options, as a Cookie header value.</summary>
    private static string SessionCookie(WebApplication app, AppUser? user = null)
    {
        user ??= new AppUser { Id = Guid.NewGuid(), Name = "Pat", Email = "pat@example.com", Issuer = Issuer, Roles = ["Salesperson"] };
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthModule.CookieScheme);
        var now = DateTimeOffset.UtcNow;
        var properties = new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = now.AddHours(1) };
        SessionRules.Stamp(properties, now);
        var ticket = new AuthenticationTicket(UserMatching.ToPrincipal(user), properties, AuthModule.CookieScheme);
        return $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}";
    }

    private static HttpRequestMessage Request(string method, string url, string? cookie = null, bool csrf = false, string? origin = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        if (csrf)
        {
            request.Headers.Add(AuthModule.CsrfHeader, "1");
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return request;
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string? code = null)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        if (code is not null)
        {
            Assert.Equal(code, (await ReadJson(response)).GetProperty("code").GetString());
        }
    }

    // ---- AC 6: CSRF ----

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Ac6_A_cookie_write_without_the_csrf_header_is_403_and_nothing_changes(string method)
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().SendAsync(Request(method, Things, SessionCookie(app)));

        await AssertProblem(response, HttpStatusCode.Forbidden, ErrorCodes.CsrfRejected);
        Assert.Equal(0, writes);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Ac6_A_cookie_write_with_the_header_and_same_origin_succeeds(string method)
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var cookie = SessionCookie(app);

        var withOrigin = await client.SendAsync(Request(method, Things, cookie, csrf: true, origin: "http://localhost"));
        var withoutOrigin = await client.SendAsync(Request(method, Things, cookie, csrf: true));

        Assert.Equal(HttpStatusCode.OK, withOrigin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withoutOrigin.StatusCode);
        Assert.Equal(2, writes);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost:3000")]
    [InlineData("null")]
    public async Task Ac6_A_foreign_origin_is_403_even_with_the_header(string origin)
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().SendAsync(Request("POST", Things, SessionCookie(app), csrf: true, origin: origin));

        await AssertProblem(response, HttpStatusCode.Forbidden, ErrorCodes.CsrfRejected);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task Ac6_A_cross_site_fetch_or_a_wrong_header_value_is_403()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var crossSite = Request("POST", Things, SessionCookie(app), csrf: true);
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        var wrongValue = Request("POST", Things, SessionCookie(app));
        wrongValue.Headers.Add(AuthModule.CsrfHeader, "0");

        await AssertProblem(await client.SendAsync(crossSite), HttpStatusCode.Forbidden, ErrorCodes.CsrfRejected);
        await AssertProblem(await client.SendAsync(wrongValue), HttpStatusCode.Forbidden, ErrorCodes.CsrfRejected);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task Ac6_Get_is_unaffected_and_a_bearer_call_without_the_header_is_not_rejected()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var bearer = Request("POST", "/api/v1/public/ping");
        bearer.Headers.Add("Authorization", "Bearer some-api-token");

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request("GET", Things, SessionCookie(app)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(bearer)).StatusCode);
    }

    [Fact]
    public async Task Ac6_A_write_with_an_invalid_session_cookie_is_401_not_a_csrf_rejection()
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().SendAsync(Request("POST", Things, "zephyr.session=not-a-valid-ticket"));

        await AssertProblem(response, HttpStatusCode.Unauthorized);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task Ac6_Logout_needs_the_csrf_header_and_then_clears_the_cookie()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var cookie = SessionCookie(app);

        var rejected = await client.SendAsync(Request("POST", "/auth/logout", cookie));
        var accepted = await client.SendAsync(Request("POST", "/auth/logout", cookie, csrf: true));

        await AssertProblem(rejected, HttpStatusCode.Forbidden, ErrorCodes.CsrfRejected);
        // A valid session is renewed on every request (US-141), so the rejected call may carry a fresh cookie; it must not clear it.
        Assert.DoesNotContain(
            rejected.Headers.TryGetValues("Set-Cookie", out var rejectedCookies) ? rejectedCookies : [],
            v => v.StartsWith("zephyr.session=;"));
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Contains(accepted.Headers.GetValues("Set-Cookie"), v => v.StartsWith("zephyr.session=;"));
    }

    // ---- AC 7: no CORS ----

    [Fact]
    public async Task Ac7_A_cross_origin_preflight_gets_no_access_control_allow_headers()
    {
        await using var app = await StartAsync();
        var request = Request("OPTIONS", Things, origin: "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "x-zephyr-csrf");

        var response = await app.GetTestClient().SendAsync(request);

        var headers = response.Headers.Concat(response.Content.Headers).Select(h => h.Key);
        Assert.DoesNotContain(headers, h => h.StartsWith("Access-Control-Allow-", StringComparison.OrdinalIgnoreCase));
    }

    // ---- AC 8: anonymous set ----

    [Fact]
    public async Task Ac8_Anonymous_callers_reach_only_the_allow_list()
    {
        await using var app = await StartAsync(Provider(0, "zephyr", "http://localhost:8180/realms/zephyr"));
        var client = app.GetTestClient();

        await AssertProblem(await client.GetAsync(Things), HttpStatusCode.Unauthorized);
        await AssertProblem(await client.GetAsync("/auth/me"), HttpStatusCode.Unauthorized);
        await AssertProblem(await client.SendAsync(Request("POST", "/auth/logout")), HttpStatusCode.Unauthorized);
        await AssertProblem(await client.SendAsync(Request("POST", Things)), HttpStatusCode.Unauthorized);

        // Reachable without a session: the handlers answer (a 400/403 problem), the default-deny policy does not.
        await AssertProblem(await client.GetAsync("/auth/login?provider=nope"), HttpStatusCode.BadRequest);
        await AssertProblem(await client.GetAsync("/auth/denied"), HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request("POST", "/api/v1/public/ping"))).StatusCode);
    }

    [Fact]
    public async Task Ac3_The_denied_page_is_a_403_problem_with_a_plain_message()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var refused = await client.GetAsync("/auth/denied");
        var failed = await client.GetAsync("/auth/denied?reason=failed");

        await AssertProblem(refused, HttpStatusCode.Forbidden);
        Assert.Equal("Your account is not set up in Zephyr. Contact an administrator.", (await ReadJson(refused)).GetProperty("detail").GetString());
        await AssertProblem(failed, HttpStatusCode.Forbidden);
        Assert.NotEqual(AuthModule.NotProvisionedMessage, (await ReadJson(failed)).GetProperty("detail").GetString());
    }

    // ---- login endpoint and provider configuration ----

    [Theory]
    [InlineData("/")]
    [InlineData("/accounts")]
    [InlineData("/accounts?stage=won#top")]
    public void Return_url_accepts_local_paths(string value) => Assert.True(AuthModule.IsLocalPath(value));

    [Theory]
    [InlineData("")]
    [InlineData("accounts")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("https://evil.example")]
    [InlineData("http:/evil.example")]
    [InlineData("/ok\nSet-Cookie: x=y")]
    public void Return_url_refuses_anything_that_is_not_a_local_path(string value) => Assert.False(AuthModule.IsLocalPath(value));

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    public async Task Login_with_a_return_url_off_this_site_is_a_400_and_does_not_redirect(string returnUrl)
    {
        await using var app = await StartAsync(Provider(0, "zephyr", "http://localhost:8180/realms/zephyr"));

        var response = await app.GetTestClient().GetAsync($"/auth/login?provider=zephyr&returnUrl={Uri.EscapeDataString(returnUrl)}");

        await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal("returnUrl", (await ReadJson(response)).GetProperty("errors")[0].GetProperty("field").GetString());
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Login_with_an_unknown_provider_or_none_configured_is_a_400_naming_the_choices()
    {
        await using var configured = await StartAsync(Provider(0, "zephyr", "http://localhost:8180/realms/zephyr"));
        await using var empty = await StartAsync();

        var unknown = await configured.GetTestClient().GetAsync("/auth/login?provider=nope");
        var none = await empty.GetTestClient().GetAsync("/auth/login");

        await AssertProblem(unknown, HttpStatusCode.BadRequest);
        Assert.Contains("zephyr", (await ReadJson(unknown)).GetProperty("detail").GetString());
        await AssertProblem(none, HttpStatusCode.BadRequest);
        Assert.Contains("No sign-in provider", (await ReadJson(none)).GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("Bad Name", "http://localhost:8180/realms/x", "dup")]
    [InlineData("zephyr", "not a url", "dup")]
    [InlineData("zephyr", "", "dup")]
    public async Task A_misconfigured_provider_stops_the_API_from_starting(string name, string authority, string _)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(Provider(0, name, authority)));

        Assert.Contains("ZEPHYR_AUTH__PROVIDERS__0__", exception.Message);
    }

    [Fact]
    public async Task Two_providers_with_the_same_name_stop_the_API_from_starting()
    {
        var settings = Provider(0, "zephyr", "http://localhost:8180/realms/zephyr")
            .Concat(Provider(1, "zephyr", "http://localhost:8180/realms/zephyr-b")).ToArray();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(settings));

        Assert.Contains("more than one provider", exception.Message);
    }

    // ---- /auth/me ----

    [DbFact]
    public async Task Ac1_Me_returns_the_signed_in_user()
    {
        var user = await AddUser("pat@example.com", u => (u.Issuer, u.Subject, u.HomeCountry, u.Roles) = (Issuer, "sub-1", null, ["PM", "Finance"]));
        await using var app = await StartAsync();

        var response = await app.GetTestClient().SendAsync(Request("GET", "/auth/me", SessionCookie(app, user)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal(user.Id, body.GetProperty("id").GetGuid());
        Assert.Equal("Pre Provisioned", body.GetProperty("name").GetString());
        Assert.Equal("pat@example.com", body.GetProperty("email").GetString());
        Assert.Equal(new[] { "PM", "Finance" }, body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("homeCountry").ValueKind);
    }

    [DbFact]
    public async Task Ac1_Me_is_401_for_a_session_whose_user_is_inactive_or_gone()
    {
        var inactive = await AddUser("off@example.com", u => u.Active = false);
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        await AssertProblem(await client.SendAsync(Request("GET", "/auth/me", SessionCookie(app, inactive))), HttpStatusCode.Unauthorized);
        await AssertProblem(await client.SendAsync(Request("GET", "/auth/me", SessionCookie(app))), HttpStatusCode.Unauthorized);
    }

    // ---- Keycloak (skipped unless the Compose Keycloak is configured) ----

    private Task<WebApplication> StartWithKeycloakAsync(KeycloakEnv kc, bool emailTrusted = true, string? bootstrapSubject = null)
    {
        var settings = Provider(0, "zephyr", kc.Issuer("zephyr"), emailTrusted)
            .Concat(Provider(1, "zephyr-b", kc.Issuer("zephyr-b"), emailTrusted))
            .Select(s => s.Key.EndsWith(":ClientSecret") ? (s.Key, kc.ClientSecret) : s)
            .Concat([
                ("ZEPHYR_BOOTSTRAP_ADMIN_ISSUER", bootstrapSubject is null ? "" : kc.Issuer("zephyr")),
                ("ZEPHYR_BOOTSTRAP_ADMIN_SUBJECT", bootstrapSubject ?? "")
            ])
            .ToArray();
        return StartAsync(settings);
    }

    private static Dictionary<string, string> Cookies(HttpResponseMessage response, Dictionary<string, string> jar)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            foreach (var value in values)
            {
                var pair = value.Split(';')[0];
                var equals = pair.IndexOf('=');
                jar[pair[..equals]] = pair[(equals + 1)..];
            }
        }

        return jar;
    }

    private static string CookieHeader(Dictionary<string, string> jar) => string.Join("; ", jar.Select(c => $"{c.Key}={c.Value}"));

    /// <summary>
    /// Drives the browser flow: /auth/login, the Keycloak login form, then the callback on the API. The API client uses
    /// http://localhost:8080 so the redirect URI is one the Keycloak client allows. Returns the callback response and the cookies.
    /// </summary>
    private static async Task<(HttpResponseMessage Callback, Dictionary<string, string> Jar)> SignIn(
        WebApplication app, string provider, string username, string password)
    {
        var client = app.GetTestClient();
        client.BaseAddress = new Uri("http://localhost:8080");
        var login = await client.GetAsync($"/auth/login?provider={provider}&returnUrl=/welcome");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var jar = Cookies(login, new Dictionary<string, string>());

        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var page = await browser.GetAsync(login.Headers.Location);
        for (var hops = 0; hops < 5 && (int)page.StatusCode is >= 300 and < 400 && page.Headers.Location is { } next; hops++)
        {
            page = await browser.GetAsync(new Uri(page.RequestMessage!.RequestUri!, next));
        }

        var html = await page.Content.ReadAsStringAsync();
        var form = Regex.Match(html, "<form[^>]*id=\"kc-form-login\"[^>]*>");
        var action = Regex.Match(form.Value, "action=\"([^\"]+)\"");
        Assert.True(action.Success, "The Keycloak login form was not found.");
        var submit = await browser.PostAsync(
            WebUtility.HtmlDecode(action.Groups[1].Value),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = username, ["password"] = password, ["credentialId"] = "" }));
        Assert.Equal(HttpStatusCode.Found, submit.StatusCode);

        var callback = Request("GET", submit.Headers.Location!.PathAndQuery, CookieHeader(jar));
        var response = await client.SendAsync(callback);
        return (response, Cookies(response, jar));
    }

    private static async Task<HttpResponseMessage> Me(WebApplication app, Dictionary<string, string> jar) =>
        await app.GetTestClient().SendAsync(Request("GET", "/auth/me", CookieHeader(jar)));

    [KeycloakFact]
    public async Task Ac1_A_keycloak_user_signs_in_and_lands_in_zephyr()
    {
        var kc = KeycloakEnv.Read()!;
        var user = await AddUser("salesperson@example.com");
        await using var app = await StartWithKeycloakAsync(kc);

        var (callback, jar) = await SignIn(app, "zephyr", "salesperson@example.com", kc.SalesPassword);

        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        Assert.Equal("/welcome", callback.Headers.Location?.OriginalString);
        var me = await Me(app, jar);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("salesperson@example.com", (await ReadJson(me)).GetProperty("email").GetString());
        var saved = await Reload(user.Id);
        Assert.Equal((kc.Issuer("zephyr"), KeycloakEnv.SalesSubject), (saved.Issuer, saved.Subject));
        Assert.NotNull(saved.LastLoginAt);
    }

    [KeycloakFact]
    public async Task Ac2_Keycloak_with_email_not_trusted_does_not_link_and_refuses_access()
    {
        var kc = KeycloakEnv.Read()!;
        var user = await AddUser("salesperson@example.com");
        await using var app = await StartWithKeycloakAsync(kc, emailTrusted: false);

        var (callback, jar) = await SignIn(app, "zephyr", "salesperson@example.com", kc.SalesPassword);

        Assert.Equal("/auth/denied", callback.Headers.Location?.OriginalString);
        Assert.False(jar.ContainsKey("zephyr.session") && jar["zephyr.session"] != "");
        Assert.Null((await Reload(user.Id)).Subject);
    }

    [KeycloakFact]
    public async Task Ac3_A_keycloak_user_that_is_not_in_zephyr_is_refused_and_no_row_is_created()
    {
        var kc = KeycloakEnv.Read()!;
        await using var app = await StartWithKeycloakAsync(kc);

        var (callback, _) = await SignIn(app, "zephyr", "salesperson@example.com", kc.SalesPassword);

        Assert.Equal("/auth/denied", callback.Headers.Location?.OriginalString);
        await AssertProblem(await app.GetTestClient().GetAsync("/auth/denied"), HttpStatusCode.Forbidden);
        Assert.Equal(0, await CountUsers());
    }

    [KeycloakFact]
    public async Task Ac4_The_bootstrap_subject_signs_in_as_admin_when_no_admin_exists()
    {
        var kc = KeycloakEnv.Read()!;
        await using var app = await StartWithKeycloakAsync(kc, bootstrapSubject: kc.AdminSubject);

        var (callback, jar) = await SignIn(app, "zephyr", "admin@example.com", Environment.GetEnvironmentVariable("KC_USER_ADMIN_PASSWORD") ?? "");

        Assert.Equal("/welcome", callback.Headers.Location?.OriginalString);
        var body = await ReadJson(await Me(app, jar));
        Assert.Equal(new[] { "Admin" }, body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        await using var db = fixture.CreateContext();
        Assert.Single(await db.AuditLogs.Where(a => a.EntityId == body.GetProperty("id").GetGuid()).ToListAsync());
    }

    [KeycloakFact]
    public async Task Ac5_The_same_build_signs_in_through_the_second_realm_with_only_configuration_changed()
    {
        var kc = KeycloakEnv.Read()!;
        var user = await AddUser("user-b@example.com");
        await using var app = await StartWithKeycloakAsync(kc);

        var (callback, jar) = await SignIn(app, "zephyr-b", "user-b@example.com", kc.UserBPassword);

        Assert.Equal("/welcome", callback.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await Me(app, jar)).StatusCode);
        var saved = await Reload(user.Id);
        Assert.Equal((kc.Issuer("zephyr-b"), KeycloakEnv.UserBSubject), (saved.Issuer, saved.Subject));
    }
}
