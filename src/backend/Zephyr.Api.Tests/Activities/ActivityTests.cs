using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zephyr.Api.Activities;
using Zephyr.Api.Common;
using Zephyr.Api.Data;
using Zephyr.Api.Permissions;
using Zephyr.Api.Tests.Permissions;
using Zephyr.Api.Tests.Support;

namespace Zephyr.Api.Tests.Activities;

/// <summary>
/// The activity table and API (US-151), one group of tests per acceptance criterion. They run the real activity routes behind the
/// permission matrix and need PostgreSQL. The contact and opportunity tables belong to later stories, so a test that needs one
/// creates a stub with the columns the API reads (<c>id</c>, <c>account_id</c>, <c>deleted_at</c>) and drops it again.
/// </summary>
[Collection("db")]
public class ActivityTests(DbFixture fixture) : IAsyncLifetime
{
    private const string Url = "/api/v1/activities";

    private readonly List<string> stubs = [];

    public Task InitializeAsync() => fixture.ResetAsync();

    public async Task DisposeAsync()
    {
        foreach (var table in stubs)
        {
            await ExecAsync($"drop table if exists {table}");
        }
    }

    // ---- helpers ----

    private static async Task<WebApplication> StartAsync()
    {
        var engine = new PermissionService();
        engine.Load(PermissionSeeder.Parse(PermissionSeeder.EmbeddedCsv()));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddApiConventions();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TokenAuthHandler>("Test", null);
        builder.Services.AddSingleton<IPermissionService>(engine);
        var app = builder.Build();
        app.UseApiConventions();
        app.UseAuthentication();
        app.MapActivityEndpoints();
        await app.StartAsync();
        return app;
    }

    private static Task<HttpResponseMessage> Send(
        WebApplication app, HttpMethod method, string url, Guid? user, object? body = null, string? ifMatch = null, bool bearer = false)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            var json = body as string ?? JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            request.Content = new StringContent(json, Encoding.UTF8, method == HttpMethod.Patch ? "application/merge-patch+json" : "application/json");
        }

        if (user is not null)
        {
            request.Headers.Add("X-Test-User", user.ToString());
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (bearer)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer test-token");
        }

        return app.GetTestClient().SendAsync(request);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> Created(WebApplication app, Guid user, object body)
    {
        var response = await Send(app, HttpMethod.Post, Url, user, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadJson(response);
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

    private static Guid[] Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();

    private static async Task<Guid[]> TimelineOf(WebApplication app, Guid user, Guid account) =>
        Ids(await ReadJson(await Send(app, HttpMethod.Get, $"{Url}?accountId={account}", user)));

    private async Task<Guid> AddUser(string role)
    {
        var user = new AppUser { Id = Guid.NewGuid(), Name = role, Email = $"{Guid.NewGuid():N}@example.test", Roles = [role] };
        await using var db = fixture.CreateContext();
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> AddAccount(string name = "Acme")
    {
        await using var db = fixture.CreateContext();
        var account = new Account { Name = name };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    /// <summary>The two outcomes most tests use, with the values of Seed/0001_reference_data.sql.</summary>
    private Task AddOutcomes() => ExecAsync(
        "insert into call_outcome (key, label, sort_order, counts_as_contact) values " +
        "('meeting_booked', 'Meeting booked', 9, true), ('no_answer', 'No answer', 1, false)");

    /// <summary>Creates the stub table of a regarding type that no story has built yet (dropped in DisposeAsync), then one record in it.</summary>
    private async Task<Guid> AddStubRecord(string table, Guid? account)
    {
        if (!stubs.Contains(table) && await ScalarAsync($"select to_regclass('public.{table}')::text") is null)
        {
            await ExecAsync($"create table {table} (id uuid primary key, account_id uuid null, deleted_at timestamptz null)");
            stubs.Add(table);
        }

        var id = Guid.NewGuid();
        await ExecAsync($"insert into {table} (id, account_id) values ('{id}', {(account is null ? "null" : $"'{account}'")})");
        return id;
    }

    private async Task ExecAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        if (value is null || value is DBNull)
        {
            return null;
        }

        return value.ToString();
    }

    private async Task<long> CountActivities() => long.Parse((await ScalarAsync("select count(*) from activity"))!);

    private Task<string?> StoredAccount(Guid activity) => ScalarAsync($"select account_id::text from activity where id = '{activity}'");

    // ---- AC 1: timeline membership ----

    [DbFact]
    public async Task Ac1_An_activity_regarding_an_opportunity_is_on_its_accounts_timeline_and_one_without_regarding_is_on_none()
    {
        var user = await AddUser("Salesperson");
        var acme = await AddAccount("Acme");
        var other = await AddAccount("Other");
        var opportunity = await AddStubRecord("opportunity", acme);
        await using var app = await StartAsync();

        var call = await Created(app, user, new { type = "call", subject = "Intro call", regardingType = "opportunity", regardingId = opportunity });
        var loose = await Created(app, user, new { type = "note", subject = "Loose note" });

        Assert.Equal(acme, call.GetProperty("accountId").GetGuid());
        Assert.Equal(JsonValueKind.Null, loose.GetProperty("accountId").ValueKind);
        Assert.Null(await StoredAccount(loose.GetProperty("id").GetGuid()));
        Assert.Equal(new[] { call.GetProperty("id").GetGuid() }, await TimelineOf(app, user, acme));
        Assert.Empty(await TimelineOf(app, user, other));
        Assert.Equal(2, Ids(await ReadJson(await Send(app, HttpMethod.Get, Url, user))).Length);
    }

    [DbFact]
    public async Task Ac1_The_account_comes_from_the_regarding_record_and_is_null_when_the_record_has_none()
    {
        var user = await AddUser("Salesperson");
        var acme = await AddAccount();
        var contact = await AddStubRecord("contact", acme);
        var contactWithout = await AddStubRecord("contact", null);
        await using var app = await StartAsync();

        var onAccount = await Created(app, user, new { type = "note", subject = "a", regardingType = "account", regardingId = acme });
        var onContact = await Created(app, user, new { type = "note", subject = "b", regardingType = "contact", regardingId = contact });
        var onLoneContact = await Created(app, user, new { type = "note", subject = "c", regardingType = "contact", regardingId = contactWithout });

        Assert.Equal(acme, onAccount.GetProperty("accountId").GetGuid());
        Assert.Equal(acme, onContact.GetProperty("accountId").GetGuid());
        Assert.Equal(JsonValueKind.Null, onLoneContact.GetProperty("accountId").ValueKind);
        Assert.Equal(2, (await TimelineOf(app, user, acme)).Length);
    }

    [DbFact]
    public async Task Ac1_The_timeline_is_newest_first_and_keyset_paged_without_gaps_or_repeats()
    {
        var user = await AddUser("Salesperson");
        var acme = await AddAccount();
        await using var app = await StartAsync();
        var created = new List<Guid>();
        foreach (var start in new[] { "2026-11-01T10:00:00Z", "2026-11-02T10:00:00Z", "2026-11-02T10:00:00Z", "2026-11-03T10:00:00Z" })
        {
            var activity = await Created(app, user, new { type = "meeting", subject = "m", startAt = start, regardingType = "account", regardingId = acme });
            created.Add(activity.GetProperty("id").GetGuid());
        }

        var first = await ReadJson(await Send(app, HttpMethod.Get, $"{Url}?accountId={acme}&limit=3", user));
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);
        var second = await ReadJson(await Send(app, HttpMethod.Get, $"{Url}?accountId={acme}&limit=3&cursor={Uri.EscapeDataString(cursor!)}", user));

        Assert.Equal(3, Ids(first).Length);
        Assert.Single(Ids(second));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        Assert.Equal(created.Order(), Ids(first).Concat(Ids(second)).Order());
        var moments = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray())
            .Select(i => i.GetProperty("occurredAt").GetDateTimeOffset()).ToList();
        Assert.Equal(moments.OrderByDescending(m => m), moments);
    }

    [DbFact]
    public async Task Ac1_A_page_without_a_limit_holds_25_rows_and_a_limit_over_100_or_a_bad_cursor_is_a_400()
    {
        var user = await AddUser("Salesperson");
        await using var app = await StartAsync();
        for (var i = 0; i < 26; i++)
        {
            await Created(app, user, new { type = "note", subject = $"n{i}" });
        }

        var page = await ReadJson(await Send(app, HttpMethod.Get, Url, user));

        Assert.Equal(25, Ids(page).Length);
        Assert.NotNull(page.GetProperty("nextCursor").GetString());
        await AssertProblem(await Send(app, HttpMethod.Get, $"{Url}?limit=101", user), HttpStatusCode.BadRequest, field: "limit");
        await AssertProblem(await Send(app, HttpMethod.Get, $"{Url}?cursor=garbage", user), HttpStatusCode.BadRequest, field: "cursor");
        await AssertProblem(await Send(app, HttpMethod.Get, $"{Url}?regardingType=invoice", user), HttpStatusCode.BadRequest, field: "regardingType");
    }

    // ---- AC 2: connected flag ----

    [DbFact]
    public async Task Ac2_Meeting_booked_sets_connected_true_no_answer_false_and_a_call_without_outcome_has_none()
    {
        var user = await AddUser("Salesperson");
        await AddOutcomes();
        await using var app = await StartAsync();

        var booked = await Created(app, user, new { type = "call", subject = "c1", callOutcomeKey = "meeting_booked" });
        var unanswered = await Created(app, user, new { type = "call", subject = "c2", callOutcomeKey = "no_answer" });
        var plain = await Created(app, user, new { type = "call", subject = "c3" });
        var note = await Created(app, user, new { type = "note", subject = "n" });

        Assert.True(booked.GetProperty("connected").GetBoolean());
        Assert.False(unanswered.GetProperty("connected").GetBoolean());
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("connected").ValueKind);
        Assert.Equal(JsonValueKind.Null, note.GetProperty("connected").ValueKind);
        Assert.Equal("true", await ScalarAsync($"select connected::text from activity where id = '{booked.GetProperty("id").GetGuid()}'"));
    }

    [DbFact]
    public async Task Ac2_Every_seeded_outcome_sets_connected_as_the_seed_says_and_there_are_nine()
    {
        var user = await AddUser("Salesperson");
        await using (var db = fixture.CreateContext())
        {
            await PermissionSeeder.SeedAsync(db);
        }

        fixture.RunSeedScript();
        var seeded = (await ScalarAsync("select string_agg(key || ':' || counts_as_contact::text, ',' order by key) from call_outcome"))!.Split(',');
        await using var app = await StartAsync();

        Assert.Equal(9, seeded.Length);
        foreach (var outcome in seeded)
        {
            var parts = outcome.Split(':');
            var call = await Created(app, user, new { type = "call", subject = parts[0], callOutcomeKey = parts[0] });
            Assert.Equal(parts[1] == "true", call.GetProperty("connected").GetBoolean());
        }
    }

    [DbFact]
    public async Task Ac2_A_client_cannot_send_connected_and_nothing_is_stored()
    {
        var user = await AddUser("Salesperson");
        await AddOutcomes();
        await using var app = await StartAsync();

        var response = await Send(app, HttpMethod.Post, Url, user, new { type = "call", subject = "c", callOutcomeKey = "no_answer", connected = true });

        await AssertProblem(response, (HttpStatusCode)422, "VALIDATION", "connected");
        Assert.Equal(0, await CountActivities());
    }

    // ---- AC 3: invalid regarding ----

    [DbFact]
    public async Task Ac3_A_regarding_type_outside_the_six_values_is_422_and_nothing_is_stored()
    {
        var user = await AddUser("Salesperson");
        var acme = await AddAccount();
        await using var app = await StartAsync();

        var response = await Send(app, HttpMethod.Post, Url, user, new { type = "note", subject = "n", regardingType = "invoice", regardingId = acme });

        await AssertProblem(response, (HttpStatusCode)422, "VALIDATION", "regardingType");
        Assert.Equal(0, await CountActivities());
    }

    [DbFact]
    public async Task Ac3_A_random_id_is_422_for_each_of_the_six_types_even_when_the_types_table_does_not_exist_yet()
    {
        var user = await AddUser("Salesperson");
        await using var app = await StartAsync();

        foreach (var type in new[] { "lead", "contact", "account", "opportunity", "project", "service_request" })
        {
            var response = await Send(app, HttpMethod.Post, Url, user, new { type = "note", subject = "n", regardingType = type, regardingId = Guid.NewGuid() });

            await AssertProblem(response, (HttpStatusCode)422, "VALIDATION", "regardingId");
        }

        Assert.Equal(0, await CountActivities());
    }

    [DbFact]
    public async Task Ac3_A_deleted_regarding_record_and_a_type_without_an_id_are_422()
    {
        var user = await AddUser("Salesperson");
        var acme = await AddAccount();
        await ExecAsync($"update account set deleted_at = now() where id = '{acme}'");
        await using var app = await StartAsync();

        var deleted = await Send(app, HttpMethod.Post, Url, user, new { type = "note", subject = "n", regardingType = "account", regardingId = acme });
        var half = await Send(app, HttpMethod.Post, Url, user, new { type = "note", subject = "n", regardingType = "account" });

        await AssertProblem(deleted, (HttpStatusCode)422, "VALIDATION", "regardingId");
        await AssertProblem(half, (HttpStatusCode)422, "VALIDATION", "regardingId");
        Assert.Equal(0, await CountActivities());
    }

    // ---- AC 4: client account ignored ----

    [DbFact]
    public async Task Ac4_A_body_with_accountId_is_422_and_the_stored_account_is_derived_from_the_regarding_record()
    {
        var user = await AddUser("Salesperson");
        var acme = await AddAccount("Acme");
        var other = await AddAccount("Other");
        await using var app = await StartAsync();

        var refused = await Send(app, HttpMethod.Post, Url, user,
            new { type = "note", subject = "n", regardingType = "account", regardingId = acme, accountId = other });
        var accepted = await Created(app, user, new { type = "note", subject = "n", regardingType = "account", regardingId = acme });

        await AssertProblem(refused, (HttpStatusCode)422, "VALIDATION", "accountId");
        Assert.Equal(1, await CountActivities());
        Assert.Equal(acme.ToString(), await StoredAccount(accepted.GetProperty("id").GetGuid()));
    }

    [DbFact]
    public async Task Ac4_A_patch_cannot_send_accountId_and_changing_the_regarding_record_re_derives_the_account()
    {
        var user = await AddUser("Salesperson");
        var acme = await AddAccount("Acme");
        var other = await AddAccount("Other");
        await using var app = await StartAsync();
        var activity = await Created(app, user, new { type = "note", subject = "n", regardingType = "account", regardingId = acme });
        var id = activity.GetProperty("id").GetGuid();
        var etag = (await Send(app, HttpMethod.Get, $"{Url}/{id}", user)).Headers.ETag!.Tag;

        var refused = await Send(app, HttpMethod.Patch, $"{Url}/{id}", user, new { accountId = other }, etag);
        var moved = await Send(app, HttpMethod.Patch, $"{Url}/{id}", user, new { regardingType = "account", regardingId = other }, etag);
        var movedJson = await ReadJson(moved);
        var cleared = await Send(app, HttpMethod.Patch, $"{Url}/{id}", user, """{"regardingType":null,"regardingId":null}""", moved.Headers.ETag!.Tag);

        await AssertProblem(refused, (HttpStatusCode)422, "VALIDATION", "accountId");
        Assert.Equal(other, movedJson.GetProperty("accountId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null(await StoredAccount(id));
    }

    // ---- AC 5: contact moves ----

    [DbFact]
    public async Task Ac5_Moving_a_contact_moves_its_activities_but_not_those_where_it_is_only_a_participant()
    {
        var user = await AddUser("Salesperson");
        var from = await AddAccount("From");
        var to = await AddAccount("To");
        var contact = await AddStubRecord("contact", from);
        await using var app = await StartAsync();
        var onContact = (await Created(app, user, new { type = "call", subject = "a", regardingType = "contact", regardingId = contact })).GetProperty("id").GetGuid();
        var participant = (await Created(app, user, new
        {
            type = "meeting",
            subject = "b",
            regardingType = "account",
            regardingId = from,
            participants = new[] { new { role = "attendee", contactId = contact } }
        })).GetProperty("id").GetGuid();

        await using (var db = fixture.CreateContext(user))
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("update contact set account_id = {0} where id = {1}", to, contact);
            Assert.Equal(1, await ActivityEndpoints.MoveContactActivitiesAsync(db, contact, to, user));
            Assert.Equal(from.ToString(), await StoredAccount(onContact)); // another connection still sees the old value: one transaction
            await transaction.CommitAsync();
        }

        Assert.Equal(to.ToString(), await StoredAccount(onContact));
        Assert.Equal(from.ToString(), await StoredAccount(participant));
        Assert.Equal("1", await ScalarAsync($"select count(*) from audit_log where action = 'activity_account_moved' and entity_id = '{onContact}'"));
        Assert.Equal(new[] { onContact }, await TimelineOf(app, user, to));
    }

    [DbFact]
    public async Task Ac5_A_failure_after_the_contact_changed_rolls_back_the_contact_and_its_activities()
    {
        var user = await AddUser("Salesperson");
        var from = await AddAccount("From");
        var to = await AddAccount("To");
        var contact = await AddStubRecord("contact", from);
        await using var app = await StartAsync();
        var activity = (await Created(app, user, new { type = "call", subject = "a", regardingType = "contact", regardingId = contact })).GetProperty("id").GetGuid();
        await ExecAsync("create function zz_fail_activity_move() returns trigger as $$ begin raise exception 'forced failure'; end; $$ language plpgsql");
        await ExecAsync("create trigger zz_fail_activity_move before update of account_id on activity for each row execute function zz_fail_activity_move()");
        try
        {
            await using var db = fixture.CreateContext(user);
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("update contact set account_id = {0} where id = {1}", to, contact);

            await Assert.ThrowsAnyAsync<Exception>(() => ActivityEndpoints.MoveContactActivitiesAsync(db, contact, to, user));
            await transaction.RollbackAsync();
        }
        finally
        {
            await ExecAsync("drop trigger if exists zz_fail_activity_move on activity");
            await ExecAsync("drop function if exists zz_fail_activity_move()");
        }

        Assert.Equal(from.ToString(), await ScalarAsync($"select account_id::text from contact where id = '{contact}'"));
        Assert.Equal(from.ToString(), await StoredAccount(activity));
        Assert.Equal("0", await ScalarAsync("select count(*) from audit_log where action = 'activity_account_moved'"));
    }

    // ---- AC 6: outcome rules ----

    [DbFact]
    public async Task Ac6_An_outcome_on_a_non_call_or_an_unknown_key_is_422_and_nothing_is_stored()
    {
        var user = await AddUser("Salesperson");
        await AddOutcomes();
        await using var app = await StartAsync();

        var onNote = await Send(app, HttpMethod.Post, Url, user, new { type = "note", subject = "n", callOutcomeKey = "no_answer" });
        var unknown = await Send(app, HttpMethod.Post, Url, user, new { type = "call", subject = "c", callOutcomeKey = "no_such_outcome" });

        await AssertProblem(onNote, (HttpStatusCode)422, "VALIDATION", "callOutcomeKey");
        await AssertProblem(unknown, (HttpStatusCode)422, "VALIDATION", "callOutcomeKey");
        Assert.Equal(0, await CountActivities());
    }

    [DbFact]
    public async Task Ac6_Changing_or_clearing_the_outcome_of_a_call_follows_connected()
    {
        var user = await AddUser("Salesperson");
        await AddOutcomes();
        await using var app = await StartAsync();
        var call = await Created(app, user, new { type = "call", subject = "c", callOutcomeKey = "no_answer" });
        var id = call.GetProperty("id").GetGuid();
        var etag = (await Send(app, HttpMethod.Get, $"{Url}/{id}", user)).Headers.ETag!.Tag;

        var booked = await Send(app, HttpMethod.Patch, $"{Url}/{id}", user, new { callOutcomeKey = "meeting_booked" }, etag);
        var cleared = await Send(app, HttpMethod.Patch, $"{Url}/{id}", user, """{"callOutcomeKey":null}""", booked.Headers.ETag!.Tag);

        Assert.True((await ReadJson(booked)).GetProperty("connected").GetBoolean());
        Assert.Equal(JsonValueKind.Null, (await ReadJson(cleared)).GetProperty("connected").ValueKind);
    }

    // ---- the rest of the API: fields, participants, patch, delete, access ----

    [DbFact]
    public async Task A_create_returns_201_with_location_etag_participants_and_the_ui_source_and_a_bearer_call_is_mcp()
    {
        var user = await AddUser("Salesperson");
        var colleague = await AddUser("BDR");
        await using var app = await StartAsync();

        var response = await Send(app, HttpMethod.Post, Url, user, new
        {
            type = "email",
            subject = "Proposal",
            direction = "outbound",
            participants = new object[]
            {
                new { role = "from", userId = colleague },
                new { role = "to", email = "buyer@example.test" }
            }
        });
        var body = await ReadJson(response);
        var viaToken = await ReadJson(await Send(app, HttpMethod.Post, Url, user, new { type = "note", subject = "n" }, bearer: true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"{Url}/{body.GetProperty("id").GetGuid()}", response.Headers.Location!.ToString());
        Assert.NotNull(response.Headers.ETag);
        Assert.Equal("ui", body.GetProperty("source").GetString());
        Assert.Equal("mcp", viaToken.GetProperty("source").GetString());
        Assert.Equal(2, body.GetProperty("participants").GetArrayLength());
        Assert.Equal(user.ToString(), await ScalarAsync($"select created_by::text from activity where id = '{body.GetProperty("id").GetGuid()}'"));
    }

    [DbFact]
    public async Task A_bad_body_is_422_with_one_error_per_field_and_nothing_is_stored()
    {
        var user = await AddUser("Salesperson");
        await using var app = await StartAsync();

        var response = await Send(app, HttpMethod.Post, Url, user, new
        {
            type = "chat",
            subject = new string('x', 251),
            direction = "sideways",
            durationMinutes = -1,
            colour = "red",
            participants = new object[]
            {
                new { role = "to" },
                new { role = "boss", email = "a@example.test" },
                new { role = "cc", email = "a@example.test", userId = Guid.NewGuid() }
            }
        });

        await AssertProblem(response, (HttpStatusCode)422, "VALIDATION");
        var fields = (await ReadJson(response)).GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("field").GetString()).ToList();
        foreach (var field in new[] { "type", "subject", "direction", "durationMinutes", "colour", "participants[0]", "participants[1].role", "participants[2]" })
        {
            Assert.Contains(field, fields);
        }

        await AssertProblem(await Send(app, HttpMethod.Post, Url, user, "{}"), (HttpStatusCode)422, "VALIDATION", "type");
        await AssertProblem(
            await Send(app, HttpMethod.Post, Url, user, new { type = "note", subject = "n", startAt = "2026-11-02T10:00:00Z", endAt = "2026-11-02T09:00:00Z" }),
            (HttpStatusCode)422, "VALIDATION", "endAt");
        await AssertProblem(
            await Send(app, HttpMethod.Post, Url, user, new { type = "note", subject = "n", participants = new[] { new { role = "to", userId = Guid.NewGuid() } } }),
            (HttpStatusCode)422, "VALIDATION", "participants[0].userId");
        await AssertProblem(
            await Send(app, HttpMethod.Post, Url, user, new { type = "note", subject = "n", participants = new[] { new { role = "to", contactId = Guid.NewGuid() } } }),
            (HttpStatusCode)422, "VALIDATION", "participants[0].contactId");
        await AssertProblem(await Send(app, HttpMethod.Post, Url, user, "[1]"), HttpStatusCode.BadRequest, field: "body");
        Assert.Equal(0, await CountActivities());
    }

    [DbFact]
    public async Task A_patch_needs_a_current_if_match_cannot_change_the_type_and_replaces_participants_only_when_sent()
    {
        var user = await AddUser("Salesperson");
        await using var app = await StartAsync();
        var activity = await Created(app, user, new
        {
            type = "meeting",
            subject = "Kick-off",
            participants = new[] { new { role = "attendee", email = "a@example.test" } }
        });
        var id = activity.GetProperty("id").GetGuid();
        var etag = (await Send(app, HttpMethod.Get, $"{Url}/{id}", user)).Headers.ETag!.Tag;

        await AssertProblem(await Send(app, HttpMethod.Patch, $"{Url}/{id}", user, new { subject = "x" }), (HttpStatusCode)428, "PRECONDITION_REQUIRED");
        await AssertProblem(await Send(app, HttpMethod.Patch, $"{Url}/{id}", user, new { subject = "x" }, "\"stale\""), (HttpStatusCode)412, "PRECONDITION_FAILED");
        await AssertProblem(await Send(app, HttpMethod.Patch, $"{Url}/{id}", user, new { type = "call" }, etag), (HttpStatusCode)422, "VALIDATION", "type");
        var renamed = await Send(app, HttpMethod.Patch, $"{Url}/{id}", user, new { subject = "Renamed" }, etag);
        var renamedJson = await ReadJson(renamed);
        var replaced = await Send(app, HttpMethod.Patch, $"{Url}/{id}", user,
            new { participants = new[] { new { role = "to", email = "b@example.test" }, new { role = "cc", email = "c@example.test" } } }, renamed.Headers.ETag!.Tag);

        var body = await ReadJson(replaced);
        Assert.Equal("Renamed", renamedJson.GetProperty("subject").GetString());
        Assert.Equal(1, renamedJson.GetProperty("participants").GetArrayLength());
        Assert.Equal("Renamed", body.GetProperty("subject").GetString());
        Assert.Equal(2, body.GetProperty("participants").GetArrayLength());
        Assert.Equal("meeting", body.GetProperty("type").GetString());
        // the first ETag is stale now
        await AssertProblem(await Send(app, HttpMethod.Patch, $"{Url}/{id}", user, new { subject = "y" }, etag), (HttpStatusCode)412, "PRECONDITION_FAILED");
    }

    [DbFact]
    public async Task A_delete_is_a_204_soft_delete_that_hides_the_activity_and_a_repeat_is_404()
    {
        var user = await AddUser("Salesperson");
        var acme = await AddAccount();
        await using var app = await StartAsync();
        var id = (await Created(app, user, new { type = "task", subject = "t", regardingType = "account", regardingId = acme })).GetProperty("id").GetGuid();

        var deleted = await Send(app, HttpMethod.Delete, $"{Url}/{id}", user);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(app, HttpMethod.Get, $"{Url}/{id}", user)).StatusCode);
        Assert.Empty(await TimelineOf(app, user, acme));
        Assert.Equal("1", await ScalarAsync($"select count(*) from activity where id = '{id}' and deleted_at is not null"));
        await AssertProblem(await Send(app, HttpMethod.Delete, $"{Url}/{id}", user), HttpStatusCode.NotFound);
    }

    [DbFact]
    public async Task Access_follows_the_matrix_own_activities_for_a_salesperson_everything_to_read_for_management_and_nothing_for_others()
    {
        var owner = await AddUser("Salesperson");
        var colleague = await AddUser("Salesperson");
        var management = await AddUser("Management");
        var consultant = await AddUser("Consultant");
        await using var app = await StartAsync();
        var id = (await Created(app, owner, new { type = "note", subject = "private" })).GetProperty("id").GetGuid();

        // another salesperson sees no trace of it
        Assert.Equal(HttpStatusCode.NotFound, (await Send(app, HttpMethod.Get, $"{Url}/{id}", colleague)).StatusCode);
        Assert.Empty(Ids(await ReadJson(await Send(app, HttpMethod.Get, Url, colleague))));
        await AssertProblem(await Send(app, HttpMethod.Patch, $"{Url}/{id}", colleague, new { subject = "x" }, "*"), HttpStatusCode.NotFound);
        await AssertProblem(await Send(app, HttpMethod.Delete, $"{Url}/{id}", colleague), HttpStatusCode.NotFound);
        Assert.Equal("1", await ScalarAsync($"select count(*) from activity where id = '{id}' and deleted_at is null"));

        // management reads all, writes none
        Assert.Equal(new[] { id }, Ids(await ReadJson(await Send(app, HttpMethod.Get, Url, management))));
        Assert.Equal(HttpStatusCode.OK, (await Send(app, HttpMethod.Get, $"{Url}/{id}", management)).StatusCode);
        await AssertProblem(await Send(app, HttpMethod.Post, Url, management, new { type = "note", subject = "n" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblem(await Send(app, HttpMethod.Delete, $"{Url}/{id}", management), HttpStatusCode.Forbidden, "FORBIDDEN");

        // a role without the activity entity, and nobody signed in
        await AssertProblem(await Send(app, HttpMethod.Get, Url, consultant), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblem(await Send(app, HttpMethod.Get, Url, null), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    // ---- schema ----

    [DbFact]
    public async Task The_database_rejects_bad_rows_directly_and_the_test_reset_clears_the_activity_tables()
    {
        // Regression: Respawn must be able to clean activity, activity_participant and their foreign keys.
        await ExecAsync("insert into activity (type, subject) values ('note', 'ok')");
        var id = await ScalarAsync("select id::text from activity");
        await ExecAsync($"insert into activity_participant (activity_id, role, email) values ('{id}', 'to', 'a@example.test')");

        await Assert.ThrowsAsync<PostgresException>(() => ExecAsync("insert into activity (type, subject) values ('sms', 'x')"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecAsync("insert into activity (type, subject, regarding_type) values ('note', 'x', 'account')"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecAsync("insert into activity (type, subject, connected) values ('note', 'x', true)"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecAsync($"insert into activity_participant (activity_id, role) values ('{id}', 'to')"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecAsync($"insert into activity_participant (activity_id, role, email, contact_id) values ('{id}', 'to', 'a@b.c', gen_random_uuid())"));

        await fixture.ResetAsync();

        Assert.Equal(0, await CountActivities());
        Assert.Equal("0", await ScalarAsync("select count(*) from activity_participant"));
    }
}
