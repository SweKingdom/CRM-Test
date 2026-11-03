using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Zephyr.Api.Auth;
using Zephyr.Api.Common;
using Zephyr.Api.Data;
using Zephyr.Api.Tests.Support;

namespace Zephyr.Api.Tests.Auth;

/// <summary>
/// Personal API tokens (US-142). Time is a <see cref="FakeTimeProvider"/> shared by the token handler, the cookie handler and
/// the user-status cache, so no test waits. Tokens are created with a signed-in cookie (a token cannot create tokens) and
/// used with <c>Authorization: Bearer</c>.
/// </summary>
[Collection("db")]
public class ApiTokenTests(DbFixture fixture) : IAsyncLifetime
{
    private const string Tokens = "/api/v1/me/tokens";
    private const string WhoAmI = "/whoami";
    private static readonly Regex TokenFormat = new("^zph_[A-Za-z0-9_-]{43}$");
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero));

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers ----

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddApiConventions();
        builder.AddZephyrAuth();
        builder.Services.AddSingleton<TimeProvider>(clock);
        var app = builder.Build();
        app.UseApiConventions();
        app.UseZephyrAuth();
        app.MapApiTokenEndpoints();
        app.MapGet(WhoAmI, (ClaimsPrincipal user) => Results.Ok(new
        {
            id = user.FindFirstValue(HttpCurrentUser.ClaimType),
            roles = user.FindAll("role").Select(c => c.Value).ToArray(),
            via = user.Identity!.AuthenticationType
        }));
        await app.StartAsync();
        return app;
    }

    private async Task<AppUser> AddUser(string email, string[]? roles = null)
    {
        var user = new AppUser { Name = "Pat", Email = email, Roles = roles ?? ["Salesperson"] };
        await using var db = fixture.CreateContext();
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>A browser session signed in at the current fake time.</summary>
    private string CookieFor(WebApplication app, AppUser user)
    {
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthModule.CookieScheme);
        var now = clock.GetUtcNow();
        var properties = new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = now + options.ExpireTimeSpan };
        SessionRules.Stamp(properties, now);
        var ticket = new AuthenticationTicket(UserMatching.ToPrincipal(user), properties, AuthModule.CookieScheme);
        return $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}";
    }

    private static Task<HttpResponseMessage> Send(
        WebApplication app, string method, string url, string? cookie = null, string? bearer = null, string? json = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
            request.Headers.Add(AuthModule.CsrfHeader, "1");
        }

        if (bearer is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return app.GetTestClient().SendAsync(request);
    }

    private Task<HttpResponseMessage> TryCreate(WebApplication app, AppUser user, string json = "{\"name\":\"CI\"}") =>
        Send(app, "POST", Tokens, CookieFor(app, user), json: json);

    private async Task<JsonElement> Create(WebApplication app, AppUser user, string json = "{\"name\":\"CI\"}")
    {
        var response = await TryCreate(app, user, json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadJson(response);
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

    private static async Task AssertRefusedToken(HttpResponseMessage response)
    {
        await AssertProblem(response, HttpStatusCode.Unauthorized);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.Select(h => h.Scheme));
    }

    private async Task<int> CountTokens()
    {
        await using var db = fixture.CreateContext();
        return await db.ApiTokens.CountAsync();
    }

    private async Task<ApiToken> Reload(string id)
    {
        await using var db = fixture.CreateContext();
        return await db.ApiTokens.AsNoTracking().SingleAsync(t => t.Id == Guid.Parse(id));
    }

    // ---- token format and hash (no database) ----

    [Fact]
    public void A_token_is_zph_plus_43_base64url_characters_and_every_token_is_different()
    {
        var first = ApiTokens.NewToken();

        Assert.Matches(TokenFormat, first);
        Assert.NotEqual(first, ApiTokens.NewToken());
    }

    [Fact]
    public void The_hash_is_lowercase_hex_SHA_256_of_the_UTF8_text()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ApiTokens.Hash("abc"));
    }

    [Fact]
    public void A_stored_hash_matches_only_with_the_same_hash_and_the_current_algorithm()
    {
        var hash = ApiTokens.Hash("zph_x");

        Assert.True(ApiTokens.HashMatches(new ApiToken { TokenHash = hash, HashAlgorithm = "sha256" }, hash));
        Assert.False(ApiTokens.HashMatches(new ApiToken { TokenHash = hash, HashAlgorithm = "hmac-sha256" }, hash));
        Assert.False(ApiTokens.HashMatches(new ApiToken { TokenHash = ApiTokens.Hash("zph_y"), HashAlgorithm = "sha256" }, hash));
    }

    // ---- AC 1: create ----

    [DbFact]
    public async Task Ac1_Create_returns_the_plaintext_once_and_later_reads_show_only_prefix_and_expiry()
    {
        var user = await AddUser("pat@example.com");
        await using var app = await StartAsync();

        var response = await TryCreate(app, user, "{\"name\":\"My script\"}");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var created = await ReadJson(response);
        var plaintext = created.GetProperty("token").GetString()!;
        Assert.Matches(TokenFormat, plaintext);
        Assert.Equal(plaintext[..12], created.GetProperty("prefix").GetString());
        Assert.Equal("My script", created.GetProperty("name").GetString());
        Assert.Equal(clock.GetUtcNow().AddDays(90), created.GetProperty("expiresAt").GetDateTimeOffset());

        var list = await Send(app, "GET", Tokens, CookieFor(app, user));
        var text = await list.Content.ReadAsStringAsync();
        var rows = JsonDocument.Parse(text).RootElement.EnumerateArray().ToList();

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var row = Assert.Single(rows);
        Assert.Equal(
            new[] { "expiresAt", "id", "lastUsedAt", "name", "prefix", "revokedAt" },
            row.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(created.GetProperty("id").GetGuid(), row.GetProperty("id").GetGuid());
        Assert.Equal(created.GetProperty("prefix").GetString(), row.GetProperty("prefix").GetString());
        Assert.Equal(created.GetProperty("expiresAt").GetDateTimeOffset(), row.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.DoesNotContain(plaintext, text);
    }

    [DbFact]
    public async Task Ac1_A_user_lists_only_their_own_tokens()
    {
        var pat = await AddUser("pat@example.com");
        var sam = await AddUser("sam@example.com");
        await using var app = await StartAsync();
        var mine = await Create(app, pat, "{\"name\":\"mine\"}");
        await Create(app, sam, "{\"name\":\"theirs\"}");

        var list = await ReadJson(await Send(app, "GET", Tokens, CookieFor(app, pat)));

        Assert.Equal(mine.GetProperty("id").GetGuid(), Assert.Single(list.EnumerateArray()).GetProperty("id").GetGuid());
    }

    [DbFact]
    public async Task Ac1_A_created_token_acts_as_its_owner_with_the_owners_roles()
    {
        var owner = await AddUser("fin@example.com", ["Finance", "Consultant"]);
        await using var app = await StartAsync();
        var plaintext = (await Create(app, owner)).GetProperty("token").GetString()!;

        var response = await Send(app, "GET", WhoAmI, bearer: plaintext);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var who = await ReadJson(response);
        Assert.Equal(owner.Id.ToString(), who.GetProperty("id").GetString());
        Assert.Equal(new[] { "Finance", "Consultant" }, who.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToArray());
        Assert.Equal("ApiToken", who.GetProperty("via").GetString());
    }

    [DbTheory]
    [InlineData("{}")]
    [InlineData("{\"name\":\"   \"}")]
    [InlineData("{\"name\":42}")]
    public async Task Ac1_A_missing_or_blank_name_is_422_and_nothing_is_stored(string body)
    {
        var user = await AddUser("pat@example.com");
        await using var app = await StartAsync();

        var response = await TryCreate(app, user, body);

        await AssertProblem(response, HttpStatusCode.UnprocessableEntity, ErrorCodes.Validation);
        Assert.Contains((await ReadJson(response)).GetProperty("errors").EnumerateArray(), e => e.GetProperty("field").GetString() == "name");
        Assert.Equal(0, await CountTokens());
    }

    [DbFact]
    public async Task Ac1_A_name_longer_than_100_characters_is_422_and_100_is_accepted()
    {
        var user = await AddUser("pat@example.com");
        await using var app = await StartAsync();

        await AssertProblem(await TryCreate(app, user, $"{{\"name\":\"{new string('n', 101)}\"}}"), HttpStatusCode.UnprocessableEntity, ErrorCodes.Validation);
        Assert.Equal(HttpStatusCode.Created, (await TryCreate(app, user, $"{{\"name\":\"{new string('n', 100)}\"}}")).StatusCode);
    }

    [DbFact]
    public async Task Creating_without_signing_in_is_401()
    {
        await using var app = await StartAsync();

        await AssertProblem(await Send(app, "POST", Tokens, json: "{\"name\":\"CI\"}"), HttpStatusCode.Unauthorized);
        await AssertProblem(await Send(app, "GET", Tokens), HttpStatusCode.Unauthorized);
    }

    [DbFact]
    public async Task A_token_cannot_create_tokens()
    {
        var user = await AddUser("pat@example.com");
        await using var app = await StartAsync();
        var plaintext = (await Create(app, user)).GetProperty("token").GetString()!;

        var response = await Send(app, "POST", Tokens, bearer: plaintext, json: "{\"name\":\"second\"}");

        await AssertProblem(response, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        Assert.Equal(1, await CountTokens());
    }

    // ---- AC 2: limit ----

    [DbFact]
    public async Task Ac2_The_11th_active_token_is_422_with_a_message_and_is_not_stored()
    {
        var user = await AddUser("pat@example.com");
        await using var app = await StartAsync();
        for (var i = 0; i < 10; i++)
        {
            await Create(app, user, $"{{\"name\":\"t{i}\"}}");
        }

        var response = await TryCreate(app, user);

        await AssertProblem(response, HttpStatusCode.UnprocessableEntity, ErrorCodes.TokenLimit);
        var problem = await ReadJson(response);
        Assert.Contains("10 active API tokens", problem.GetProperty("detail").GetString());
        Assert.Equal(ErrorCodes.TokenLimit, problem.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.Equal(10, await CountTokens());
    }

    [DbFact]
    public async Task Ac2_Expired_and_revoked_tokens_do_not_count_towards_the_limit()
    {
        var user = await AddUser("pat@example.com");
        await using var app = await StartAsync();
        for (var i = 0; i < 5; i++)
        {
            await Create(app, user, "{\"name\":\"short\",\"expiresInDays\":1}");
        }

        var kept = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            kept.Add((await Create(app, user, "{\"name\":\"long\"}")).GetProperty("id").GetString()!);
        }

        // Ten tokens now; two days later the five short ones are expired, then one more is revoked: four are active.
        clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(HttpStatusCode.NoContent, (await Send(app, "DELETE", $"{Tokens}/{kept[0]}", CookieFor(app, user))).StatusCode);
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await TryCreate(app, user)).StatusCode);
        }

        await AssertProblem(await TryCreate(app, user), HttpStatusCode.UnprocessableEntity, ErrorCodes.TokenLimit);
    }

    [DbFact]
    public async Task Ac2_The_limit_is_per_user()
    {
        var pat = await AddUser("pat@example.com");
        var sam = await AddUser("sam@example.com");
        await using var app = await StartAsync();
        for (var i = 0; i < 10; i++)
        {
            await Create(app, pat);
        }

        Assert.Equal(HttpStatusCode.Created, (await TryCreate(app, sam)).StatusCode);
    }

    // ---- AC 3: expiry ----

    [DbTheory]
    [InlineData("366")]
    [InlineData("1000")]
    [InlineData("0")]
    [InlineData("-5")]
    public async Task Ac3_Expiry_outside_1_to_365_days_is_422_out_of_range_and_nothing_is_stored(string days)
    {
        var user = await AddUser("pat@example.com");
        await using var app = await StartAsync();

        var response = await TryCreate(app, user, $"{{\"name\":\"CI\",\"expiresInDays\":{days}}}");

        await AssertProblem(response, HttpStatusCode.UnprocessableEntity);
        var error = (await ReadJson(response)).GetProperty("errors")[0];
        Assert.Equal("expiresInDays", error.GetProperty("field").GetString());
        Assert.Equal(ErrorCodes.OutOfRange, error.GetProperty("code").GetString());
        Assert.Equal(0, await CountTokens());
    }

    [DbTheory]
    [InlineData("\"soon\"")]
    [InlineData("1.5")]
    [InlineData("true")]
    public async Task Ac3_Expiry_that_is_not_a_whole_number_is_422(string days)
    {
        var user = await AddUser("pat@example.com");
        await using var app = await StartAsync();

        var response = await TryCreate(app, user, $"{{\"name\":\"CI\",\"expiresInDays\":{days}}}");

        await AssertProblem(response, HttpStatusCode.UnprocessableEntity);
        Assert.Equal(ErrorCodes.Validation, (await ReadJson(response)).GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [DbTheory]
    [InlineData("{\"name\":\"CI\"}", 90)]
    [InlineData("{\"name\":\"CI\",\"expiresInDays\":null}", 90)]
    [InlineData("{\"name\":\"CI\",\"expiresInDays\":1}", 1)]
    [InlineData("{\"name\":\"CI\",\"expiresInDays\":365}", 365)]
    public async Task Ac3_An_omitted_expiry_is_90_days_and_1_to_365_days_are_accepted(string body, int days)
    {
        var user = await AddUser("pat@example.com");
        await using var app = await StartAsync();

        var created = await Create(app, user, body);

        var expected = clock.GetUtcNow().AddDays(days);
        Assert.Equal(expected, created.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal(expected.UtcDateTime, (await Reload(created.GetProperty("id").GetString()!)).ExpiresAt);
    }

    [DbFact]
    public async Task Ac3_A_token_is_accepted_until_its_expiry_and_refused_with_401_from_then_on()
    {
        var user = await AddUser("pat@example.com");
        await using var app = await StartAsync();
        var plaintext = (await Create(app, user, "{\"name\":\"CI\",\"expiresInDays\":1}")).GetProperty("token").GetString()!;

        clock.Advance(TimeSpan.FromDays(1) - Second);
        Assert.Equal(HttpStatusCode.OK, (await Send(app, "GET", WhoAmI, bearer: plaintext)).StatusCode);

        // expires_at <= now is expired.
        clock.Advance(Second);
        await AssertRefusedToken(await Send(app, "GET", WhoAmI, bearer: plaintext));
        clock.Advance(TimeSpan.FromDays(30));
        await AssertRefusedToken(await Send(app, "GET", WhoAmI, bearer: plaintext));
    }

    // ---- AC 4: revoke ----

    [DbFact]
    public async Task Ac4_A_revoked_token_is_refused_immediately_and_the_list_shows_when_it_was_revoked()
    {
        var user = await AddUser("pat@example.com");
        await using var app = await StartAsync();
        var created = await Create(app, user);
        var plaintext = created.GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await Send(app, "GET", WhoAmI, bearer: plaintext)).StatusCode);

        var revoked = await Send(app, "DELETE", $"{Tokens}/{created.GetProperty("id").GetString()}", CookieFor(app, user));

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        await AssertRefusedToken(await Send(app, "GET", WhoAmI, bearer: plaintext));
        var row = (await ReadJson(await Send(app, "GET", Tokens, CookieFor(app, user))))[0];
        Assert.Equal(clock.GetUtcNow(), row.GetProperty("revokedAt").GetDateTimeOffset());
    }

    [DbFact]
    public async Task Ac4_Revoking_twice_is_204_and_keeps_the_first_revocation_time()
    {
        var user = await AddUser("pat@example.com");
        await using var app = await StartAsync();
        var id = (await Create(app, user)).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await Send(app, "DELETE", $"{Tokens}/{id}", CookieFor(app, user))).StatusCode);
        var first = (await Reload(id)).RevokedAt;

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(HttpStatusCode.NoContent, (await Send(app, "DELETE", $"{Tokens}/{id}", CookieFor(app, user))).StatusCode);

        Assert.Equal(first, (await Reload(id)).RevokedAt);
    }

    [DbFact]
    public async Task Ac4_Revoking_someone_elses_or_an_unknown_token_is_404_and_changes_nothing()
    {
        var pat = await AddUser("pat@example.com");
        var sam = await AddUser("sam@example.com");
        await using var app = await StartAsync();
        var theirs = await Create(app, sam);

        var other = await Send(app, "DELETE", $"{Tokens}/{theirs.GetProperty("id").GetString()}", CookieFor(app, pat));
        var unknown = await Send(app, "DELETE", $"{Tokens}/{Guid.NewGuid()}", CookieFor(app, pat));

        await AssertProblem(other, HttpStatusCode.NotFound);
        await AssertProblem(unknown, HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.OK, (await Send(app, "GET", WhoAmI, bearer: theirs.GetProperty("token").GetString())).StatusCode);
    }

    [DbFact]
    public async Task Ac4_A_token_can_revoke_itself_and_is_then_refused()
    {
        var user = await AddUser("pat@example.com");
        await using var app = await StartAsync();
        var created = await Create(app, user);
        var plaintext = created.GetProperty("token").GetString()!;

        var response = await Send(app, "DELETE", $"{Tokens}/{created.GetProperty("id").GetString()}", bearer: plaintext);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await AssertRefusedToken(await Send(app, "GET", WhoAmI, bearer: plaintext));
    }

    // ---- AC 5: storage ----

    [DbFact]
    public async Task Ac5_The_database_holds_only_the_hash_and_no_column_or_audit_row_contains_the_plaintext()
    {
        var user = await AddUser("pat@example.com");
        await using var app = await StartAsync();
        var created = await Create(app, user, "{\"name\":\"CI\"}");
        var plaintext = created.GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await Send(app, "DELETE", $"{Tokens}/{created.GetProperty("id").GetString()}", CookieFor(app, user))).StatusCode);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select token_hash, hash_algorithm, prefix, row_to_json(t)::text from api_token t", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))).ToLowerInvariant();
        Assert.Equal(expectedHash, reader.GetString(0));
        Assert.Equal("sha256", reader.GetString(1));
        Assert.Equal(plaintext[..12], reader.GetString(2));
        var wholeRow = reader.GetString(3);
        Assert.DoesNotContain(plaintext, wholeRow);
        Assert.DoesNotContain(plaintext[4..], wholeRow);
        Assert.False(await reader.ReadAsync());
        await reader.CloseAsync();

        await using var db = fixture.CreateContext();
        var audit = await db.AuditLogs.Where(a => a.EntityType == "api_token").OrderBy(a => a.At).ToListAsync();
        Assert.Equal(new[] { "api_token_created", "api_token_revoked" }, audit.Select(a => a.Action).ToArray());
        Assert.All(audit, a => Assert.DoesNotContain(plaintext[4..], a.Detail));
        Assert.All(audit, a => Assert.Equal(user.Id, a.UserId));
    }

    [DbFact]
    public async Task Ac5_The_hash_is_unique_in_the_database()
    {
        var user = await AddUser("pat@example.com");
        await using var db = fixture.CreateContext();
        var hash = ApiTokens.Hash("zph_same");
        db.ApiTokens.Add(new ApiToken { UserId = user.Id, Name = "a", TokenHash = hash, Prefix = "zph_same", ExpiresAt = clock.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync();

        db.ApiTokens.Add(new ApiToken { UserId = user.Id, Name = "b", TokenHash = hash, Prefix = "zph_same", ExpiresAt = clock.GetUtcNow().UtcDateTime });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [DbFact]
    public async Task Reset_between_tests_works_while_api_token_rows_reference_app_user()
    {
        // Regression: Respawn must be able to clear api_token (foreign key to app_user) and app_user together.
        var user = await AddUser("pat@example.com");
        await using var app = await StartAsync();
        await Create(app, user);

        await fixture.ResetAsync();

        await using var db = fixture.CreateContext();
        Assert.Empty(await db.ApiTokens.ToListAsync());
        Assert.Empty(await db.AppUsers.ToListAsync());
    }

    // ---- authentication handler ----

    [DbFact]
    public async Task An_unknown_or_malformed_bearer_token_is_401_with_a_bearer_challenge()
    {
        await using var app = await StartAsync();

        await AssertRefusedToken(await Send(app, "GET", WhoAmI, bearer: ApiTokens.NewToken()));
        await AssertRefusedToken(await Send(app, "GET", WhoAmI, bearer: "zph_"));
        await AssertProblem(await Send(app, "GET", WhoAmI, bearer: "not-a-zephyr-token"), HttpStatusCode.Unauthorized);
    }

    [DbFact]
    public async Task A_token_is_refused_when_its_owner_is_deactivated_after_the_status_window()
    {
        var user = await AddUser("pat@example.com");
        await using var app = await StartAsync();
        var plaintext = (await Create(app, user)).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await Send(app, "GET", WhoAmI, bearer: plaintext)).StatusCode);

        await using (var db = fixture.CreateContext())
        {
            (await db.AppUsers.SingleAsync(u => u.Id == user.Id)).Active = false;
            await db.SaveChangesAsync();
        }

        clock.Advance(TimeSpan.FromMinutes(5) + Second);
        await AssertRefusedToken(await Send(app, "GET", WhoAmI, bearer: plaintext));
    }

    [DbFact]
    public async Task A_token_is_refused_at_once_when_its_owner_is_deleted()
    {
        var user = await AddUser("pat@example.com");
        await using var app = await StartAsync();
        var plaintext = (await Create(app, user)).GetProperty("token").GetString()!;
        await using (var db = fixture.CreateContext())
        {
            db.AppUsers.Remove(await db.AppUsers.SingleAsync(u => u.Id == user.Id));
            await db.SaveChangesAsync();
        }

        await AssertRefusedToken(await Send(app, "GET", WhoAmI, bearer: plaintext));
    }

    [DbFact]
    public async Task Last_used_at_is_set_on_first_use_and_updated_at_most_once_per_minute()
    {
        var user = await AddUser("pat@example.com");
        await using var app = await StartAsync();
        var created = await Create(app, user);
        var id = created.GetProperty("id").GetString()!;
        var plaintext = created.GetProperty("token").GetString()!;
        Assert.Null((await Reload(id)).LastUsedAt);

        await Send(app, "GET", WhoAmI, bearer: plaintext);
        var first = clock.GetUtcNow().UtcDateTime;
        Assert.Equal(first, (await Reload(id)).LastUsedAt);

        clock.Advance(TimeSpan.FromSeconds(59));
        await Send(app, "GET", WhoAmI, bearer: plaintext);
        Assert.Equal(first, (await Reload(id)).LastUsedAt);

        clock.Advance(TimeSpan.FromSeconds(1));
        await Send(app, "GET", WhoAmI, bearer: plaintext);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, (await Reload(id)).LastUsedAt);
    }
}
