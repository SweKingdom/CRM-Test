using Zephyr.Api.Data;
using Zephyr.Api.Permissions;

namespace Zephyr.Api.Common;

/// <summary>Wires the API conventions (US-133) into the service collection and the request pipeline.</summary>
public static class ApiConventions
{
    public static IServiceCollection AddApiConventions(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddScoped<AuditInterceptor>();
        services.AddDbContext<ZephyrDbContext>((sp, options) =>
            ZephyrDbContext.Configure(options, sp.GetRequiredService<AuditInterceptor>()));
        services.AddWriteHooks();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<BusinessClock>();
        services.AddPermissions();

        // Binding failures (bad route value, unreadable body) throw, so the global handler answers them as problems.
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        return services;
    }

    /// <summary>Call first: every error, including unexpected ones and bare 404/401/403, leaves as problem+json.</summary>
    public static IApplicationBuilder UseApiConventions(this IApplicationBuilder app)
    {
        app.UseExceptionHandler(errors => errors.Run(Problems.HandleExceptionAsync));
        app.UseStatusCodePages(Problems.WriteStatusAsync);
        return app;
    }
}
