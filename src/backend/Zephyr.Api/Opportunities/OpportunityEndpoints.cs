using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zephyr.Api.Common;
using Zephyr.Api.Data;
using Zephyr.Api.Finance;
using Zephyr.Api.Permissions;

namespace Zephyr.Api.Opportunities;

/// <summary>
/// Opportunities (US-156): CRUD with ETag, list filters, the FT-112 scope and <c>GET /api/v1/stages</c>. The opportunity stores only
/// <c>stage_key</c>, so renaming a stage label in <c>stage</c> changes no opportunity row. The stage and the close fields are not
/// writable here: <c>:change-stage</c> and <c>:close</c> (US-157, US-158) own them, and PATCH answers 422 for them.
/// </summary>
public static class OpportunityEndpoints
{
    private const string Entity = "opportunity";
    private const string Url = "/api/v1/opportunities";
    private const string Required = "REQUIRED";
    private const string Invalid = "INVALID";
    private const string UnknownKey = "UNKNOWN_KEY";
    private const int MaxText = 200;
    private const int MaxKey = 100;
    private const decimal MaxHourlyRate = 9_999_999_999_999_999.99m;
    private const decimal MaxEstHours = 9_999_999.99m;

    private static readonly string[] ForecastCategories = ["pipeline", "best_case", "commit", "omitted"];
    private static readonly string[] ClosedFields = ["closedOutcome", "closedReasonKey", "competitor", "closedAt"];
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly ListSpec<Opportunity> Spec = new("stageKey", "ownerUserId", "accountId", "expectedCloseDate", "closedAt", "updatedAt");

    private sealed class StageRow
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public int SortOrder { get; set; }
        public bool IsTerminal { get; set; }
        public string? Outcome { get; set; }
        public bool Active { get; set; }
    }

    public static IEndpointRouteBuilder MapOpportunities(this IEndpointRouteBuilder routes)
    {
        // Stages are reference data: every role that reads reference data gets the labels, ordered, so a renamed label shows at once.
        routes.MapGet("/api/v1/stages", async Task<IResult> (ZephyrDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Database.SqlQuery<StageRow>(
                $"""
                select key as "Key", label as "Label", sort_order as "SortOrder", is_terminal as "IsTerminal", outcome as "Outcome", active as "Active"
                from stage order by sort_order, key
                """).ToListAsync(ct))).RequirePermission("reference_data", "read");

        // The FT-112 read scope is part of the query, before the filters, the cursor and the limit.
        routes.MapGet(Url, async Task<IResult> (HttpContext context, ZephyrDbContext db, IPermissionService permissions) =>
        {
            var query = ListQuery<Opportunity>.Parse(context.Request.Query, Spec);
            var visible = permissions.Filter(db.Opportunities.AsNoTracking(), await context.CurrentUserAsync(), Entity);
            return Results.Ok((await query.ToPageAsync(visible, context.RequestAborted)).Select(Dto));
        }).RequirePermission(Entity, "read");

        // 404 for a record the caller may not see.
        routes.MapGet($"{Url}/{{id:guid}}", async Task<IResult> (Guid id, HttpContext context, ZephyrDbContext db, IPermissionService permissions) =>
        {
            var row = await permissions.FindAsync(
                db.Opportunities.AsNoTracking(), await context.CurrentUserAsync(), Entity, "read", o => o.Id == id, context.RequestAborted);
            return ETags.Ok(context, row, Dto(row));
        }).RequirePermission(Entity, "read");

        // stageKey and accountId are required; the owner defaults to the caller.
        routes.MapPost(Url, async Task<IResult> (HttpContext context, ZephyrDbContext db, IPermissionService permissions) =>
        {
            var ct = context.RequestAborted;
            var body = await WriteHooks.ReadBodyAsync(context.Request, ct);
            await WriteHooks.Run(context, Entity, body);
            var user = await context.CurrentUserAsync();
            var row = new Opportunity { OwnerUserId = user.Id };
            var state = new ModelState();
            await ApplyAsync(row, body, true, db, state, ct);
            if (!state.IsValid)
            {
                throw Problems.Validation(state.Errors);
            }

            if (!permissions.Can(user, Entity, "create", row))
            {
                throw Problems.Forbidden();
            }

            db.Opportunities.Add(row);
            await db.SaveChangesAsync(ct);
            ETags.Set(context.Response, row);
            return Results.Created($"{Url}/{row.Id}", Dto(row));
        }).RequirePermission(Entity, "create").WithWriteHooks();

        // Merge-patch: an absent field stays, null clears an optional one. 404, then 428/412 on If-Match, then 422.
        routes.MapPatch($"{Url}/{{id:guid}}", async Task<IResult> (Guid id, HttpContext context, ZephyrDbContext db, IPermissionService permissions) =>
        {
            var ct = context.RequestAborted;
            var body = await WriteHooks.ReadBodyAsync(context.Request, ct);
            var user = await context.CurrentUserAsync();
            var row = await permissions.FindAsync(db.Opportunities, user, Entity, "update", o => o.Id == id, ct);
            ETags.RequireMatch(context.Request, row);
            await WriteHooks.Run(context, Entity, body);
            var state = new ModelState();
            await ApplyAsync(row, body, false, db, state, ct);
            if (!state.IsValid)
            {
                throw Problems.Validation(state.Errors);
            }

            // An owner change must not take the record out of the caller's own scope.
            if (!permissions.Can(user, Entity, "update", row))
            {
                throw Problems.Forbidden();
            }

            await db.SaveChangesAsync(ct);
            ETags.Set(context.Response, row);
            return Results.Ok(Dto(row));
        }).RequirePermission(Entity, "update").WithWriteHooks();

        // The matrix defines no delete cell for opportunities yet, so, like accounts, only an Admin deletes (soft delete, 204).
        routes.MapDelete($"{Url}/{{id:guid}}", async Task<IResult> (Guid id, HttpContext context, ZephyrDbContext db) =>
        {
            var row = await db.Opportunities.SingleOrDefaultAsync(o => o.Id == id, context.RequestAborted);
            if (row is null)
            {
                return Results.NotFound();
            }

            db.Opportunities.Remove(row);
            await db.SaveChangesAsync(context.RequestAborted);
            return Results.NoContent();
        }).RequireRoles(ExternalUpsert.DeleteRoles);

        return routes;
    }

    private static object Dto(Opportunity o) => new
    {
        o.Id,
        o.Name,
        o.AccountId,
        o.StageKey,
        Amount = o.Amount is { } amount
            ? new { Amount = amount.ToString("F2", Inv), o.Currency, AmountSEK = o.AmountSek?.ToString("F2", Inv), o.RateDate }
            : null,
        HourlyRate = o.HourlyRate?.ToString("F2", Inv),
        EstHours = o.EstHours?.ToString("F2", Inv),
        o.ContractingCountryKey,
        o.NextStep,
        o.NextStepDate,
        o.LeadSourceKey,
        o.OwnerUserId,
        o.ForecastCategory,
        o.ExpectedCloseDate,
        o.ClosedOutcome,
        o.ClosedReasonKey,
        o.Competitor,
        o.ClosedAt,
        o.CustomPlatform,
        o.DeliveryTeam,
        o.ProposedPo,
        o.CreatedAt,
        o.UpdatedAt
    };

    /// <summary>
    /// Reads the body into <paramref name="row"/>, adding one error per invalid field to <paramref name="state"/>. Keys must exist
    /// (checked in the database); the stage and the close fields are refused on PATCH, the close fields on POST as well.
    /// </summary>
    private static async Task ApplyAsync(Opportunity row, JsonElement body, bool create, ZephyrDbContext db, ModelState state, CancellationToken ct)
    {
        Task<bool> Exists(FormattableString sql) => db.Database.SqlQuery<bool>(sql).SingleAsync(ct);

        void RequiredOnCreate(string field)
        {
            if (create && !body.TryGetProperty(field, out _))
            {
                state.Add(field, Required, $"{field} is required.");
            }
        }

        if (TryText(body, "name", MaxText, false, state, out var name))
        {
            row.Name = name!;
        }
        else
        {
            RequiredOnCreate("name");
        }

        if (TryGuid(body, "accountId", state, out var accountId))
        {
            if (await Exists($"select exists (select 1 from account where id = {accountId} and deleted_at is null) as \"Value\""))
            {
                row.AccountId = accountId;
            }
            else
            {
                state.Add("accountId", UnknownKey, "No account has this id. Choose an existing account.");
            }
        }
        else
        {
            RequiredOnCreate("accountId");
        }

        if (!create && body.TryGetProperty("stageKey", out _))
        {
            state.Add("stageKey", ErrorCodes.NotPatchable, "The stage cannot be changed with PATCH. Move the opportunity with POST /api/v1/opportunities/{id}:change-stage.");
        }
        else if (TryText(body, "stageKey", MaxKey, false, state, out var stage))
        {
            if (await Exists($"select exists (select 1 from stage where key = {stage} and active) as \"Value\""))
            {
                row.StageKey = stage!;
            }
            else
            {
                state.Add("stageKey", UnknownKey, $"'{stage}' is not a stage. Read the stage keys from GET /api/v1/stages.");
            }
        }
        else
        {
            RequiredOnCreate("stageKey");
        }

        foreach (var field in ClosedFields.Where(field => body.TryGetProperty(field, out _)))
        {
            state.Add(field, ErrorCodes.NotPatchable, $"{field} cannot be set here. Close the opportunity with POST /api/v1/opportunities/{{id}}:close.");
        }

        if (TryGuid(body, "ownerUserId", state, out var owner))
        {
            if (await Exists($"select exists (select 1 from app_user where id = {owner} and active and deleted_at is null) as \"Value\""))
            {
                row.OwnerUserId = owner;
            }
            else
            {
                state.Add("ownerUserId", UnknownKey, "No active user has this id. Choose an active user as owner.");
            }
        }

        if (TryText(body, "contractingCountryKey", MaxKey, true, state, out var country))
        {
            if (country is null || await Exists($"select exists (select 1 from country where key = {country} and active) as \"Value\""))
            {
                row.ContractingCountryKey = country;
            }
            else
            {
                state.Add("contractingCountryKey", UnknownKey, $"'{country}' is not a country key, for example SE.");
            }
        }

        if (TryText(body, "leadSourceKey", MaxKey, true, state, out var source))
        {
            if (source is null || await Exists($"select exists (select 1 from lead_source where key = {source} and active) as \"Value\""))
            {
                row.LeadSourceKey = source;
            }
            else
            {
                state.Add("leadSourceKey", UnknownKey, $"'{source}' is not a lead source.");
            }
        }

        if (TryText(body, "forecastCategory", MaxKey, true, state, out var forecast))
        {
            if (forecast is null || ForecastCategories.Contains(forecast))
            {
                row.ForecastCategory = forecast;
            }
            else
            {
                state.Add("forecastCategory", Invalid, $"forecastCategory must be one of: {string.Join(", ", ForecastCategories)}.");
            }
        }

        // The SEK amount and rate date are stamped when the deal closes (US-158), never taken from the client.
        if (body.TryGetProperty("amount", out var amountElement))
        {
            if (amountElement.ValueKind == JsonValueKind.Null)
            {
                row.Amount = null;
                row.Currency = null;
            }
            else if (MoneyInput.Read(amountElement, "amount", state) is { } money)
            {
                if (await Exists($"select exists (select 1 from currency where iso3 = {money.Currency} and active) as \"Value\""))
                {
                    row.Amount = money.Amount;
                    row.Currency = money.Currency;
                }
                else
                {
                    state.Add("amount.currency", Invalid, $"'{money.Currency}' is not an active currency.");
                }
            }
        }

        if (TryNumber(body, "hourlyRate", MaxHourlyRate, state, out var rate))
        {
            row.HourlyRate = rate;
        }

        if (TryNumber(body, "estHours", MaxEstHours, state, out var hours))
        {
            row.EstHours = hours;
        }

        if (TryText(body, "nextStep", MaxText, true, state, out var nextStep))
        {
            row.NextStep = nextStep;
        }

        if (TryDate(body, "nextStepDate", state, out var nextStepDate))
        {
            row.NextStepDate = nextStepDate;
        }

        if (TryDate(body, "expectedCloseDate", state, out var closeDate))
        {
            row.ExpectedCloseDate = closeDate;
        }

        if (TryText(body, "customPlatform", MaxText, true, state, out var platform))
        {
            row.CustomPlatform = platform;
        }

        if (TryText(body, "deliveryTeam", MaxText, true, state, out var team))
        {
            row.DeliveryTeam = team;
        }

        if (TryText(body, "proposedPo", MaxText, true, state, out var po))
        {
            row.ProposedPo = po;
        }
    }

    /// <summary>True when <paramref name="field"/> is present and valid. Empty text (or null) clears a <paramref name="nullable"/> field and is an error otherwise.</summary>
    private static bool TryText(JsonElement body, string field, int max, bool nullable, ModelState state, out string? value)
    {
        value = null;
        if (!body.TryGetProperty(field, out var element))
        {
            return false;
        }

        var text = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()!.Trim(),
            JsonValueKind.Null => "",
            _ => null
        };
        if (text is null)
        {
            state.Add(field, Invalid, $"{field} must be text.");
        }
        else if (text.Length == 0)
        {
            if (nullable)
            {
                return true;
            }

            state.Add(field, Required, $"{field} is required and cannot be empty.");
        }
        else if (text.Length > max)
        {
            state.Add(field, Invalid, $"{field} can be at most {max} characters.");
        }
        else
        {
            value = text;
            return true;
        }

        return false;
    }

    private static bool TryGuid(JsonElement body, string field, ModelState state, out Guid value)
    {
        value = default;
        if (!body.TryGetProperty(field, out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out value))
        {
            return true;
        }

        state.Add(field, element.ValueKind == JsonValueKind.Null ? Required : Invalid, $"{field} must be an id, for example 3f2b8c1e-0000-4000-8000-000000000001.");
        return false;
    }

    /// <summary>A date written yyyy-MM-dd; null clears.</summary>
    private static bool TryDate(JsonElement body, string field, ModelState state, out DateOnly? value)
    {
        value = null;
        if (!body.TryGetProperty(field, out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(element.GetString(), "yyyy-MM-dd", Inv, DateTimeStyles.None, out var date))
        {
            value = date;
            return true;
        }

        state.Add(field, Invalid, $"{field} must be a date written yyyy-MM-dd, for example 2026-12-24.");
        return false;
    }

    /// <summary>A number or numeric text from 0 to <paramref name="max"/> with at most 2 decimals; null clears.</summary>
    private static bool TryNumber(JsonElement body, string field, decimal max, ModelState state, out decimal? value)
    {
        value = null;
        if (!body.TryGetProperty(field, out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        var number = 0m;
        var ok = element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDecimal(out number),
            JsonValueKind.String => decimal.TryParse(element.GetString(), NumberStyles.AllowDecimalPoint, Inv, out number),
            _ => false
        };
        if (ok && number >= 0 && number <= max && decimal.Round(number, 2) == number)
        {
            value = number;
            return true;
        }

        state.Add(field, Invalid, $"{field} must be a number of 0 or more with at most 2 decimals, for example \"1200.00\".");
        return false;
    }
}
