using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Zephyr.Api.Auth;
using Zephyr.Api.Common;
using Zephyr.Api.Data;
using Zephyr.Api.Finance;
using Zephyr.Api.Permissions;

// "import" mode: apply migrations and exit, no web host (the import itself is added by its own story).
if (args.Length > 0 && args[0] == "import")
{
    var options = new DbContextOptionsBuilder<ZephyrDbContext>();
    ZephyrDbContext.Configure(options, new AuditInterceptor());
    using var db = new ZephyrDbContext(options.Options);
    db.Database.Migrate();
    Console.WriteLine("Database is up to date.");
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Logs go to stdout as JSON lines; configuration comes from environment variables only.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// API conventions (US-133) also register the audit interceptor and the DbContext.
builder.Services.AddApiConventions();
// Sign-in (US-140): OIDC providers from configuration, session cookie, default-deny authorisation. No CORS: the SPA is same-origin.
builder.AddZephyrAuth();
builder.Services.AddHealthChecks().AddNpgSql(_ => ZephyrDbContext.ConnectionStringFromEnvironment());

var app = builder.Build();
app.UseApiConventions();
app.UseZephyrAuth();
app.MapUsersEndpoints();
app.MapApiTokenEndpoints();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<ZephyrDbContext>().Database.Migrate();
}

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

// By-external-id upsert and soft delete (US-135) for every entity in the registry.
app.MapExternalUpsert(EntityRegistry.Accounts);
app.MapSoftDelete(EntityRegistry.Accounts);

app.Run();

/// <summary>Makes the entry point visible to the integration tests.</summary>
public partial class Program
{
}
