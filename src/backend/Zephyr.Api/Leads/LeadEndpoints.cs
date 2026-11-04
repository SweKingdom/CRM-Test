using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zephyr.Api.Common;
using Zephyr.Api.Data;
using Zephyr.Api.Permissions;

namespace Zephyr.Api.Leads;

/// <summary>
/// Leads (US-153): <c>/api/v1/leads</c> list, read, create, merge-patch and soft delete. Reads and updates follow the read and
/// update scopes of the permission matrix, applied in the query (a lead out of scope is a 404). <c>callTries</c>,
/// <c>lastCallOutcome</c> and <c>status</c> belong to the call (US-154) and conversion (US-155) stories: a client that sends one is
/// refused with a 422.
/// </summary>
public static class LeadEndpoints
{
    public const string Url = "/api/v1/leads";

    private const string Entity = "lead";

    private static readonly ListSpec<Lead> Spec = new("status", "ownerUserId", "leadSource", "updatedAt");

    private static readonly string[] ServerOnly = ["callTries", "lastCallOutcome", "status"];

    private static readonly string[] RequiredOnCreate = ["lastName", "companyName", "leadSource"];

    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant);

    /// <summary>A plain text field: how it is stored, its longest value and an optional format check on a non-empty value.</summary>
    private sealed record TextField(string Name, Action<Lead, string?> Set, int MaxLength, bool Required = false, Func<string, string?>? Check = null);

    private static readonly TextField[] TextFields =
    [
        new("firstName", (l, v) => l.FirstName = v, 100),
        new("lastName", (l, v) => l.LastName = v ?? "", 100, Required: true),
        new("companyName", (l, v) => l.CompanyName = v ?? "", 200, Required: true),
        new("jobTitle", (l, v) => l.JobTitle = v, 100),
        new("phone", (l, v) => l.Phone = v, 50),
        new("mobile", (l, v) => l.Mobile = v, 50),
        new("email", (l, v) => l.Email = v, 254, Check: v => EmailPattern.IsMatch(v) ? null : "email must look like name@example.com."),
        new("website", (l, v) => l.Website = v, 2048, Check: v => IsWebAddress(v) ? null : "website must be a web address starting with http:// or https://."),
        new("nextStep", (l, v) => l.NextStep = v, 500)
    ];

    private static bool IsWebAddress(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0;

    private static object Dto(Lead l) => new
    {
        l.Id,
        l.FirstName,
        l.LastName,
        l.CompanyName,
        l.JobTitle,
        l.Phone,
        l.Mobile,
        l.Email,
        l.Website,
        l.LeadSource,
        l.CountryCode,
        l.CallTries,
        l.LastCallOutcome,
        l.NextStep,
        l.NextStepDate,
        l.AccountId,
        l.OwnerUserId,
        l.Status,
        l.DisqualifyReason,
        l.ConvertedContactId,
        l.ConvertedOpportunityId,
        l.CreatedAt,
        l.UpdatedAt
    };

    /// <summary>The audit entry of a write: who, what, and the names (never the values) of the fields that were sent.</summary>
    private static AuditLog Audit(Guid? userId, string action, Guid id, IEnumerable<string> fields) => new()
    {
        UserId = userId,
        Action = action,
        EntityType = Entity,
        EntityId = id,
        Detail = JsonSerializer.Serialize(new { fields })
    };

    private static string[] FieldNames(JsonElement body) => body.EnumerateObject().Select(p => p.Name).ToArray();

    private static Task<bool> ExistsAsync(ZephyrDbContext db, FormattableString sql, CancellationToken ct) =>
        db.Database.SqlQuery<string>(sql).AnyAsync(ct);

    /// <summary>Trimmed text of a JSON string (empty becomes null, null stays null), or the reason the value is not valid text.</summary>
    private static string? ReadText(JsonElement value, string name, int maxLength, out string? text)
    {
        text = null;
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return $"{name} must be text.";
        }

        var trimmed = value.GetString()!.Trim();
        if (trimmed.Length > maxLength)
        {
            return $"{name} can be at most {maxLength} characters.";
        }

        text = trimmed.Length == 0 ? null : trimmed;
        return null;
    }

    private static void ApplyText(Lead lead, TextField field, JsonElement value, ModelState state)
    {
        var error = ReadText(value, field.Name, field.MaxLength, out var text);
        if (error is not null)
        {
            state.Add(field.Name, "INVALID", error);
        }
        else if (text is null && field.Required)
        {
            state.Add(field.Name, "REQUIRED", $"{field.Name} is required.");
        }
        else if (text is not null && field.Check?.Invoke(text) is { } invalid)
        {
            state.Add(field.Name, "INVALID", invalid);
        }
        else
        {
            field.Set(lead, text);
        }
    }

    private static async Task ApplyLeadSourceAsync(Lead lead, JsonElement value, ModelState state, ZephyrDbContext db, CancellationToken ct)
    {
        const string name = "leadSource";
        var error = ReadText(value, name, 100, out var source);
        if (error is not null)
        {
            state.Add(name, "INVALID", error);
        }
        else if (source is null)
        {
            state.Add(name, "REQUIRED", "leadSource is required. Choose one of the active lead sources.");
        }
        else if (!await ExistsAsync(db, $"select key as \"Value\" from lead_source where key = {source} and active", ct))
        {
            state.Add(name, "INVALID", $"'{source}' is not an active lead source. Choose one from the list.");
        }
        else
        {
            lead.LeadSource = source;
        }
    }

    private static async Task ApplyCountryAsync(Lead lead, JsonElement value, ModelState state, ZephyrDbContext db, CancellationToken ct)
    {
        const string name = "countryCode";
        if (value.ValueKind == JsonValueKind.Null)
        {
            lead.CountryCode = null;
        }
        else if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: 2 } code && code.All(char.IsAsciiLetterUpper))
        {
            if (await ExistsAsync(db, $"select iso2 as \"Value\" from country where iso2 = {code} and active", ct))
            {
                lead.CountryCode = code;
            }
            else
            {
                state.Add(name, "INVALID", $"'{code}' is not a known country.");
            }
        }
        else
        {
            state.Add(name, "INVALID", "countryCode must be two capital letters, for example SE.");
        }
    }

    private static async Task ApplyAccountAsync(Lead lead, JsonElement value, ModelState state, ZephyrDbContext db, CancellationToken ct)
    {
        const string name = "accountId";
        if (value.ValueKind == JsonValueKind.Null)
        {
            lead.AccountId = null;
        }
        else if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var accountId))
        {
            if (await db.Accounts.AnyAsync(a => a.Id == accountId, ct))
            {
                lead.AccountId = accountId;
            }
            else
            {
                state.Add(name, "INVALID", "The account was not found.");
            }
        }
        else
        {
            state.Add(name, "INVALID", "accountId must be the id of an account, or null.");
        }
    }

    private static void ApplyNextStepDate(Lead lead, JsonElement value, ModelState state)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            lead.NextStepDate = null;
        }
        else if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment))
        {
            lead.NextStepDate = moment.UtcDateTime;
        }
        else
        {
            state.Add("nextStepDate", "INVALID", "nextStepDate must be a date and time, for example 2026-11-05T09:00:00Z.");
        }
    }

    /// <summary>
    /// Applies the JSON body to <paramref name="lead"/> field by field. A missing field is left alone (merge-patch); on create the
    /// required fields must be present. Picklist, country and account values are checked against the database. Every problem is
    /// added to the returned state, nothing is thrown, so the caller reports all of them at once.
    /// </summary>
    private static async Task<ModelState> ApplyAsync(Lead lead, JsonElement body, bool create, ZephyrDbContext db, CancellationToken ct)
    {
        var state = new ModelState();
        foreach (var property in body.EnumerateObject())
        {
            var name = property.Name;
            var value = property.Value;
            if (TextFields.FirstOrDefault(f => f.Name == name) is { } text)
            {
                ApplyText(lead, text, value, state);
            }
            else if (name == "leadSource")
            {
                await ApplyLeadSourceAsync(lead, value, state, db, ct);
            }
            else if (name == "countryCode")
            {
                await ApplyCountryAsync(lead, value, state, db, ct);
            }
            else if (name == "accountId")
            {
                await ApplyAccountAsync(lead, value, state, db, ct);
            }
            else if (name == "nextStepDate")
            {
                ApplyNextStepDate(lead, value, state);
            }
            else
            {
                state.Add(
                    name,
                    ErrorCodes.NotWritable,
                    ServerOnly.Contains(name) ? $"{name} is set by the server and cannot be sent." : $"{name} is not a field you can set on a lead.");
            }
        }

        if (create)
        {
            foreach (var required in RequiredOnCreate.Where(r => !body.TryGetProperty(r, out _)))
            {
                state.Add(required, "REQUIRED", $"{required} is required.");
            }
        }

        return state;
    }

    public static IEndpointRouteBuilder MapLeadEndpoints(this IEndpointRouteBuilder routes)
    {
        // Keyset paging, filter[status|ownerUserId|leadSource|updatedAt][op] and sort; only leads the caller may read.
        routes.MapGet(Url, async Task<IResult> (HttpContext context, ZephyrDbContext db, IPermissionService permissions) =>
        {
            var user = await context.CurrentUserAsync();
            var query = ListQuery<Lead>.Parse(context.Request.Query, Spec);
            var visible = permissions.Filter(db.Leads.AsNoTracking(), user, Entity);
            return Results.Ok((await query.ToPageAsync(visible, context.RequestAborted)).Select(Dto));
        }).RequirePermission(Entity, "read");

        // 404 for a lead that does not exist, is deleted or is out of the caller's scope.
        routes.MapGet($"{Url}/{{id:guid}}", async Task<IResult> (Guid id, HttpContext context, ZephyrDbContext db, IPermissionService permissions) =>
        {
            var user = await context.CurrentUserAsync();
            var lead = await permissions.FindAsync(db.Leads.AsNoTracking(), user, Entity, "read", l => l.Id == id, context.RequestAborted);
            return ETags.Ok(context, lead, Dto(lead));
        }).RequirePermission(Entity, "read");

        // 201 with Location and ETag. The owner is the caller. 422 lists every invalid field.
        routes.MapPost(Url, async Task<IResult> (HttpContext context, ZephyrDbContext db) =>
        {
            var ct = context.RequestAborted;
            var body = await WriteHooks.ReadBodyAsync(context.Request, ct);
            await WriteHooks.Run(context, Entity, body);
            var user = await context.CurrentUserAsync();
            var lead = new Lead { Id = Guid.NewGuid(), OwnerUserId = user.Id };
            var state = await ApplyAsync(lead, body, true, db, ct);
            if (!state.IsValid)
            {
                throw Problems.Validation(state.Errors);
            }

            db.Leads.Add(lead);
            db.AuditLogs.Add(Audit(user.Id, "lead_created", lead.Id, FieldNames(body)));
            await db.SaveChangesAsync(ct);
            ETags.Set(context.Response, lead);
            return Results.Created($"{Url}/{lead.Id}", Dto(lead));
        }).RequirePermission(Entity, "create").WithWriteHooks();

        // Merge-patch with If-Match: 404 out of scope, 403 visible but not changeable, 428 without If-Match, 412 when stale.
        routes.MapPatch($"{Url}/{{id:guid}}", async Task<IResult> (Guid id, HttpContext context, ZephyrDbContext db, IPermissionService permissions) =>
        {
            var ct = context.RequestAborted;
            var body = await WriteHooks.ReadBodyAsync(context.Request, ct);
            var user = await context.CurrentUserAsync();
            var lead = await permissions.FindAsync(db.Leads, user, Entity, "update", l => l.Id == id, ct);
            ETags.RequireMatch(context.Request, lead);
            await WriteHooks.Run(context, Entity, body);
            var state = await ApplyAsync(lead, body, false, db, ct);
            if (!state.IsValid)
            {
                throw Problems.Validation(state.Errors);
            }

            db.AuditLogs.Add(Audit(user.Id, "lead_updated", lead.Id, FieldNames(body)));
            await db.SaveChangesAsync(ct);
            ETags.Set(context.Response, lead);
            return Results.Ok(Dto(lead));
        }).RequirePermission(Entity, "update").WithWriteHooks();

        // The matrix gives nobody a lead delete cell yet, so delete is Admin only, like every other entity (ExternalUpsert.DeleteRoles).
        routes.MapDelete($"{Url}/{{id:guid}}", async Task<IResult> (Guid id, HttpContext context, ZephyrDbContext db, ICurrentUser current) =>
        {
            var ct = context.RequestAborted;
            var lead = await db.Leads.SingleOrDefaultAsync(l => l.Id == id, ct) ?? throw Problems.NotFound();
            db.Leads.Remove(lead);
            db.AuditLogs.Add(Audit(current.UserId, "lead_deleted", id, []));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireRoles(ExternalUpsert.DeleteRoles);
        return routes;
    }
}
