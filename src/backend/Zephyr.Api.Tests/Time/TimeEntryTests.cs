using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Zephyr.Api.Common;
using Zephyr.Api.Data;
using Zephyr.Api.Permissions;
using Zephyr.Api.Tests.Support;
using Zephyr.Api.Time;

namespace Zephyr.Api.Tests.Time;

/// <summary>
/// Test stand-in for sign-in. <c>X-Test-User</c> is the <c>app_user</c> id; <c>X-Test-Channel</c> is the authentication type of the
/// principal: <c>Cookies</c> (the web session), <c>ApiToken</c> or <c>Mcp</c>. Roles are not taken from the request: the API reads them
/// from the database, like it does for a real session.
/// </summary>
public sealed class ChannelAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var channel = Request.Headers.TryGetValue("X-Test-Channel", out var value) ? value.ToString() : "Cookies";
        var identity = new ClaimsIdentity(new[] { new Claim(HttpCurrentUser.ClaimType, user.ToString()) }, channel);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
    }
}

/// <summary>
/// US-163. The rules are proven on <see cref="TimeRules"/> with a fake clock (AC 1, 3, 5, 7, 8); the routes are tested over HTTP against
/// the test database. Team membership and the project hourly-rate default wait for the project register (US-161): the project lookup
/// is a stub that answers "unknown", so only the pure billable rule is tested here.
/// </summary>
[Collection("db")]
public class TimeEntryTests(DbFixture fixture) : IAsyncLifetime
{
    private const string Url = "/api/v1/time-entries";

    /// <summary>Wednesday 4 Nov 2026, 11:00 in Stockholm: today is 2026-11-04, in ISO week 2026-W45.</summary>
    private const string Wednesday = "2026-11-04T10:00:00Z";

    private static readonly HttpStatusCode Unprocessable = (HttpStatusCode)422;
    private static readonly TimeTypeInfo ClientWork = new() { Key = "client_work", Active = true, BillableDefault = true, RequiresProject = true };
    private static readonly TimeTypeInfo Vacation = new() { Key = "vacation", Active = true, AllowsFuture = true };

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- unit helpers ----

    private static BusinessClock ClockAt(string utc) =>
        new(new FakeTimeProvider(DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture)));

    private static ModelState Check(
        BusinessClock clock, TimeTypeInfo? type, string date, int minutes = 480, Guid? project = null, string? notes = null)
    {
        var entry = new TimeEntry
        {
            TimeTypeKey = type?.Key ?? "unknown",
            WorkDate = DateOnly.Parse(date, CultureInfo.InvariantCulture),
            DurationMinutes = minutes,
            ProjectId = project,
            Notes = notes
        };
        var state = new ModelState();
        TimeRules.Validate(entry, type, clock, state);
        return state;
    }

    private static void AssertError(ModelState state, string field) =>
        Assert.Contains(state.Errors, e => e.Field == field && e.Code == ErrorCodes.Validation);

    // ---- unit tests on the rules ----

    [Fact]
    public void Ac1_Client_work_on_a_future_date_is_refused_and_vacation_is_accepted()
    {
        var clock = ClockAt(Wednesday);
        var project = Guid.NewGuid();

        AssertError(Check(clock, ClientWork, "2026-11-05", project: project), "workDate");
        Assert.True(Check(clock, ClientWork, "2026-11-04", project: project).IsValid);
        Assert.True(Check(clock, ClientWork, "2026-10-01", project: project).IsValid);
        Assert.True(Check(clock, Vacation, "2026-12-10").IsValid);
    }

    [Fact]
    public void Ac1_A_Sunday_evening_entry_for_Monday_is_future_for_client_work_but_not_for_vacation()
    {
        var clock = ClockAt("2026-07-05T18:00:00Z"); // Sunday 20:00 in Stockholm (CEST)

        Assert.Equal(new DateOnly(2026, 7, 5), clock.Today());
        AssertError(Check(clock, ClientWork, "2026-07-06", project: Guid.NewGuid()), "workDate");
        Assert.True(Check(clock, Vacation, "2026-07-06").IsValid);
        Assert.True(Check(clock, ClientWork, "2026-07-05", project: Guid.NewGuid()).IsValid);
    }

    [Theory]
    [InlineData(15, true)]
    [InlineData(450, true)]
    [InlineData(20, false)]
    [InlineData(1, false)]
    [InlineData(0, false)]
    [InlineData(-15, false)]
    public void Ac3_Duration_must_be_a_positive_multiple_of_15_minutes(int minutes, bool valid)
    {
        var state = Check(ClockAt(Wednesday), ClientWork, "2026-11-04", minutes, Guid.NewGuid());

        Assert.Equal(valid, state.IsValid);
        if (!valid)
        {
            AssertError(state, "durationMinutes");
        }
    }

    [Fact]
    public void Ac3_Notes_may_have_500_characters_and_not_501()
    {
        var clock = ClockAt(Wednesday);
        var project = Guid.NewGuid();

        Assert.True(Check(clock, ClientWork, "2026-11-04", project: project, notes: new string('a', 500)).IsValid);
        AssertError(Check(clock, ClientWork, "2026-11-04", project: project, notes: new string('a', 501)), "notes");
        // A character is a code point, like in the database: 500 emoji are 1000 UTF-16 units and still fit.
        Assert.True(Check(clock, ClientWork, "2026-11-04", project: project, notes: string.Concat(Enumerable.Repeat("😀", 500))).IsValid);
    }

    [Fact]
    public void Ac5_A_project_is_required_except_for_types_that_do_not_need_one()
    {
        var clock = ClockAt(Wednesday);

        AssertError(Check(clock, ClientWork, "2026-11-04"), "projectId");
        Assert.True(Check(clock, Vacation, "2026-11-04").IsValid);
        AssertError(Check(clock, null, "2026-11-04", project: Guid.NewGuid()), "timeTypeKey");
        AssertError(Check(clock, new TimeTypeInfo { Key = "old", Active = false }, "2026-11-04"), "timeTypeKey");
    }

    [Theory]
    [InlineData(true, null, null, true)]
    [InlineData(false, null, null, false)]
    [InlineData(false, null, true, true)]
    [InlineData(true, null, false, false)]
    [InlineData(true, true, null, true)]
    [InlineData(true, false, null, false)]
    [InlineData(false, false, true, true)]
    public void Ac7_Billable_follows_the_override_then_a_project_without_rate_then_the_type(
        bool typeDefault, bool? projectHasRate, bool? requested, bool expected)
    {
        var type = new TimeTypeInfo { Key = "any", Active = true, BillableDefault = typeDefault };

        Assert.Equal(expected, TimeRules.ResolveBillable(type, projectHasRate, requested));
    }

    [Fact]
    public void Ac8_Today_the_future_check_and_iso_week_use_the_same_Stockholm_date()
    {
        var clock = ClockAt("2026-07-05T23:30:00Z"); // Sunday 23:30 UTC is already Monday 01:30 in Stockholm (CEST)
        var today = clock.Today();

        Assert.Equal(new DateOnly(2026, 7, 6), today);
        Assert.Equal("2026-W28", TimeRules.IsoWeekOf(clock, today));
        Assert.Equal("2026-W27", TimeRules.IsoWeekOf(clock, new DateOnly(2026, 7, 5)));
        Assert.True(Check(clock, ClientWork, "2026-07-06", project: Guid.NewGuid()).IsValid);
        AssertError(Check(clock, ClientWork, "2026-07-07", project: Guid.NewGuid()), "workDate");
        Assert.Equal("2026-07", clock.PeriodOf(today));
    }

    [Theory]
    [InlineData(0, 600, false, true)]
    [InlineData(0, 615, true, true)]
    [InlineData(600, 840, true, true)]
    [InlineData(600, 855, false, false)]
    [InlineData(1440, 15, false, false)]
    public void Ac2_A_day_over_10_hours_warns_and_a_day_over_24_hours_is_refused(int others, int minutes, bool warns, bool allowed)
    {
        var state = new ModelState();

        var warning = TimeRules.CheckDay(others, new TimeEntry { WorkDate = new DateOnly(2026, 11, 4), DurationMinutes = minutes }, state);

        Assert.Equal(allowed, state.IsValid);
        Assert.Equal(warns, allowed && warning);
        if (!allowed)
        {
            AssertError(state, "durationMinutes");
        }
    }

    // ---- test host: the real time-entry routes, a fake clock, the real permission engine ----

    private async Task<WebApplication> StartAsync(string utcNow = Wednesday)
    {
        await using (var db = fixture.CreateContext())
        {
            await PermissionSeeder.SeedAsync(db);
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddApiConventions();
        builder.Services.AddSingleton(ClockAt(utcNow));
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, ChannelAuthHandler>("Test", null);
        var app = builder.Build();
        app.UseApiConventions();
        app.UseAuthentication();
        app.MapTimeEntries();
        await app.StartAsync();
        return app;
    }

    private async Task SeedTimeTypesAsync()
    {
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO time_type (key, label, sort_order, active, billable_default, requires_project, allows_future, category) VALUES
              ('client_work', 'Client Work', 1, true, true, true, false, 'client'),
              ('overtime', 'Overtime', 2, true, true, true, false, 'client'),
              ('internal_project', 'Internal Project', 3, true, false, true, false, 'internal'),
              ('vacation', 'Vacation', 10, true, false, false, true, 'absence'),
              ('absence', 'Absence', 11, true, false, false, true, 'absence')
            """);
    }

    private async Task<Guid> SeedUserAsync(string role = "Consultant", Guid? managerId = null)
    {
        await using var db = fixture.CreateContext();
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Name = role,
            Email = $"{Guid.NewGuid():N}@example.test",
            Roles = [role],
            ManagerId = managerId
        };
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static HttpRequestMessage Send(HttpMethod method, string url, Guid? user, object? body = null, string channel = "Cookies", string? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        if (user is not null)
        {
            request.Headers.Add("X-Test-User", user.ToString());
            request.Headers.Add("X-Test-Channel", channel);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return request;
    }

    private static Dictionary<string, object?> Entry(string type, string date, int minutes, bool withProject = true)
    {
        object? project = withProject ? Guid.NewGuid() : null;
        return new Dictionary<string, object?>
        {
            ["timeTypeKey"] = type,
            ["workDate"] = date,
            ["durationMinutes"] = minutes,
            ["projectId"] = project
        };
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string? code = null, string? field = null)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadJson(response);
        if (code is not null)
        {
            Assert.Equal(code, problem.GetProperty("code").GetString());
        }

        if (field is not null)
        {
            Assert.Contains(problem.GetProperty("errors").EnumerateArray(), e => e.GetProperty("field").GetString() == field);
        }
    }

    private static async Task<Guid> CreateAsync(HttpClient client, Guid user, Dictionary<string, object?> body)
    {
        var response = await client.SendAsync(Send(HttpMethod.Post, Url, user, body));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetGuid();
    }

    private async Task<int> CountAsync(string sql)
    {
        await using var db = fixture.CreateContext();
        return await db.Database.SqlQueryRaw<int>(sql).SingleAsync();
    }

    private async Task<string> EtagAsync(Guid id)
    {
        await using var db = fixture.CreateContext();
        return ETags.Compute(await db.TimeEntries.AsNoTracking().SingleAsync(e => e.Id == id));
    }

    private async Task SetStatusAsync(Guid id, string status)
    {
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"update time_entry set status = {status} where id = {id}");
    }

    private static string[] WarningCodes(JsonElement entry) =>
        entry.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()!).ToArray();

    private static string[] WorkDates(JsonElement list) =>
        list.EnumerateArray().Select(e => e.GetProperty("workDate").GetString()!).ToArray();

    // ---- AC 1: future dates ----

    [DbFact]
    public async Task Ac1_Client_work_for_tomorrow_is_422_and_vacation_next_month_is_201()
    {
        await SeedTimeTypesAsync();
        var user = await SeedUserAsync();
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var refused = await client.SendAsync(Send(HttpMethod.Post, Url, user, Entry("client_work", "2026-11-05", 480)));
        var today = await client.SendAsync(Send(HttpMethod.Post, Url, user, Entry("client_work", "2026-11-04", 480)));
        var vacation = await client.SendAsync(Send(HttpMethod.Post, Url, user, Entry("vacation", "2026-12-10", 480, withProject: false)));

        await AssertProblem(refused, Unprocessable, "VALIDATION", "workDate");
        Assert.Equal(HttpStatusCode.Created, today.StatusCode);
        Assert.Equal(HttpStatusCode.Created, vacation.StatusCode);
        Assert.Equal(2, await CountAsync("""select count(*)::int as "Value" from time_entry"""));
    }

    [DbFact]
    public async Task Ac1_A_Sunday_evening_entry_for_Monday_is_refused_for_client_work_and_accepted_for_vacation()
    {
        await SeedTimeTypesAsync();
        var user = await SeedUserAsync();
        await using var app = await StartAsync("2026-07-05T18:00:00Z");
        var client = app.GetTestClient();

        var refused = await client.SendAsync(Send(HttpMethod.Post, Url, user, Entry("client_work", "2026-07-06", 480)));
        var vacation = await client.SendAsync(Send(HttpMethod.Post, Url, user, Entry("vacation", "2026-07-06", 480, withProject: false)));

        await AssertProblem(refused, Unprocessable, "VALIDATION", "workDate");
        Assert.Equal(HttpStatusCode.Created, vacation.StatusCode);
    }

    // ---- AC 2: daily limit ----

    [DbFact]
    public async Task Ac2_Over_10_hours_saves_with_a_warning_and_over_24_hours_is_422()
    {
        await SeedTimeTypesAsync();
        var user = await SeedUserAsync();
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var first = await client.SendAsync(Send(HttpMethod.Post, Url, user, Entry("client_work", "2026-11-03", 480)));
        var second = await client.SendAsync(Send(HttpMethod.Post, Url, user, Entry("internal_project", "2026-11-03", 180)));
        var tooMuch = await client.SendAsync(Send(HttpMethod.Post, Url, user, Entry("client_work", "2026-11-03", 840)));
        var exactly24 = await client.SendAsync(Send(HttpMethod.Post, Url, user, Entry("client_work", "2026-11-03", 780)));
        var otherDay = await client.SendAsync(Send(HttpMethod.Post, Url, user, Entry("client_work", "2026-11-02", 840)));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Empty(WarningCodes(await ReadJson(first)));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var warned = await ReadJson(second);
        Assert.Equal(new[] { "DAY_OVER_10H" }, WarningCodes(warned));
        Assert.False(string.IsNullOrWhiteSpace(warned.GetProperty("warnings")[0].GetProperty("message").GetString()));
        await AssertProblem(tooMuch, Unprocessable, "VALIDATION", "durationMinutes");
        Assert.Equal(HttpStatusCode.Created, exactly24.StatusCode);
        Assert.Equal(HttpStatusCode.Created, otherDay.StatusCode);
        Assert.Equal(1440, await CountAsync("""select sum(duration_minutes)::int as "Value" from time_entry where work_date = '2026-11-03'"""));
    }

    [DbFact]
    public async Task Ac2_Concurrent_inserts_cannot_pass_24_hours_together()
    {
        await SeedTimeTypesAsync();
        var user = await SeedUserAsync();
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            client.SendAsync(Send(HttpMethod.Post, Url, user, Entry("client_work", "2026-11-03", 480)))));

        Assert.Equal(new[] { 201, 201, 201, 422 }, responses.Select(r => (int)r.StatusCode).Order().ToArray());
        Assert.Equal(1440, await CountAsync("""select sum(duration_minutes)::int as "Value" from time_entry where work_date = '2026-11-03'"""));
    }

    [DbFact]
    public async Task Ac2_Changing_an_entry_checks_the_day_without_counting_the_entry_itself()
    {
        await SeedTimeTypesAsync();
        var user = await SeedUserAsync();
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var id = await CreateAsync(client, user, Entry("client_work", "2026-11-03", 960));
        await CreateAsync(client, user, Entry("internal_project", "2026-11-03", 300));

        // 1200 + 300 = 25 hours is refused; 1140 + 300 = exactly 24 hours is saved, with a warning.
        var grow = await client.SendAsync(Send(HttpMethod.Patch, $"{Url}/{id}", user, new { durationMinutes = 1200 }, ifMatch: await EtagAsync(id)));
        var same = await client.SendAsync(Send(HttpMethod.Patch, $"{Url}/{id}", user, new { durationMinutes = 1140 }, ifMatch: await EtagAsync(id)));

        await AssertProblem(grow, Unprocessable, "VALIDATION", "durationMinutes");
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Equal(new[] { "DAY_OVER_10H" }, WarningCodes(await ReadJson(same)));
    }

    // ---- AC 3: steps and notes ----

    [DbFact]
    public async Task Ac3_Twenty_minutes_and_501_characters_of_notes_are_422_and_nothing_is_stored()
    {
        await SeedTimeTypesAsync();
        var user = await SeedUserAsync();
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var notes = Entry("client_work", "2026-11-04", 60);
        notes["notes"] = new string('x', 501);

        var step = await client.SendAsync(Send(HttpMethod.Post, Url, user, Entry("client_work", "2026-11-04", 20)));
        var tooLong = await client.SendAsync(Send(HttpMethod.Post, Url, user, notes));
        notes["notes"] = new string('x', 500);
        var fits = await client.SendAsync(Send(HttpMethod.Post, Url, user, notes));

        await AssertProblem(step, Unprocessable, "VALIDATION", "durationMinutes");
        await AssertProblem(tooLong, Unprocessable, "VALIDATION", "notes");
        Assert.Equal(HttpStatusCode.Created, fits.StatusCode);
        Assert.Equal(1, await CountAsync("""select count(*)::int as "Value" from time_entry"""));
    }

    [DbFact]
    public async Task Ac3_A_body_with_missing_wrong_or_server_owned_fields_is_422_with_one_error_per_field()
    {
        await SeedTimeTypesAsync();
        var user = await SeedUserAsync();
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var empty = await client.SendAsync(Send(HttpMethod.Post, Url, user, new { }));
        var owned = Entry("client_work", "2026-11-04", 60);
        owned["source"] = "import";
        owned["status"] = "Approved";
        owned["userId"] = Guid.NewGuid();
        var serverOwned = await client.SendAsync(Send(HttpMethod.Post, Url, user, owned));
        var badDate = await client.SendAsync(Send(HttpMethod.Post, Url, user, new { timeTypeKey = "client_work", workDate = "04/11/2026", durationMinutes = "60" }));
        var unknownType = await client.SendAsync(Send(HttpMethod.Post, Url, user, Entry("nonsense", "2026-11-04", 60)));

        await AssertProblem(empty, Unprocessable, "VALIDATION", "timeTypeKey");
        await AssertProblem(empty, Unprocessable, "VALIDATION", "workDate");
        await AssertProblem(empty, Unprocessable, "VALIDATION", "durationMinutes");
        await AssertProblem(serverOwned, Unprocessable, field: "source");
        await AssertProblem(serverOwned, Unprocessable, field: "status");
        await AssertProblem(serverOwned, Unprocessable, field: "userId");
        await AssertProblem(badDate, Unprocessable, "VALIDATION", "workDate");
        await AssertProblem(badDate, Unprocessable, "VALIDATION", "durationMinutes");
        await AssertProblem(unknownType, Unprocessable, "VALIDATION", "timeTypeKey");
        Assert.Equal(0, await CountAsync("""select count(*)::int as "Value" from time_entry"""));
    }

    // ---- AC 4: status edit ----

    [DbFact]
    public async Task Ac4_Patch_and_delete_are_refused_on_a_Submitted_or_Approved_entry_and_allowed_on_Draft_and_Rejected()
    {
        await SeedTimeTypesAsync();
        var user = await SeedUserAsync();
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var id = await CreateAsync(client, user, Entry("client_work", "2026-11-03", 480));

        foreach (var status in new[] { "Submitted", "Approved" })
        {
            await SetStatusAsync(id, status);

            await AssertProblem(
                await client.SendAsync(Send(HttpMethod.Patch, $"{Url}/{id}", user, new { durationMinutes = 240 }, ifMatch: await EtagAsync(id))),
                HttpStatusCode.Conflict, "STATUS_CHANGE_NOT_ALLOWED");
            await AssertProblem(await client.SendAsync(Send(HttpMethod.Delete, $"{Url}/{id}", user)), HttpStatusCode.Conflict, "STATUS_CHANGE_NOT_ALLOWED");
        }

        Assert.Equal(480, await CountAsync($"""select duration_minutes as "Value" from time_entry where id = '{id}' and deleted_at is null"""));

        foreach (var status in new[] { "Draft", "Rejected" })
        {
            await SetStatusAsync(id, status);

            var patched = await client.SendAsync(Send(HttpMethod.Patch, $"{Url}/{id}", user, new { durationMinutes = 240, notes = "fixed" }, ifMatch: await EtagAsync(id)));

            Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
            var body = await ReadJson(patched);
            Assert.Equal(240, body.GetProperty("durationMinutes").GetInt32());
            Assert.Equal("fixed", body.GetProperty("notes").GetString());
            Assert.Equal(status, body.GetProperty("status").GetString());
        }

        var deleted = await client.SendAsync(Send(HttpMethod.Delete, $"{Url}/{id}", user));

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(1, await CountAsync($"""select count(*)::int as "Value" from time_entry where id = '{id}' and deleted_at is not null"""));
        var list = await ReadJson(await client.SendAsync(Send(HttpMethod.Get, $"{Url}?week=2026-W45", user)));
        Assert.Equal(0, list.GetArrayLength());
    }

    [DbFact]
    public async Task Ac4_Patch_needs_a_current_ETag()
    {
        await SeedTimeTypesAsync();
        var user = await SeedUserAsync();
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var id = await CreateAsync(client, user, Entry("client_work", "2026-11-03", 480));

        var missing = await client.SendAsync(Send(HttpMethod.Patch, $"{Url}/{id}", user, new { durationMinutes = 240 }));
        var stale = await client.SendAsync(Send(HttpMethod.Patch, $"{Url}/{id}", user, new { durationMinutes = 240 }, ifMatch: "\"stale\""));

        await AssertProblem(missing, (HttpStatusCode)428, "PRECONDITION_REQUIRED");
        await AssertProblem(stale, HttpStatusCode.PreconditionFailed, "PRECONDITION_FAILED");
    }

    [DbFact]
    public async Task Ac4_Patch_applies_the_same_field_rules_as_create()
    {
        await SeedTimeTypesAsync();
        var user = await SeedUserAsync();
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var id = await CreateAsync(client, user, Entry("client_work", "2026-11-03", 480));

        async Task<HttpResponseMessage> Patch(object body) =>
            await client.SendAsync(Send(HttpMethod.Patch, $"{Url}/{id}", user, body, ifMatch: await EtagAsync(id)));

        await AssertProblem(await Patch(new { workDate = "2026-11-05" }), Unprocessable, "VALIDATION", "workDate");
        await AssertProblem(await Patch(new { durationMinutes = 20 }), Unprocessable, "VALIDATION", "durationMinutes");
        await AssertProblem(await Patch(new { projectId = (Guid?)null }), Unprocessable, "VALIDATION", "projectId");
        await AssertProblem(await Patch(new { status = "Approved" }), Unprocessable, field: "status");
        var moved = await Patch(new { workDate = "2026-11-02" });

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var body = await ReadJson(moved);
        Assert.Equal("2026-11-02", body.GetProperty("workDate").GetString());
        Assert.Equal("2026-W45", body.GetProperty("isoWeek").GetString());
        Assert.Equal(480, body.GetProperty("durationMinutes").GetInt32());
    }

    // ---- AC 5: project required ----

    [DbFact]
    public async Task Ac5_Client_work_without_a_project_is_422_and_vacation_without_a_project_is_201()
    {
        await SeedTimeTypesAsync();
        var user = await SeedUserAsync();
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var refused = await client.SendAsync(Send(HttpMethod.Post, Url, user, Entry("client_work", "2026-11-03", 480, withProject: false)));
        var absence = await client.SendAsync(Send(HttpMethod.Post, Url, user, Entry("absence", "2026-11-03", 120, withProject: false)));
        var vacation = await client.SendAsync(Send(HttpMethod.Post, Url, user, Entry("vacation", "2026-11-02", 480, withProject: false)));

        await AssertProblem(refused, Unprocessable, "VALIDATION", "projectId");
        Assert.Equal(HttpStatusCode.Created, absence.StatusCode);
        Assert.Equal(HttpStatusCode.Created, vacation.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ReadJson(vacation)).GetProperty("projectId").ValueKind);
    }

    // ---- AC 6: source ----

    [DbFact]
    public async Task Ac6_Source_is_set_by_the_server_from_the_channel()
    {
        await SeedTimeTypesAsync();
        var user = await SeedUserAsync();
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        async Task<string> SourceVia(string channel, string date)
        {
            var response = await client.SendAsync(Send(HttpMethod.Post, Url, user, Entry("client_work", date, 60), channel));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await ReadJson(response)).GetProperty("source").GetString()!;
        }

        Assert.Equal("api", await SourceVia(TimeEntryEndpoints.ApiTokenAuthType, "2026-11-02"));
        Assert.Equal("ui", await SourceVia("Cookies", "2026-11-03"));
        Assert.Equal("mcp", await SourceVia(TimeEntryEndpoints.McpAuthType, "2026-11-04"));
        Assert.Equal(1, await CountAsync("""select count(*)::int as "Value" from time_entry where source = 'api' and work_date = '2026-11-02'"""));
    }

    // ---- AC 7: billable default ----

    [DbFact]
    public async Task Ac7_Billable_defaults_from_the_time_type_and_an_override_persists()
    {
        await SeedTimeTypesAsync();
        var user = await SeedUserAsync();
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        async Task<JsonElement> Create(string type, string date, bool? billable = null)
        {
            var body = Entry(type, date, 60);
            if (billable is not null)
            {
                body["billable"] = billable;
            }

            var response = await client.SendAsync(Send(HttpMethod.Post, Url, user, body));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return await ReadJson(response);
        }

        Assert.True((await Create("client_work", "2026-11-02")).GetProperty("billable").GetBoolean());
        Assert.True((await Create("overtime", "2026-11-02")).GetProperty("billable").GetBoolean());
        Assert.False((await Create("internal_project", "2026-11-02")).GetProperty("billable").GetBoolean());
        Assert.True((await Create("internal_project", "2026-11-02", billable: true)).GetProperty("billable").GetBoolean());
        Assert.False((await Create("client_work", "2026-11-03", billable: false)).GetProperty("billable").GetBoolean());

        var stored = await ReadJson(await client.SendAsync(Send(HttpMethod.Get, $"{Url}?week=2026-W45", user)));
        Assert.Equal(5, stored.GetArrayLength());
        Assert.Single(stored.EnumerateArray(), e => e.GetProperty("workDate").GetString() == "2026-11-03" && !e.GetProperty("billable").GetBoolean());
    }

    [DbFact]
    public async Task Ac7_Changing_the_time_type_recomputes_the_default_but_an_explicit_choice_stays()
    {
        await SeedTimeTypesAsync();
        var user = await SeedUserAsync();
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var id = await CreateAsync(client, user, Entry("client_work", "2026-11-03", 60));

        async Task<JsonElement> Patch(object body)
        {
            var response = await client.SendAsync(Send(HttpMethod.Patch, $"{Url}/{id}", user, body, ifMatch: await EtagAsync(id)));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await ReadJson(response);
        }

        Assert.False((await Patch(new { timeTypeKey = "internal_project" })).GetProperty("billable").GetBoolean());
        Assert.True((await Patch(new { billable = true })).GetProperty("billable").GetBoolean());
        Assert.True((await Patch(new { notes = "unrelated" })).GetProperty("billable").GetBoolean());
        Assert.False((await Patch(new { timeTypeKey = "overtime", billable = false })).GetProperty("billable").GetBoolean());
    }

    // ---- AC 8: one zone ----

    [DbFact]
    public async Task Ac8_At_23_30_UTC_on_a_Sunday_the_Monday_date_is_today_and_gives_the_new_iso_week()
    {
        await SeedTimeTypesAsync();
        var user = await SeedUserAsync();
        await using var app = await StartAsync("2026-07-05T23:30:00Z");
        var client = app.GetTestClient();

        var monday = await client.SendAsync(Send(HttpMethod.Post, Url, user, Entry("client_work", "2026-07-06", 480)));
        var sunday = await client.SendAsync(Send(HttpMethod.Post, Url, user, Entry("client_work", "2026-07-05", 480)));
        var tuesday = await client.SendAsync(Send(HttpMethod.Post, Url, user, Entry("client_work", "2026-07-07", 480)));

        Assert.Equal(HttpStatusCode.Created, monday.StatusCode);
        Assert.Equal("2026-W28", (await ReadJson(monday)).GetProperty("isoWeek").GetString());
        Assert.Equal("2026-W27", (await ReadJson(sunday)).GetProperty("isoWeek").GetString());
        await AssertProblem(tuesday, Unprocessable, "VALIDATION", "workDate");
        var week = await ReadJson(await client.SendAsync(Send(HttpMethod.Get, Url, user)));
        Assert.Equal(new[] { "2026-07-06" }, WorkDates(week));
    }

    // ---- who may see and change what ----

    [DbFact]
    public async Task A_consultant_sees_only_their_own_entries_a_manager_sees_the_team_and_other_users_entries_are_404()
    {
        await SeedTimeTypesAsync();
        var manager = await SeedUserAsync("PM");
        var consultant = await SeedUserAsync(managerId: manager);
        var outsider = await SeedUserAsync();
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var id = await CreateAsync(client, consultant, Entry("client_work", "2026-11-03", 480));

        var own = await client.SendAsync(Send(HttpMethod.Get, $"{Url}?week=2026-W45&userId={consultant}", consultant));
        var peek = await client.SendAsync(Send(HttpMethod.Get, $"{Url}?week=2026-W45&userId={consultant}", outsider));
        var team = await client.SendAsync(Send(HttpMethod.Get, $"{Url}?week=2026-W45&userId={consultant}", manager));
        var notTeam = await client.SendAsync(Send(HttpMethod.Get, $"{Url}?week=2026-W45&userId={outsider}", manager));

        Assert.Equal(1, (await ReadJson(own)).GetArrayLength());
        await AssertProblem(peek, HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal(1, (await ReadJson(team)).GetArrayLength());
        await AssertProblem(notTeam, HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblem(
            await client.SendAsync(Send(HttpMethod.Patch, $"{Url}/{id}", outsider, new { durationMinutes = 60 }, ifMatch: await EtagAsync(id))),
            HttpStatusCode.NotFound);
        await AssertProblem(await client.SendAsync(Send(HttpMethod.Delete, $"{Url}/{id}", outsider)), HttpStatusCode.NotFound);
        // The manager can read the entry but the matrix only lets them change their own.
        await AssertProblem(
            await client.SendAsync(Send(HttpMethod.Patch, $"{Url}/{id}", manager, new { durationMinutes = 60 }, ifMatch: await EtagAsync(id))),
            HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [DbFact]
    public async Task Anonymous_callers_get_401_and_roles_without_create_get_403_and_a_bad_week_is_400()
    {
        await SeedTimeTypesAsync();
        var admin = await SeedUserAsync("Admin");
        var user = await SeedUserAsync();
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        await AssertProblem(await client.SendAsync(Send(HttpMethod.Get, Url, null)), HttpStatusCode.Unauthorized);
        await AssertProblem(await client.SendAsync(Send(HttpMethod.Post, Url, null, Entry("client_work", "2026-11-03", 60))), HttpStatusCode.Unauthorized);
        await AssertProblem(await client.SendAsync(Send(HttpMethod.Post, Url, admin, Entry("client_work", "2026-11-03", 60))), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblem(await client.SendAsync(Send(HttpMethod.Get, $"{Url}?week=2026-45", user)), HttpStatusCode.BadRequest, field: "week");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Send(HttpMethod.Get, Url, admin))).StatusCode);
    }

    [DbFact]
    public async Task The_week_list_holds_only_that_weeks_entries_in_date_order()
    {
        await SeedTimeTypesAsync();
        var user = await SeedUserAsync();
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        await CreateAsync(client, user, Entry("client_work", "2026-11-04", 60));
        await CreateAsync(client, user, Entry("client_work", "2026-11-02", 60));
        await CreateAsync(client, user, Entry("client_work", "2026-10-28", 60));

        var current = await ReadJson(await client.SendAsync(Send(HttpMethod.Get, Url, user)));
        var previous = await ReadJson(await client.SendAsync(Send(HttpMethod.Get, $"{Url}?week=2026-W44", user)));

        Assert.Equal(new[] { "2026-11-02", "2026-11-04" }, WorkDates(current));
        Assert.Equal(new[] { "2026-10-28" }, WorkDates(previous));
    }

    // ---- data model ----

    [DbFact]
    public async Task The_table_refuses_a_bad_duration_status_and_source_and_the_test_database_reset_still_works()
    {
        await SeedTimeTypesAsync();
        var user = await SeedUserAsync();

        async Task<string?> Violation(int minutes, string status, string source)
        {
            await using var db = fixture.CreateContext();
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                insert into time_entry (user_id, time_type_key, work_date, duration_minutes, iso_week, billable, status, source)
                values ({user}, 'vacation', '2026-11-04', {minutes}, '2026-W45', false, {status}, {source})
                """));
            return error.ConstraintName;
        }

        Assert.Equal("ck_time_entry_duration", await Violation(20, "Draft", "ui"));
        Assert.Equal("ck_time_entry_status", await Violation(60, "Done", "ui"));
        Assert.Equal("ck_time_entry_source", await Violation(60, "Draft", "fax"));
        await using (var app = await StartAsync())
        {
            await CreateAsync(app.GetTestClient(), user, Entry("vacation", "2026-11-04", 60, withProject: false));
        }

        await fixture.ResetAsync();

        Assert.Equal(0, await CountAsync("""select count(*)::int as "Value" from time_entry"""));
    }
}
