using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zephyr.Api.Common;
using Zephyr.Api.Data;
using Zephyr.Api.Finance;
using Zephyr.Api.Permissions;
using Zephyr.Api.Tests.Common;
using Zephyr.Api.Tests.Support;

namespace Zephyr.Api.Tests.Finance;

/// <summary>US-134: money value, conversion to SEK, the exchange-rate table and its endpoints.</summary>
[Collection("db")]
public class MoneyTests(DbFixture fixture) : IAsyncLifetime
{
    private const string Url = "/api/v1/exchange-rates";

    private static readonly DateOnly November = new(2026, 11, 1);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers ----

    private static decimal Dec(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);

    private static JsonElement Serialized(Money money) => JsonSerializer.SerializeToElement(money);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) => Parse(await response.Content.ReadAsStringAsync());

    /// <summary>The real rate routes behind the permission engine, with the real matrix in the database and a test sign-in (X-Test-User).</summary>
    private async Task<WebApplication> StartAsync()
    {
        await using (var db = fixture.CreateContext())
        {
            await PermissionSeeder.SeedAsync(db);
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddApiConventions();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", null);
        var app = builder.Build();
        app.UseApiConventions();
        app.UseAuthentication();
        app.MapExchangeRates();
        await app.StartAsync();
        return app;
    }

    /// <summary>An active user holding <paramref name="role"/>; the permission engine reads the role from this row.</summary>
    private async Task<Guid> SeedUserAsync(string role)
    {
        await using var db = fixture.CreateContext();
        var user = new AppUser { Id = Guid.NewGuid(), Name = role, Email = $"{Guid.NewGuid():N}@example.test", Roles = [role] };
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

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

    private async Task SeedCurrenciesAsync(params string[] codes)
    {
        await using var db = fixture.CreateContext();
        foreach (var code in codes)
        {
            await db.Database.ExecuteSqlAsync($"insert into currency (key, label, sort_order, iso3) values ({code}, {code}, 1, {code})");
        }
    }

    private async Task AddRateAsync(string currency, DateOnly month, decimal rate)
    {
        await using var db = fixture.CreateContext();
        db.ExchangeRates.Add(new ExchangeRate { Currency = currency, Month = month, RateToSek = rate });
        await db.SaveChangesAsync();
    }

    private async Task<int> RateCountAsync()
    {
        await using var db = fixture.CreateContext();
        return await db.ExchangeRates.CountAsync();
    }

    // ---- AC 1: output shape ----

    [Fact]
    public void Ac1_Money_is_written_with_string_amounts_two_decimals_currency_amountSEK_and_rateDate()
    {
        var money = Money.Convert(new MoneyInput(300000m, "SEK"), 1m, November);

        Assert.Equal(
            """{"amount":"300000.00","currency":"SEK","amountSEK":"300000.00","rateDate":"2026-11-01"}""",
            JsonSerializer.Serialize(money));
    }

    [Fact]
    public void Ac1_Whole_and_one_decimal_amounts_still_have_exactly_two_decimals()
    {
        Assert.Equal("5.00", Serialized(Money.Convert(new MoneyInput(5m, "SEK"), 1m, November)).GetProperty("amount").GetString());
        Assert.Equal("5.50", Serialized(Money.Convert(new MoneyInput(5.5m, "SEK"), 1m, November)).GetProperty("amount").GetString());
    }

    // ---- input ----

    [Fact]
    public void Input_reads_amount_and_currency_and_ignores_amountSEK_and_rateDate()
    {
        var state = new ModelState();

        var input = MoneyInput.Read(
            Parse("""{"amount":"-12.5","currency":"USD","amountSEK":"1.00","rateDate":"1999-01-01"}"""), "price", state);

        Assert.True(state.IsValid);
        Assert.Equal(new MoneyInput(-12.5m, "USD"), input);
    }

    [Theory]
    [InlineData("""{"amount":100,"currency":"USD"}""", "price.amount")]
    [InlineData("""{"amount":"100.001","currency":"USD"}""", "price.amount")]
    [InlineData("""{"amount":"1e3","currency":"USD"}""", "price.amount")]
    [InlineData("""{"amount":" 1","currency":"USD"}""", "price.amount")]
    [InlineData("""{"amount":"","currency":"USD"}""", "price.amount")]
    [InlineData("""{"amount":"12345678901234567","currency":"USD"}""", "price.amount")]
    [InlineData("""{"currency":"USD"}""", "price.amount")]
    [InlineData("""{"amount":"100.00","currency":"usd"}""", "price.currency")]
    [InlineData("""{"amount":"100.00","currency":"US"}""", "price.currency")]
    [InlineData("""{"amount":"100.00","currency":5}""", "price.currency")]
    [InlineData("""{"amount":"100.00"}""", "price.currency")]
    [InlineData("\"100.00\"", "price")]
    public void Input_with_a_bad_amount_or_currency_is_refused_naming_the_field(string json, string field)
    {
        var state = new ModelState();

        var input = MoneyInput.Read(Parse(json), "price", state);

        Assert.Null(input);
        Assert.Contains(state.Errors, e => e.Field == field);
    }

    [Fact]
    public void Input_reports_the_amount_and_the_currency_together()
    {
        var state = new ModelState();

        MoneyInput.Read(Parse("{}"), "price", state);

        Assert.Equal(new[] { "price.amount", "price.currency" }, state.Errors.Select(e => e.Field).Order().ToArray());
    }

    // ---- AC 2: conversion ----

    [Theory]
    [InlineData("100.00", "USD", "10.000000", "1000.00")]
    [InlineData("100.00", "EUR", "11.000000", "1100.00")]
    [InlineData("100.00", "NOK", "0.950000", "95.00")]
    [InlineData("100.00", "AED", "2.720000", "272.00")]
    [InlineData("100.00", "GBP", "12.800000", "1280.00")]
    [InlineData("1.00", "USD", "2.345000", "2.35")] // half-up: banker's rounding would give 2.34
    [InlineData("-1.00", "USD", "2.345000", "-2.35")]
    [InlineData("0.01", "USD", "0.400000", "0.00")]
    public void Ac2_Amount_is_converted_with_the_rate_and_rounded_half_up_to_two_decimals(string amount, string currency, string rate, string expected)
    {
        var json = Serialized(Money.Convert(new MoneyInput(Dec(amount), currency), Dec(rate), November));

        Assert.Equal(amount, json.GetProperty("amount").GetString());
        Assert.Equal(expected, json.GetProperty("amountSEK").GetString());
    }

    [Fact]
    public void Ac2_A_converted_amount_too_large_for_the_column_is_a_422()
    {
        var problem = Assert.Throws<ProblemException>(
            () => Money.Convert(new MoneyInput(9_999_999_999_999_999.99m, "USD"), 10m, November));

        Assert.Equal(422, problem.Status);
        Assert.Contains(problem.Errors!, e => e.Field == "amount");
    }

    [DbFact]
    public async Task Ac2_Stamping_uses_the_rate_of_the_month_of_the_business_date()
    {
        await SeedCurrenciesAsync("USD", "EUR");
        await AddRateAsync("USD", new DateOnly(2026, 10, 1), 9m);
        await AddRateAsync("USD", November, 10m);
        await AddRateAsync("EUR", November, 11m);
        await using var db = fixture.CreateContext();

        var usd = await ExchangeRates.StampAsync(db, new MoneyInput(100m, "USD"), new DateOnly(2026, 11, 30));
        var eur = await ExchangeRates.StampAsync(db, new MoneyInput(100m, "EUR"), new DateOnly(2026, 11, 17));
        var earlier = await ExchangeRates.StampAsync(db, new MoneyInput(100m, "USD"), new DateOnly(2026, 10, 31));

        Assert.Equal("1000.00", Serialized(usd).GetProperty("amountSEK").GetString());
        Assert.Equal("1100.00", Serialized(eur).GetProperty("amountSEK").GetString());
        Assert.Equal("2026-11-01", Serialized(usd).GetProperty("rateDate").GetString());
        Assert.Equal("900.00", Serialized(earlier).GetProperty("amountSEK").GetString());
        Assert.Equal("2026-10-01", Serialized(earlier).GetProperty("rateDate").GetString());
    }

    // ---- AC 3: SEK passthrough ----

    [Fact]
    public void Ac3_SEK_amount_has_amountSEK_equal_to_amount()
    {
        var json = Serialized(Money.Convert(new MoneyInput(300000m, "SEK"), 1m, November));

        Assert.Equal("300000.00", json.GetProperty("amountSEK").GetString());
        Assert.Equal(json.GetProperty("amount").GetString(), json.GetProperty("amountSEK").GetString());
    }

    [DbFact]
    public async Task Ac3_SEK_needs_no_rate_row_and_its_rate_date_is_the_month_of_the_business_date()
    {
        await using var db = fixture.CreateContext();

        var money = await ExchangeRates.StampAsync(db, new MoneyInput(42.50m, "SEK"), new DateOnly(2026, 11, 17));

        Assert.Equal("42.50", Serialized(money).GetProperty("amountSEK").GetString());
        Assert.Equal("2026-11-01", Serialized(money).GetProperty("rateDate").GetString());
    }

    // ---- AC 4: missing rate ----

    [DbFact]
    public async Task Ac4_A_currency_without_a_rate_for_the_month_is_a_422_naming_the_currency_and_month()
    {
        await using var db = fixture.CreateContext();

        var problem = await Assert.ThrowsAsync<ProblemException>(
            () => ExchangeRates.StampAsync(db, new MoneyInput(100m, "NOK"), new DateOnly(2026, 11, 17)));

        Assert.Equal(422, problem.Status);
        Assert.Equal("MISSING_EXCHANGE_RATE", problem.Code);
        Assert.Contains("NOK", problem.Detail);
        Assert.Contains("2026-11", problem.Detail);
        Assert.Equal("currency", Assert.Single(problem.Errors!).Field);
    }

    [DbFact]
    public async Task Ac4_A_rate_for_another_month_is_not_used_as_a_fallback()
    {
        await SeedCurrenciesAsync("NOK");
        await AddRateAsync("NOK", new DateOnly(2026, 10, 1), 0.95m);
        await using var db = fixture.CreateContext();

        var problem = await Assert.ThrowsAsync<ProblemException>(
            () => ExchangeRates.StampAsync(db, new MoneyInput(100m, "NOK"), new DateOnly(2026, 11, 1)));

        Assert.Equal("MISSING_EXCHANGE_RATE", problem.Code);
    }

    // ---- exchange-rate endpoints ----

    [DbFact]
    public async Task Admin_sets_a_rate_then_changes_it_and_the_list_and_stamping_use_it()
    {
        await SeedCurrenciesAsync("USD");
        var admin = await SeedUserAsync("Admin");
        var sales = await SeedUserAsync("Salesperson");
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var created = await client.SendAsync(Send(HttpMethod.Put, $"{Url}/USD/2026-11", admin, """{"rateToSek":"10.5"}"""));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await ReadJson(created);
        Assert.Equal("USD", body.GetProperty("currency").GetString());
        Assert.Equal("2026-11-01", body.GetProperty("month").GetString());
        Assert.Equal("10.500000", body.GetProperty("rateToSek").GetString());

        var updated = await client.SendAsync(Send(HttpMethod.Put, $"{Url}/usd/2026-11", admin, """{"rateToSek":10}"""));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var list = await ReadJson(await client.SendAsync(Send(HttpMethod.Get, $"{Url}?filter[currency][eq]=USD", sales)));
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal("10.000000", item.GetProperty("rateToSek").GetString());

        await using var db = fixture.CreateContext();
        var money = await ExchangeRates.StampAsync(db, new MoneyInput(100m, "USD"), new DateOnly(2026, 11, 5));
        Assert.Equal("1000.00", Serialized(money).GetProperty("amountSEK").GetString());
    }

    [DbFact]
    public async Task A_rate_that_is_not_valid_is_a_422_naming_the_field_and_changes_nothing()
    {
        await SeedCurrenciesAsync("USD");
        var admin = await SeedUserAsync("Admin");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var cases = new (string Path, string Body, string Field)[]
        {
            ("SEK/2026-11", """{"rateToSek":"1"}""", "currency"),
            ("XXX/2026-11", """{"rateToSek":"1"}""", "currency"),
            ("USD/2026-13", """{"rateToSek":"1"}""", "month"),
            ("USD/2026-11-15", """{"rateToSek":"1"}""", "month"),
            ("USD/2026-11", """{"rateToSek":"0"}""", "rateToSek"),
            ("USD/2026-11", """{"rateToSek":-1}""", "rateToSek"),
            ("USD/2026-11", """{"rateToSek":"1.0000001"}""", "rateToSek"),
            ("USD/2026-11", """{"rateToSek":"abc"}""", "rateToSek"),
            ("USD/2026-11", """{"rateToSek":1000000000000}""", "rateToSek"),
            ("USD/2026-11", "{}", "rateToSek"),
        };

        foreach (var (path, body, field) in cases)
        {
            var response = await client.SendAsync(Send(HttpMethod.Put, $"{Url}/{path}", admin, body));

            Assert.Equal((HttpStatusCode)422, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await ReadJson(response);
            Assert.Contains(problem.GetProperty("errors").EnumerateArray(), e => e.GetProperty("field").GetString() == field);
        }

        Assert.Equal(0, await RateCountAsync());
    }

    [DbFact]
    public async Task Only_a_signed_in_admin_can_set_a_rate_and_only_a_signed_in_user_can_read_them()
    {
        await SeedCurrenciesAsync("USD");
        var sales = await SeedUserAsync("Salesperson");
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        const string body = """{"rateToSek":"10"}""";

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Send(HttpMethod.Put, $"{Url}/USD/2026-11", null, body))).StatusCode);
        var notAdmin = await client.SendAsync(Send(HttpMethod.Put, $"{Url}/USD/2026-11", sales, body));
        Assert.Equal(HttpStatusCode.Forbidden, notAdmin.StatusCode);
        Assert.Equal("application/problem+json", notAdmin.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, await RateCountAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Send(HttpMethod.Get, Url, null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Send(HttpMethod.Get, Url, sales))).StatusCode);
    }

    [DbFact]
    public void The_rate_PUT_of_the_real_app_calls_the_write_hooks()
    {
        using var factory = new WebApplicationFactory<Program>();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        var put = Assert.Single(endpoints, e => e.RoutePattern.RawText == $"{Url}/{{currency}}/{{month}}");

        Assert.NotNull(put.Metadata.GetMetadata<WriteHooksMarker>());
    }

    // ---- table rules ----

    private async Task<string> SqlStateOfAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        return error.SqlState;
    }

    [DbFact]
    public async Task The_table_refuses_SEK_a_mid_month_date_a_rate_of_zero_an_unknown_currency_and_duplicates()
    {
        await SeedCurrenciesAsync("SEK", "USD");
        const string insert = "insert into exchange_rate (currency, month, rate_to_sek) values ";

        Assert.Equal("23514", await SqlStateOfAsync(insert + "('SEK', '2026-11-01', 1)"));
        Assert.Equal("23514", await SqlStateOfAsync(insert + "('USD', '2026-11-15', 10)"));
        Assert.Equal("23514", await SqlStateOfAsync(insert + "('USD', '2026-11-01', 0)"));
        Assert.Equal("23503", await SqlStateOfAsync(insert + "('XXX', '2026-11-01', 10)"));

        await AddRateAsync("USD", November, 10m);
        Assert.Equal("23505", await SqlStateOfAsync(insert + "('USD', '2026-11-01', 10)"));
    }

    [DbFact]
    public async Task Respawn_still_resets_the_database_when_exchange_rate_rows_reference_currency_rows()
    {
        await SeedCurrenciesAsync("USD");
        await AddRateAsync("USD", November, 10m);

        await fixture.ResetAsync();

        Assert.Equal(0, await RateCountAsync());
        await using var db = fixture.CreateContext();
        Assert.Equal(0, await db.Database.SqlQuery<long>($"select count(*) as \"Value\" from currency").SingleAsync());
    }
}
