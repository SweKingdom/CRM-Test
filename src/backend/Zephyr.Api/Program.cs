using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Logs go to stdout as JSON lines; configuration comes from environment variables only.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "ConnectionStrings__Default is not set. Run 'node scripts/setup-env.mjs' to create .env.");

builder.Services.AddHealthChecks().AddNpgSql(connectionString);

var app = builder.Build();

// Anonymous. 200 {"status":"Healthy"} when the API is up and SELECT 1 succeeds, 503 otherwise.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = (context, report) =>
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new { status = report.Status.ToString() }));
    }
}).AllowAnonymous();

app.Run();
