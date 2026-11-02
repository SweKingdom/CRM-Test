using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zephyr.Api.Common;
using Zephyr.Api.Data;
using Zephyr.Api.Tests.Support;

namespace Zephyr.Api.Tests.Common;

/// <summary>Reads the user from the X-Test-User header as the token's <c>sub</c> claim (stands in for the real sign-in).</summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity([new Claim("sub", user.ToString())], "Test");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
    }
}

/// <summary>Stands in for a domain validator: a text field that is present must not be empty.</summary>
public sealed class NonEmptyTextValidator : IWriteValidator
{
    public Task ValidateAsync(string entityType, JsonElement body, ModelState state)
    {
        foreach (var field in new[] { "entityType", "system", "value" })
        {
            if (body.TryGetProperty(field, out var value) && string.IsNullOrWhiteSpace(value.GetString()))
            {
                state.Add(field, "REQUIRED", $"{field} must not be empty.");
            }
        }

        return Task.CompletedTask;
    }
}

[Collection("db")]
public class ApiConventionsTests(DbFixture fixture) : IAsyncLifetime
{
    private const string Url = "/api/v1/external-ids";

    private static readonly ListSpec<ExternalId> Spec = new("createdAt", "updatedAt", "entityType", "system", "value");

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- test host: the real conventions over a small external-id API ----

    private static object Dto(ExternalId row) => new { row.Id, row.EntityType, row.System, row.Value, row.CreatedBy, row.UpdatedBy };

    private static string Text(JsonElement body, string field) =>
        body.TryGetProperty(field, out var value) ? value.GetString() ?? "" : "";

    private static void MapEndpoints(IEndpointRouteBuilder routes)
    {
        routes.MapGet(Url, async (HttpContext context) =>
        {
            var query = ListQuery<ExternalId>.Parse(context.Request.Query, Spec);
            var db = context.RequestServices.GetRequiredService<ZephyrDbContext>();
            return Results.Ok((await query.ToPageAsync(db.ExternalIds.AsNoTracking())).Select(Dto));
        });

        routes.MapGet(Url + "/{id:guid}", async (Guid id, HttpContext context, ZephyrDbContext db) =>
        {
            var row = await db.ExternalIds.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
            return row is null ? Results.NotFound() : ETags.Ok(context, row, Dto(row));
        });

        routes.MapPost(Url, async (HttpContext context) =>
        {
            var body = await WriteHooks.ReadBodyAsync(context.Request);
            await WriteHooks.Run(context, "external_id", body);
            var db = context.RequestServices.GetRequiredService<ZephyrDbContext>();
            var row = new ExternalId
            {
                EntityType = Text(body, "entityType"),
                EntityId = Guid.NewGuid(),
                System = Text(body, "system"),
                Value = Text(body, "value"),
                // A careless handler that copies audit fields from the body must still be overridden by the interceptor.
                CreatedBy = body.TryGetProperty("createdBy", out var by) ? by.GetGuid() : null
            };
            db.ExternalIds.Add(row);
            await db.SaveChangesAsync();
            return Results.Created($"{Url}/{row.Id}", Dto(row));
        }).WithWriteHooks();

        routes.MapMethods(Url + "/{id:guid}", ["PATCH"], async (Guid id, HttpContext context, ZephyrDbContext db) =>
        {
            var row = await db.ExternalIds.SingleOrDefaultAsync(x => x.Id == id);
            if (row is null)
            {
                return Results.NotFound();
            }

            ETags.RequireMatch(context.Request, row);
            var body = await WriteHooks.ReadBodyAsync(context.Request);
            await WriteHooks.Run(context, "external_id", body);
            if (body.TryGetProperty("value", out var value))
            {
                row.Value = value.GetString() ?? "";
            }

            if (body.TryGetProperty("updatedBy", out var by))
            {
                row.UpdatedBy = by.GetGuid();
            }

            await db.SaveChangesAsync();
            ETags.Set(context.Response, row);
            return Results.Ok(Dto(row));
        }).WithWriteHooks();

        routes.MapPost("/api/v1/forgetful", () => Results.Ok()).WithWriteHooks();
        routes.MapPost("/api/v1/unguarded", () => Results.Ok());
        routes.MapGet("/api/v1/boom", IResult () => throw new InvalidOperationException("secret detail from inside"));
        routes.MapGet("/api/v1/stale", IResult () => throw new DbUpdateConcurrencyException("stale"));
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddApiConventions();
        builder.Services.AddScoped<IWriteValidator, NonEmptyTextValidator>();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", null);
        var app = builder.Build();
        app.UseApiConventions();
        app.UseAuthentication();
        MapEndpoints(app);
        await app.StartAsync();
        return app;
    }

    // ---- helpers ----

    private static StringContent JsonBody(string json, string mediaType = "application/json") =>
        new(json, Encoding.UTF8, mediaType);

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static HttpRequestMessage Patch(string url, string json, string? ifMatch, Guid? user = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonBody(json, "application/merge-patch+json") };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (user is not null)
        {
            request.Headers.Add("X-Test-User", user.ToString());
        }

        return request;
    }

    /// <summary>A problem+json response with the status, naming <paramref name="named"/> in its detail and in an errors[] message.</summary>
    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string named)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadJson(response);
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("type").GetString()));
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("title").GetString()));
        Assert.Contains(named, problem.GetProperty("detail").GetString());
        Assert.Contains(problem.GetProperty("errors").EnumerateArray(), e => e.GetProperty("message").GetString()!.Contains(named));
    }

    private async Task<List<Guid>> SeedAsync(int count, string system = "dynamics", string prefix = "v")
    {
        await using var db = fixture.CreateContext();
        var rows = Enumerable.Range(0, count)
            .Select(i => new ExternalId { EntityType = "account", EntityId = Guid.NewGuid(), System = system, Value = $"{prefix}{i:000}" })
            .ToList();
        db.ExternalIds.AddRange(rows);
        await db.SaveChangesAsync();
        return rows.Select(r => r.Id).ToList();
    }

    /// <summary>One save per row, so each row gets its own updated_at (rows saved together share one).</summary>
    private async Task SeedOneByOneAsync(string system, params string[] values)
    {
        foreach (var value in values)
        {
            await using var db = fixture.CreateContext();
            db.ExternalIds.Add(new ExternalId { EntityType = "account", EntityId = Guid.NewGuid(), System = system, Value = value });
            await db.SaveChangesAsync();
        }
    }

    private static async Task<List<string>> Values(HttpResponseMessage response) =>
        (await ReadJson(response)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("value").GetString()!).ToList();

    // ---- AC 1: default paging ----

    [DbFact]
    public async Task Ac1_Default_page_is_50_and_the_cursor_walks_every_row_once()
    {
        var seeded = await SeedAsync(120);
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var ids = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await ReadJson(await client.GetAsync(cursor is null ? Url : $"{Url}?cursor={cursor}"));
            var items = page.GetProperty("items");
            if (pages == 0)
            {
                Assert.Equal(50, items.GetArrayLength());
            }

            ids.AddRange(items.EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null);

        // Every row shares one updated_at here, so the id tie-break is what keeps the pages free of duplicates and gaps.
        Assert.Equal(3, pages);
        Assert.Equal(120, ids.Distinct().Count());
        Assert.Equal(seeded.Order(), ids.Order());
    }

    [DbFact]
    public async Task Ac1_Last_page_has_a_null_next_cursor_even_when_it_is_exactly_full()
    {
        await SeedAsync(100);
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var first = await ReadJson(await client.GetAsync($"{Url}?limit=50"));
        var second = await ReadJson(await client.GetAsync($"{Url}?limit=50&cursor={first.GetProperty("nextCursor").GetString()}"));

        Assert.Equal(50, second.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Ac1_A_tampered_cursor_is_a_400()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var otherSort = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"sort":"value,id","values":["a","b"]}"""))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        await AssertProblem(await client.GetAsync($"{Url}?cursor=not-a-cursor"), HttpStatusCode.BadRequest, "cursor");
        await AssertProblem(await client.GetAsync($"{Url}?cursor="), HttpStatusCode.BadRequest, "cursor");
        await AssertProblem(await client.GetAsync($"{Url}?cursor={otherSort}"), HttpStatusCode.BadRequest, "cursor");
    }

    // ---- AC 2: limit cap ----

    [Theory]
    [InlineData("500")]
    [InlineData("201")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    public async Task Ac2_Invalid_limit_is_a_400_naming_limit(string limit)
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().GetAsync($"{Url}?limit={limit}");

        await AssertProblem(response, HttpStatusCode.BadRequest, "limit");
        Assert.Contains("200", (await ReadJson(response)).GetProperty("detail").GetString());
    }

    [DbFact]
    public async Task Ac2_Limit_200_returns_200_items()
    {
        await SeedAsync(210);
        await using var app = await StartAsync();

        var page = await ReadJson(await app.GetTestClient().GetAsync($"{Url}?limit=200"));

        Assert.Equal(200, page.GetProperty("items").GetArrayLength());
        Assert.NotEqual(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }

    // ---- AC 3: PATCH concurrency ----

    [DbFact]
    public async Task Ac3_Patch_without_if_match_is_428_and_with_a_stale_etag_is_412()
    {
        var id = (await SeedAsync(1))[0];
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var etag = (await client.GetAsync($"{Url}/{id}")).Headers.ETag!.Tag;

        var missing = await client.SendAsync(Patch($"{Url}/{id}", """{"value":"x"}""", null));
        var stale = await client.SendAsync(Patch($"{Url}/{id}", """{"value":"x"}""", "\"stale\""));
        var weak = await client.SendAsync(Patch($"{Url}/{id}", """{"value":"x"}""", "W/" + etag));

        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        Assert.Equal("PRECONDITION_REQUIRED", (await ReadJson(missing)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("PRECONDITION_FAILED", (await ReadJson(stale)).GetProperty("code").GetString());
        Assert.Equal("application/problem+json", stale.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.PreconditionFailed, weak.StatusCode); // a weak tag never matches If-Match
    }

    [DbFact]
    public async Task Ac3_Matching_etag_updates_and_returns_a_new_etag_that_the_next_read_agrees_with()
    {
        var id = (await SeedAsync(1))[0];
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var before = (await client.GetAsync($"{Url}/{id}")).Headers.ETag!.Tag;

        var patched = await client.SendAsync(Patch($"{Url}/{id}", """{"value":"changed"}""", before));

        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var after = patched.Headers.ETag!.Tag;
        Assert.NotEqual(before, after);
        Assert.Equal("changed", (await ReadJson(patched)).GetProperty("value").GetString());
        Assert.Equal(after, (await client.GetAsync($"{Url}/{id}")).Headers.ETag!.Tag);

        // The old ETag is now stale.
        var again = await client.SendAsync(Patch($"{Url}/{id}", """{"value":"again"}""", before));
        Assert.Equal(HttpStatusCode.PreconditionFailed, again.StatusCode);
    }

    [DbFact]
    public async Task Ac3_Get_returns_an_etag_and_a_matching_if_none_match_is_304()
    {
        var id = (await SeedAsync(1))[0];
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var first = await client.GetAsync($"{Url}/{id}");
        var etag = first.Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var matching = new HttpRequestMessage(HttpMethod.Get, $"{Url}/{id}");
        matching.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var notModified = await client.SendAsync(matching);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal(etag, notModified.Headers.ETag!.Tag);

        var different = new HttpRequestMessage(HttpMethod.Get, $"{Url}/{id}");
        different.Headers.TryAddWithoutValidation("If-None-Match", "\"other\"");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(different)).StatusCode);
    }

    [DbFact]
    public async Task Ac3_An_update_racing_another_one_fails_because_updated_at_is_the_concurrency_token()
    {
        var id = (await SeedAsync(1))[0];
        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        var a = await first.ExternalIds.SingleAsync(x => x.Id == id);
        var b = await second.ExternalIds.SingleAsync(x => x.Id == id);
        b.Value = "winner";
        await second.SaveChangesAsync();

        a.Value = "loser";

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => first.SaveChangesAsync());
    }

    [Fact]
    public async Task Ac3_A_concurrency_failure_inside_a_handler_is_answered_as_412()
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().GetAsync("/api/v1/stale");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // ---- AC 4: validation problem details ----

    [Fact]
    public async Task Ac4_Invalid_body_is_422_with_one_error_per_failing_field()
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().PostAsync(Url, JsonBody("""{"entityType":"","system":"dynamics","value":" "}"""));

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadJson(response);
        Assert.Equal(422, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("type").GetString()));
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("detail").GetString()));
        var errors = problem.GetProperty("errors").EnumerateArray().ToList();
        Assert.Equal(new[] { "entityType", "value" }, errors.Select(e => e.GetProperty("field").GetString()!).Order().ToArray());
        Assert.All(errors, e =>
        {
            Assert.Equal("REQUIRED", e.GetProperty("code").GetString());
            Assert.False(string.IsNullOrEmpty(e.GetProperty("message").GetString()));
        });
    }

    [Theory]
    [InlineData("""{"entityType":""")]
    [InlineData("")]
    [InlineData("[1,2]")]
    public async Task Ac4_Malformed_json_is_400(string json)
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().PostAsync(Url, JsonBody(json));

        await AssertProblem(response, HttpStatusCode.BadRequest, "body");
    }

    [Fact]
    public async Task Errors_without_a_body_become_problems_too_and_unexpected_ones_leak_nothing()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var notFound = await client.GetAsync("/api/v1/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal("application/problem+json", notFound.Content.Headers.ContentType?.MediaType);

        var boom = await client.GetAsync("/api/v1/boom");
        Assert.Equal(HttpStatusCode.InternalServerError, boom.StatusCode);
        Assert.Equal("application/problem+json", boom.Content.Headers.ContentType?.MediaType);
        var text = await boom.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret detail", text);
        Assert.DoesNotContain(" at Zephyr", text);
        using var problem = JsonDocument.Parse(text);
        Assert.False(string.IsNullOrEmpty(problem.RootElement.GetProperty("traceId").GetString()));
    }

    // ---- AC 5: filter and sort ----

    [DbFact]
    public async Task Ac5_Filter_and_descending_sort_return_matching_rows_newest_first()
    {
        await SeedOneByOneAsync("dynamics", "d0", "d1");
        await SeedOneByOneAsync("fortnox", "f0", "f1", "f2");
        await using var app = await StartAsync();

        var response = await app.GetTestClient().GetAsync($"{Url}?filter[system][eq]=fortnox&sort=-updatedAt");

        Assert.Equal(new[] { "f2", "f1", "f0" }, await Values(response));
    }

    [DbFact]
    public async Task Ac5_Default_sort_is_newest_first_and_ascending_sort_is_supported()
    {
        await SeedOneByOneAsync("fortnox", "f0", "f1", "f2");
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        Assert.Equal(new[] { "f2", "f1", "f0" }, await Values(await client.GetAsync(Url)));
        Assert.Equal(new[] { "f0", "f1", "f2" }, await Values(await client.GetAsync($"{Url}?sort=updatedAt")));
    }

    [DbFact]
    public async Task Ac5_Operators_ne_gt_lt_contains_and_in_filter_rows()
    {
        await SeedAsync(5, "fortnox", "Item");
        await SeedOneByOneAsync("dynamics", "50%off", "50xoff", "a_b", "axb");
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        Assert.Equal(new[] { "Item000", "Item001", "Item002", "Item003", "Item004" },
            await Values(await client.GetAsync($"{Url}?filter[system][ne]=dynamics&sort=value")));
        Assert.Equal(9, (await Values(await client.GetAsync($"{Url}?filter[createdAt][gt]=2000-01-01T00:00:00Z"))).Count);
        Assert.Empty(await Values(await client.GetAsync($"{Url}?filter[createdAt][lt]=2000-01-01T00:00:00Z")));
        Assert.Equal(new[] { "Item001", "Item003" },
            await Values(await client.GetAsync($"{Url}?filter[value][contains]=M00&filter[value][in]=Item001,Item003,nope&sort=value")));
        // LIKE wildcards in the value are literal text, not patterns.
        Assert.Equal(new[] { "50%off" }, await Values(await client.GetAsync($"{Url}?filter[value][contains]=50%25off")));
        Assert.Equal(new[] { "a_b" }, await Values(await client.GetAsync($"{Url}?filter[value][contains]=a_b")));
    }

    [DbFact]
    public async Task Ac5_Sort_by_a_text_field_pages_through_the_cursor_in_order()
    {
        await SeedAsync(120);
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var firstPage = await ReadJson(await client.GetAsync($"{Url}?sort=-value&limit=100"));
        var values = firstPage.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("value").GetString()!).ToList();
        Assert.Equal("v119", values[0]);
        Assert.Equal("v020", values[^1]);

        var second = await ReadJson(await client.GetAsync($"{Url}?sort=-value&limit=100&cursor={firstPage.GetProperty("nextCursor").GetString()}"));
        Assert.Equal(20, second.GetProperty("items").GetArrayLength());
        Assert.Equal("v019", second.GetProperty("items")[0].GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
    }

    [Theory]
    [InlineData("filter[bogus][eq]=x", "bogus")]
    [InlineData("filter[system][like]=x", "like")]
    [InlineData("filter[system]=x", "filter[system]")]
    [InlineData("filter[id][eq]=not-a-guid", "not-a-guid")]
    [InlineData("filter[value][gt]=1", "gt")]
    [InlineData("filter[createdAt][contains]=1", "contains")]
    [InlineData("filter[system][in]=,", "in")]
    [InlineData("sort=bogus", "bogus")]
    [InlineData("sort=-value,value", "value")]
    [InlineData("sort=", "sort")]
    [InlineData("colour=red", "colour")]
    [InlineData("limit=1&limit=2", "limit")]
    public async Task Ac5_Unknown_field_operator_sort_key_or_parameter_is_a_400_naming_it(string query, string named)
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().GetAsync($"{Url}?{query}");

        await AssertProblem(response, HttpStatusCode.BadRequest, named);
    }

    // ---- AC 6: audit user ----

    [DbFact]
    public async Task Ac6_Audit_columns_hold_the_token_user_never_a_value_from_the_body()
    {
        var author = Guid.NewGuid();
        var editor = Guid.NewGuid();
        var spoof = Guid.NewGuid();
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var create = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = JsonBody($$"""{"entityType":"account","system":"dynamics","value":"a1","createdBy":"{{spoof}}"}""")
        };
        create.Headers.Add("X-Test-User", author.ToString());
        var created = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await ReadJson(created)).GetProperty("id").GetGuid();

        var etag = (await client.GetAsync($"{Url}/{id}")).Headers.ETag!.Tag;
        var patched = await client.SendAsync(Patch($"{Url}/{id}", $$"""{"value":"a2","updatedBy":"{{spoof}}"}""", etag, editor));
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        await using var db = fixture.CreateContext();
        var saved = await db.ExternalIds.SingleAsync(x => x.Id == id);
        Assert.Equal(author, saved.CreatedBy);
        Assert.Equal(editor, saved.UpdatedBy);
    }

    [DbFact]
    public async Task Ac6_A_request_without_a_token_writes_as_the_system_user()
    {
        await using var app = await StartAsync();

        var created = await app.GetTestClient().PostAsync(Url, JsonBody("""{"entityType":"account","system":"dynamics","value":"anon"}"""));

        var id = (await ReadJson(created)).GetProperty("id").GetGuid();
        await using var db = fixture.CreateContext();
        Assert.Equal(SystemUser.Id, (await db.ExternalIds.SingleAsync(x => x.Id == id)).CreatedBy);
    }

    // ---- write hooks ----

    private static List<string?> UnguardedWriteEndpoints(IServiceProvider services) =>
        services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1", StringComparison.Ordinal) == true)
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>() is { } methods && methods.HttpMethods.Any(m => m is "POST" or "PUT" or "PATCH"))
            .Where(e => e.Metadata.GetMetadata<WriteHooksMarker>() is null)
            .Select(e => e.RoutePattern.RawText)
            .ToList();

    [Fact]
    public async Task Write_hook_check_flags_a_write_endpoint_that_skips_the_hooks()
    {
        await using var app = await StartAsync();

        Assert.Equal(new[] { "/api/v1/unguarded" }, UnguardedWriteEndpoints(app.Services));
    }

    [DbFact]
    public void Every_write_endpoint_of_the_real_app_calls_the_write_hooks()
    {
        using var factory = new WebApplicationFactory<Program>();

        Assert.Empty(UnguardedWriteEndpoints(factory.Services));
    }

    [Fact]
    public async Task A_write_endpoint_that_succeeds_without_running_the_hooks_fails_loudly()
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().PostAsync("/api/v1/forgetful", JsonBody("{}"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Registered_validators_run_in_addition_to_the_default_no_op()
    {
        await using var app = await StartAsync();
        using var scope = app.Services.CreateScope();

        var validators = scope.ServiceProvider.GetServices<IWriteValidator>().ToList();

        Assert.Contains(validators, v => v is NoOpWriteValidator);
        Assert.Contains(validators, v => v is NonEmptyTextValidator);
    }
}
