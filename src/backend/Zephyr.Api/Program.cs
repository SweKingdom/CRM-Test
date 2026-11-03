using Microsoft.EntityFrameworkCore;
using Zephyr.Api;
using Zephyr.Api.Auth;
using Zephyr.Api.Common;
using Zephyr.Api.Data;
using Zephyr.Api.Permissions;

// "import" mode: apply migrations and exit, no web host (the import itself is added by its own story).
if (args.Length > 0 && args[0] == "import")
{
    var options = new DbContextOptionsBuilder<ZephyrDbContext>();
    ZephyrDbContext.Configure(options, new AuditInterceptor());
    using var db = new ZephyrDbContext(options.Options);
    db.Database.Migrate();
    await PermissionSeeder.SeedAsync(db);
    Console.WriteLine("Database is up to date.");
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Logs go to stdout as JSON lines; configuration comes from environment variables only.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// API conventions (US-133) also register the audit interceptor, the DbContext and the permission engine (US-144).
builder.Services.AddApiConventions();
// Sign-in (US-140): OIDC providers from configuration, session cookie, default-deny authorisation. No CORS: the SPA is same-origin.
builder.AddZephyrAuth();
builder.Services.AddHealthChecks().AddNpgSql(_ => ZephyrDbContext.ConnectionStringFromEnvironment());

var app = builder.Build();
app.UseApiConventions();
app.UseZephyrAuth();
app.MapZephyrEndpoints();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ZephyrDbContext>();
    db.Database.Migrate();
    // The permission matrix (US-143): an invalid permission-matrix.csv stops the API from starting.
    await PermissionSeeder.SeedAsync(db);
}

// The engine keeps the matrix in memory: read it once the seed has run.
await app.Services.GetRequiredService<IPermissionService>().ReloadAsync();

app.Run();

/// <summary>Makes the entry point visible to the integration tests.</summary>
public partial class Program
{
}
