using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Zephyr.Api.Auth;
using Zephyr.Api.Common;
using Zephyr.Api.Data;
using Zephyr.Api.Tests.Support;

namespace Zephyr.Api.Tests.Auth;

/// <summary>
/// Session limits and the deactivation recheck (US-141). Time is a <see cref="FakeTimeProvider"/> shared by the cookie
/// handler, the session rules and the status cache: no test waits (AC 4).
/// </summary>
[Collection("db")]
public class SessionTests(DbFixture fixture) : IAsyncLifetime
{
    private const string Things = "/api/v1/things";
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero));

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers ----

    private sealed class AlwaysActive : IUserStatus
    {
        public Task<bool> IsActiveAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(true);

        public void Forget(Guid userId)
        {
        }
    }

    /// <summary>The real status cache over a scripted lookup, so the recheck logic is tested without a database.</summary>
    private UserStatus ScriptedStatus(Func<bool> active, List<Guid>? reads = null, int recheckSeconds = 300) =>
        new(
            new MemoryCache(new MemoryCacheOptions()),
            clock,
            new SessionSettings(TimeSpan.FromHours(4), TimeSpan.FromHours(12), TimeSpan.FromSeconds(recheckSeconds)),
            (id, _) =>
            {
                reads?.Add(id);
                return Task.FromResult(active());
            });

    private async Task<WebApplication> StartAsync(IUserStatus? status = null, params (string Key, string Value)[] settings)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));
        builder.Services.AddApiConventions();
        builder.AddZephyrAuth();
        builder.Services.AddSingleton<TimeProvider>(clock);
        if (status is not null)
        {
            builder.Services.AddScoped(_ => status);
        }

        var app = builder.Build();
        app.UseApiConventions();
        app.UseZephyrAuth();
        app.MapUsersEndpoints();
        app.MapGet(Things, () => Results.Ok());
        await app.StartAsync();
        return app;
    }

    private async Task<AppUser> AddUser(string email, string[]? roles = null, bool active = true)
    {
        var user = new AppUser { Name = "Pat", Email = email, Roles = roles ?? ["Salesperson"], Active = active };
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

    private async Task SetActive(Guid id, bool active)
    {
        await using var db = fixture.CreateContext();
        var user = await db.AppUsers.SingleAsync(u => u.Id == id);
        user.Active = active;
        await db.SaveChangesAsync();
    }

    /// <summary>A browser session: the cookie signed in at the current fake time. It keeps the renewed cookie the API sends back.</summary>
    private sealed class Session(WebApplication app, string cookie)
    {
        private const string CookiePrefix = "zephyr.session=";

        public string Cookie { get; private set; } = cookie;

        public async Task<HttpResponseMessage> Send(string method, string url, string? body = null, string? ifMatch = null)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), url);
            request.Headers.Add("Cookie", Cookie);
            request.Headers.Add(AuthModule.CsrfHeader, "1");
            if (ifMatch is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            }

            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/merge-patch+json");
            }

            var response = await app.GetTestClient().SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
            {
                // A renewed cookie replaces the old one; the empty cookie of a sign-out is ignored.
                var renewed = values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith(CookiePrefix) && v != CookiePrefix);
                Cookie = renewed ?? Cookie;
            }

            return response;
        }

        public Task<HttpResponseMessage> Get(string url) => Send("GET", url);
    }

    private Session SignedIn(WebApplication app, AppUser? user = null, bool stamped = true)
    {
        user ??= new AppUser { Id = Guid.NewGuid(), Name = "Pat", Email = "pat@example.com", Roles = ["Salesperson"] };
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthModule.CookieScheme);
        var now = clock.GetUtcNow();
        var properties = new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = now + options.ExpireTimeSpan };
        if (stamped)
        {
            SessionRules.Stamp(properties, now);
        }

        var ticket = new AuthenticationTicket(UserMatching.ToPrincipal(user), properties, AuthModule.CookieScheme);
        return new Session(app, $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}");
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

    // ---- configuration ----

    [Fact]
    public void Settings_default_to_4_hours_idle_12_hours_absolute_and_5_minutes_recheck()
    {
        var settings = SessionSettings.From(new ConfigurationBuilder().Build());

        Assert.Equal(new SessionSettings(TimeSpan.FromHours(4), TimeSpan.FromHours(12), TimeSpan.FromMinutes(5)), settings);
    }

    [Fact]
    public void Settings_are_read_from_the_environment_variables()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SESSION_IDLE_SECONDS"] = "60",
            ["SESSION_ABSOLUTE_SECONDS"] = "100",
            ["USER_STATUS_RECHECK_SECONDS"] = "10"
        }).Build();

        Assert.Equal(new SessionSettings(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(10)), SessionSettings.From(configuration));
    }

    [Theory]
    [InlineData("SESSION_IDLE_SECONDS", "0")]
    [InlineData("SESSION_ABSOLUTE_SECONDS", "-5")]
    [InlineData("USER_STATUS_RECHECK_SECONDS", "soon")]
    [InlineData("SESSION_IDLE_SECONDS", "1.5")]
    public void An_invalid_value_stops_the_API_from_starting_and_names_the_variable(string name, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [name] = value }).Build();

        var exception = Assert.Throws<InvalidOperationException>(() => SessionSettings.From(configuration));

        Assert.Contains(name, exception.Message);
    }

    // ---- AC 1: idle ----

    [Fact]
    public async Task Ac1_A_session_idle_for_4_hours_requires_sign_in_again()
    {
        await using var app = await StartAsync(new AlwaysActive());
        var session = SignedIn(app);
        Assert.Equal(HttpStatusCode.OK, (await session.Get(Things)).StatusCode);

        clock.Advance(TimeSpan.FromHours(4) + Second);

        await AssertProblem(await session.Get(Things), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ac1_Activity_restarts_the_idle_window_from_the_last_request()
    {
        await using var app = await StartAsync(new AlwaysActive());
        var session = SignedIn(app);

        // 1 h in, then 3 h 59 min later (just inside 4 h of the previous request): both are accepted.
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(HttpStatusCode.OK, (await session.Get(Things)).StatusCode);
        clock.Advance(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(59));
        Assert.Equal(HttpStatusCode.OK, (await session.Get(Things)).StatusCode);

        // 9 h after sign-in, under the absolute limit: only 4 h 1 s of idle time ends this session.
        clock.Advance(TimeSpan.FromHours(4) + Second);
        await AssertProblem(await session.Get(Things), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ac1_The_idle_limit_comes_from_SESSION_IDLE_SECONDS()
    {
        await using var app = await StartAsync(new AlwaysActive(), ("SESSION_IDLE_SECONDS", "60"));
        var inside = SignedIn(app);
        var outside = SignedIn(app);

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(HttpStatusCode.OK, (await inside.Get(Things)).StatusCode);
        clock.Advance(TimeSpan.FromSeconds(2));
        await AssertProblem(await outside.Get(Things), HttpStatusCode.Unauthorized);
    }

    // ---- AC 2: absolute ----

    [Fact]
    public async Task Ac2_A_session_older_than_12_hours_requires_sign_in_again_even_when_active()
    {
        await using var app = await StartAsync(new AlwaysActive());
        var session = SignedIn(app);

        foreach (var hours in new[] { 3, 3, 3, 2 })
        {
            clock.Advance(TimeSpan.FromHours(hours));
            Assert.Equal(HttpStatusCode.OK, (await session.Get(Things)).StatusCode);
        }

        // 11 h in and active a moment ago; 12 h + 1 s after sign-in the session is over.
        clock.Advance(TimeSpan.FromHours(1) + Second);
        var refused = await session.Get(Things);

        await AssertProblem(refused, HttpStatusCode.Unauthorized);
        Assert.Contains(refused.Headers.GetValues("Set-Cookie"), v => v.StartsWith("zephyr.session=;"));
    }

    [Fact]
    public async Task Ac2_The_absolute_limit_comes_from_SESSION_ABSOLUTE_SECONDS()
    {
        await using var app = await StartAsync(new AlwaysActive(), ("SESSION_IDLE_SECONDS", "60"), ("SESSION_ABSOLUTE_SECONDS", "100"));
        var session = SignedIn(app);

        clock.Advance(TimeSpan.FromSeconds(50));
        Assert.Equal(HttpStatusCode.OK, (await session.Get(Things)).StatusCode);
        clock.Advance(TimeSpan.FromSeconds(50));
        Assert.Equal(HttpStatusCode.OK, (await session.Get(Things)).StatusCode);
        clock.Advance(Second);

        await AssertProblem(await session.Get(Things), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ac2_A_session_without_the_sign_in_time_is_refused()
    {
        await using var app = await StartAsync(new AlwaysActive());

        await AssertProblem(await SignedIn(app, stamped: false).Get(Things), HttpStatusCode.Unauthorized);
    }

    // ---- AC 3: deactivation ----

    [Fact]
    public async Task Ac3_A_deactivated_users_request_more_than_5_minutes_later_is_401()
    {
        var active = true;
        await using var app = await StartAsync(ScriptedStatus(() => active));
        var session = SignedIn(app);
        Assert.Equal(HttpStatusCode.OK, (await session.Get(Things)).StatusCode);

        active = false;
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(HttpStatusCode.OK, (await session.Get(Things)).StatusCode);

        // 5 min + 1 s after the status was read: the status is read again and the session ends.
        clock.Advance(TimeSpan.FromMinutes(1) + Second);
        await AssertProblem(await session.Get(Things), HttpStatusCode.Unauthorized);
        await AssertProblem(await session.Get(Things), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ac3_A_shorter_recheck_window_applies()
    {
        var active = true;
        await using var app = await StartAsync(ScriptedStatus(() => active, recheckSeconds: 30));
        var session = SignedIn(app);
        Assert.Equal(HttpStatusCode.OK, (await session.Get(Things)).StatusCode);

        active = false;
        clock.Advance(TimeSpan.FromSeconds(31));

        await AssertProblem(await session.Get(Things), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ac3_The_status_is_read_once_per_window_and_read_again_after_it()
    {
        var active = true;
        var reads = new List<Guid>();
        var status = ScriptedStatus(() => active, reads);
        var user = Guid.NewGuid();

        Assert.True(await status.IsActiveAsync(user));
        active = false;
        clock.Advance(TimeSpan.FromSeconds(299));
        Assert.True(await status.IsActiveAsync(user));
        Assert.Single(reads);

        // The same answer API-token authentication uses: refused after the window.
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(await status.IsActiveAsync(user));
        Assert.Equal(2, reads.Count);
    }

    [Fact]
    public async Task Ac3_Forget_makes_the_next_check_read_again_and_users_are_cached_separately()
    {
        var active = true;
        var reads = new List<Guid>();
        var status = ScriptedStatus(() => active, reads);
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        Assert.True(await status.IsActiveAsync(first));
        Assert.True(await status.IsActiveAsync(second));
        Assert.Equal(2, reads.Count);

        active = false;
        status.Forget(first);

        Assert.False(await status.IsActiveAsync(first));
        Assert.True(await status.IsActiveAsync(second));
    }

    [DbFact]
    public async Task Ac3_The_database_lookup_counts_only_active_users_that_are_not_deleted()
    {
        var active = await AddUser("active@example.com");
        var inactive = await AddUser("inactive@example.com", active: false);
        var deleted = await AddUser("deleted@example.com");
        await using (var db = fixture.CreateContext())
        {
            db.AppUsers.Remove(await db.AppUsers.SingleAsync(u => u.Id == deleted.Id));
            await db.SaveChangesAsync();
        }

        await using var app = await StartAsync();
        using var scope = app.Services.CreateScope();
        var status = scope.ServiceProvider.GetRequiredService<IUserStatus>();

        Assert.True(await status.IsActiveAsync(active.Id));
        Assert.False(await status.IsActiveAsync(inactive.Id));
        Assert.False(await status.IsActiveAsync(deleted.Id));
        Assert.False(await status.IsActiveAsync(Guid.NewGuid()));
    }

    [DbFact]
    public async Task Ac3_A_user_deactivated_in_the_database_is_refused_after_the_window_with_the_real_status_check()
    {
        var user = await AddUser("pat@example.com");
        await using var app = await StartAsync();
        var session = SignedIn(app, user);
        Assert.Equal(HttpStatusCode.OK, (await session.Get(Things)).StatusCode);

        await SetActive(user.Id, false);
        clock.Advance(TimeSpan.FromMinutes(5) + Second);

        await AssertProblem(await session.Get(Things), HttpStatusCode.Unauthorized);
    }

    // ---- PATCH /api/v1/users/{id} ----

    private async Task<HttpResponseMessage> Patch(Session admin, Guid id, string body, bool withEtag = true, string? etag = null)
    {
        etag ??= withEtag ? ETags.Compute(await Reload(id)) : null;
        return await admin.Send("PATCH", $"/api/v1/users/{id}", body, etag);
    }

    [DbFact]
    public async Task Patch_deactivates_a_user_with_a_new_etag_updated_by_and_an_audit_row()
    {
        var admin = await AddUser("admin@example.com", ["Admin"]);
        var target = await AddUser("leaver@example.com");
        await using var app = await StartAsync();

        var response = await Patch(SignedIn(app, admin), target.Id, "{\"active\":false}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await Reload(target.Id);
        Assert.False(saved.Active);
        Assert.Equal(admin.Id, saved.UpdatedBy);
        Assert.Equal(ETags.Compute(saved), response.Headers.ETag?.Tag);
        var body = await ReadJson(response);
        Assert.False(body.GetProperty("active").GetBoolean());
        Assert.Equal(target.Id, body.GetProperty("id").GetGuid());
        await using var db = fixture.CreateContext();
        var audit = await db.AuditLogs.SingleAsync(a => a.EntityId == target.Id);
        Assert.Equal(("user_deactivated", admin.Id, "app_user"), (audit.Action, audit.UserId, audit.EntityType));
    }

    [DbFact]
    public async Task Ac3_Deactivating_through_the_API_refuses_the_user_on_the_next_request_without_waiting()
    {
        var admin = await AddUser("admin@example.com", ["Admin"]);
        var target = await AddUser("leaver@example.com");
        await using var app = await StartAsync();
        var leaver = SignedIn(app, target);
        Assert.Equal(HttpStatusCode.OK, (await leaver.Get(Things)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Patch(SignedIn(app, admin), target.Id, "{\"active\":false}")).StatusCode);

        await AssertProblem(await leaver.Get(Things), HttpStatusCode.Unauthorized);
    }

    [DbFact]
    public async Task Patch_reactivates_a_user_and_the_same_value_again_changes_nothing()
    {
        var admin = await AddUser("admin@example.com", ["Admin"]);
        var target = await AddUser("back@example.com", active: false);
        await using var app = await StartAsync();
        var session = SignedIn(app, admin);

        var first = await Patch(session, target.Id, "{\"active\":true}");
        var again = await Patch(session, target.Id, "{\"active\":true}");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True((await Reload(target.Id)).Active);
        Assert.Equal(first.Headers.ETag?.Tag, again.Headers.ETag?.Tag);
        await using var db = fixture.CreateContext();
        Assert.Equal("user_activated", (await db.AuditLogs.SingleAsync(a => a.EntityId == target.Id)).Action);
    }

    [DbFact]
    public async Task Patch_by_a_user_who_is_not_an_admin_is_403_and_changes_nothing()
    {
        var sales = await AddUser("sales@example.com");
        var target = await AddUser("other@example.com");
        await using var app = await StartAsync();

        var response = await Patch(SignedIn(app, sales), target.Id, "{\"active\":false}");

        await AssertProblem(response, HttpStatusCode.Forbidden);
        Assert.True((await Reload(target.Id)).Active);
    }

    [DbFact]
    public async Task Patch_without_if_match_is_428_and_with_a_stale_one_is_412()
    {
        var admin = await AddUser("admin@example.com", ["Admin"]);
        var target = await AddUser("leaver@example.com");
        await using var app = await StartAsync();
        var session = SignedIn(app, admin);

        await AssertProblem(await Patch(session, target.Id, "{\"active\":false}", withEtag: false), HttpStatusCode.PreconditionRequired, ErrorCodes.PreconditionRequired);
        await AssertProblem(await Patch(session, target.Id, "{\"active\":false}", etag: "\"stale\""), HttpStatusCode.PreconditionFailed, ErrorCodes.PreconditionFailed);

        Assert.True((await Reload(target.Id)).Active);
    }

    [DbFact]
    public async Task Patch_of_an_unknown_user_is_404()
    {
        var admin = await AddUser("admin@example.com", ["Admin"]);
        await using var app = await StartAsync();

        var response = await SignedIn(app, admin).Send("PATCH", $"/api/v1/users/{Guid.NewGuid()}", "{\"active\":false}", "\"any\"");

        await AssertProblem(response, HttpStatusCode.NotFound);
    }

    [DbTheory]
    [InlineData("{\"active\":false,\"name\":\"X\"}", "name", ErrorCodes.NotPatchable)]
    [InlineData("{\"roles\":[\"Admin\"]}", "roles", ErrorCodes.NotPatchable)]
    [InlineData("{}", "active", ErrorCodes.Validation)]
    [InlineData("{\"active\":\"no\"}", "active", ErrorCodes.Validation)]
    public async Task Patch_with_other_fields_or_a_bad_active_is_422_and_changes_nothing(string body, string field, string code)
    {
        var admin = await AddUser("admin@example.com", ["Admin"]);
        var target = await AddUser("leaver@example.com");
        await using var app = await StartAsync();

        var response = await Patch(SignedIn(app, admin), target.Id, body);

        await AssertProblem(response, HttpStatusCode.UnprocessableEntity);
        var errors = (await ReadJson(response)).GetProperty("errors").EnumerateArray().ToList();
        Assert.Contains(errors, e => e.GetProperty("field").GetString() == field && e.GetProperty("code").GetString() == code);
        var saved = await Reload(target.Id);
        Assert.True(saved.Active);
        Assert.Equal("Pat", saved.Name);
    }

    [DbFact]
    public async Task Patch_cannot_deactivate_the_last_active_admin_but_can_when_another_admin_is_active()
    {
        var onlyAdmin = await AddUser("admin@example.com", ["Admin"]);
        await using var app = await StartAsync();
        var session = SignedIn(app, onlyAdmin);

        var refused = await Patch(session, onlyAdmin.Id, "{\"active\":false}");

        await AssertProblem(refused, HttpStatusCode.UnprocessableEntity);
        Assert.Equal(ErrorCodes.LastAdmin, (await ReadJson(refused)).GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.True((await Reload(onlyAdmin.Id)).Active);

        await AddUser("second@example.com", ["Admin"]);
        var allowed = await Patch(session, onlyAdmin.Id, "{\"active\":false}");

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.False((await Reload(onlyAdmin.Id)).Active);
    }
}
