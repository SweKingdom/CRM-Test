using Microsoft.EntityFrameworkCore;
using Zephyr.Api.Data;

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
builder.Services.AddScoped<AuditInterceptor>();
builder.Services.AddDbContext<ZephyrDbContext>((sp, options) =>
    ZephyrDbContext.Configure(options, sp.GetRequiredService<AuditInterceptor>()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<ZephyrDbContext>().Database.Migrate();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
