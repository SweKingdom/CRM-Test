using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Zephyr.Api.Auth;
using Zephyr.Api.Common;
using Zephyr.Api.Finance;
using Zephyr.Api.Permissions;
using Zephyr.Api.Search;
using Zephyr.Api.Time;

namespace Zephyr.Api;

/// <summary>Every endpoint of the API except the <c>/auth/*</c> ones (<c>UseZephyrAuth</c>), in one place so a test can build the same route table.</summary>
public static class Endpoints
{
    public static WebApplication MapZephyrEndpoints(this WebApplication app)
    {
        app.MapUsersEndpoints();
        app.MapPermissionEndpoints();
        // Per-project time approvers and delegation (US-145).
        app.MapApprovers();

        // Anonymous. 200 {"status":"Healthy"} when the API is up and SELECT 1 succeeds, 503 otherwise.
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = (context, report) =>
            {
                context.Response.ContentType = "application/json";
                return context.Response.WriteAsync(JsonSerializer.Serialize(new { status = report.Status.ToString() }));
            }
        }).AllowAnonymous();

        app.MapExchangeRates();

        // Weekly time entries with validation rules (US-163).
        app.MapTimeEntries();

        // Global search (US-147).
        app.MapSearch();

        // By-external-id upsert and soft delete (US-135) for every entity in the registry.
        app.MapExternalUpsert(EntityRegistry.Accounts);
        app.MapSoftDelete(EntityRegistry.Accounts);
        return app;
    }
}
