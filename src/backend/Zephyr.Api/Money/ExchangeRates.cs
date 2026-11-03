using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zephyr.Api.Common;
using Zephyr.Api.Data;

namespace Zephyr.Api.Finance;

/// <summary>Rate lookup for stamping money (US-134) and the exchange-rate endpoints.</summary>
public static class ExchangeRates
{
    public const string BaseCurrency = "SEK";

    /// <summary>Role key that may edit rates. Sign-in (US-140) and permissions (FT-112) put it on the principal.</summary>
    public const string AdminRole = "Admin";

    private const string Url = "/api/v1/exchange-rates";

    /// <summary>numeric(18,6) holds 12 integer digits.</summary>
    private const decimal MaxRate = 999_999_999_999m;

    private static readonly ListSpec<ExchangeRate> Spec = new("currency", "month", "updatedAt");

    private static string MonthText(DateOnly month) => month.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>
    /// Stamps <paramref name="input"/> with the rate of the month of <paramref name="businessDate"/> (close date, time-entry date...).
    /// SEK needs no row (rate 1). A missing rate is a 422 <c>MISSING_EXCHANGE_RATE</c>; no other month is used instead.
    /// </summary>
    public static async Task<Money> StampAsync(ZephyrDbContext db, MoneyInput input, DateOnly businessDate, CancellationToken ct = default)
    {
        var month = new DateOnly(businessDate.Year, businessDate.Month, 1);
        if (input.Currency == BaseCurrency)
        {
            return Money.Convert(input, 1m, month);
        }

        var rate = await db.ExchangeRates.AsNoTracking()
            .Where(r => r.Currency == input.Currency && r.Month == month)
            .Select(r => (decimal?)r.RateToSek)
            .SingleOrDefaultAsync(ct);
        if (rate is null)
        {
            var message = $"There is no exchange rate for {input.Currency} in {MonthText(month)}. " +
                $"An admin can add it with PUT {Url}/{input.Currency}/{MonthText(month)}.";
            throw new ProblemException(
                StatusCodes.Status422UnprocessableEntity, ErrorCodes.MissingExchangeRate, "Missing exchange rate", message,
                [new FieldError("currency", ErrorCodes.MissingExchangeRate, message)]);
        }

        return Money.Convert(input, rate.Value, month);
    }

    private static object Dto(ExchangeRate row) => new
    {
        row.Id,
        row.Currency,
        Month = row.Month.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        RateToSek = row.RateToSek.ToString("F6", CultureInfo.InvariantCulture),
        row.UpdatedAt
    };

    /// <summary>401 when nobody is signed in, 403 when the user is not an admin and the action needs one; otherwise null.</summary>
    private static int? DeniedStatus(HttpContext context, bool adminOnly)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return StatusCodes.Status401Unauthorized;
        }

        return adminOnly && !context.User.IsInRole(AdminRole) ? StatusCodes.Status403Forbidden : null;
    }

    /// <summary>Reads <c>rateToSek</c> (number or string, above 0, at most 6 decimals). Adds an error and returns 0 when invalid.</summary>
    private static decimal ReadRate(JsonElement body, ModelState state)
    {
        var rate = 0m;
        var ok = body.TryGetProperty("rateToSek", out var value) && value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDecimal(out rate),
            JsonValueKind.String => decimal.TryParse(value.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out rate),
            _ => false
        };
        if (!ok || rate <= 0 || rate > MaxRate || decimal.Round(rate, 6) != rate)
        {
            state.Add("rateToSek", ErrorCodes.Validation, "rateToSek must be a number above 0 with at most 6 decimals, for example \"10.50\".");
            return 0m;
        }

        return rate;
    }

    public static IEndpointRouteBuilder MapExchangeRates(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Url, async Task<IResult> (HttpContext context, ZephyrDbContext db) =>
        {
            if (DeniedStatus(context, adminOnly: false) is { } denied)
            {
                return Results.StatusCode(denied);
            }

            var query = ListQuery<ExchangeRate>.Parse(context.Request.Query, Spec);
            return Results.Ok((await query.ToPageAsync(db.ExchangeRates.AsNoTracking(), context.RequestAborted)).Select(Dto));
        });

        // Body {"rateToSek": ...}. 201 when the month had no rate yet, 200 when an existing rate was set. Admin only.
        routes.MapPut($"{Url}/{{currency}}/{{month}}", async Task<IResult> (string currency, string month, HttpContext context, ZephyrDbContext db) =>
        {
            if (DeniedStatus(context, adminOnly: true) is { } denied)
            {
                return Results.StatusCode(denied);
            }

            var ct = context.RequestAborted;
            var body = await WriteHooks.ReadBodyAsync(context.Request, ct);
            await WriteHooks.Run(context, "exchange_rate", body);

            var state = new ModelState();
            currency = currency.ToUpperInvariant();
            if (currency == BaseCurrency)
            {
                state.Add("currency", ErrorCodes.Validation, "SEK is the base currency and has no exchange rate: it is always 1.");
            }
            else if (!await db.Database.SqlQuery<string>($"select iso3 as \"Value\" from currency where iso3 = {currency} and active").AnyAsync(ct))
            {
                state.Add("currency", ErrorCodes.Validation, $"'{currency}' is not an active currency.");
            }

            if (!DateOnly.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
            {
                state.Add("month", ErrorCodes.Validation, "The month must look like 2026-11 (yyyy-MM).");
            }

            var rate = ReadRate(body, state);
            if (!state.IsValid)
            {
                throw Problems.Validation(state.Errors);
            }

            var row = await db.ExchangeRates.SingleOrDefaultAsync(r => r.Currency == currency && r.Month == first, ct);
            var created = row is null;
            if (row is null)
            {
                row = new ExchangeRate { Currency = currency, Month = first, RateToSek = rate };
                db.ExchangeRates.Add(row);
            }
            else
            {
                row.RateToSek = rate;
            }

            await db.SaveChangesAsync(ct);
            ETags.Set(context.Response, row);
            return created ? Results.Created($"{Url}/{currency}/{MonthText(first)}", Dto(row)) : Results.Ok(Dto(row));
        }).WithWriteHooks();

        return routes;
    }
}
