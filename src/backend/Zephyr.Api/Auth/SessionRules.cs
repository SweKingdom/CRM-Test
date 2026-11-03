using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Zephyr.Api.Data;

namespace Zephyr.Api.Auth;

/// <summary>
/// Session limits and the user-status recheck, read from the environment (US-141):
/// SESSION_IDLE_SECONDS (default 14400), SESSION_ABSOLUTE_SECONDS (43200), USER_STATUS_RECHECK_SECONDS (300).
/// </summary>
public sealed record SessionSettings(TimeSpan Idle, TimeSpan Absolute, TimeSpan Recheck)
{
    public static SessionSettings From(IConfiguration configuration) => new(
        Seconds(configuration, "SESSION_IDLE_SECONDS", 4 * 3600),
        Seconds(configuration, "SESSION_ABSOLUTE_SECONDS", 12 * 3600),
        Seconds(configuration, "USER_STATUS_RECHECK_SECONDS", 300));

    private static TimeSpan Seconds(IConfiguration configuration, string name, int fallback)
    {
        var raw = configuration[name];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TimeSpan.FromSeconds(fallback);
        }

        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : throw new InvalidOperationException($"{name} must be a whole number of seconds greater than 0, but is '{raw}'.");
    }
}

/// <summary>Whether a user may still use the system: active and not deleted. Shared by the session cookie and API-token authentication.</summary>
public interface IUserStatus
{
    Task<bool> IsActiveAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Drops the cached answer so the next request reads the database (called when an admin changes <c>active</c>).</summary>
    void Forget(Guid userId);
}

/// <summary>
/// The answer is kept for <see cref="SessionSettings.Recheck"/>, judged by the injected <see cref="TimeProvider"/>. The
/// memory cache only holds the entries (its own expiry just frees memory), so a fake clock moves the window in tests.
/// </summary>
public sealed class UserStatus(
    IMemoryCache cache, TimeProvider time, SessionSettings settings, Func<Guid, CancellationToken, Task<bool>> readActive) : IUserStatus
{
    private sealed record Checked(bool Active, DateTimeOffset At);

    /// <summary>Reads <c>app_user</c> through the request's DbContext (the soft-delete filter excludes deleted users).</summary>
    public static UserStatus Create(IServiceProvider services)
    {
        var db = services.GetRequiredService<ZephyrDbContext>();
        return new UserStatus(
            services.GetRequiredService<IMemoryCache>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<SessionSettings>(),
            (id, ct) => db.AppUsers.AsNoTracking().AnyAsync(u => u.Id == id && u.Active, ct));
    }

    private static string Key(Guid userId) => $"zephyr:user-status:{userId:N}";

    public async Task<bool> IsActiveAsync(Guid userId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        if (cache.TryGetValue(Key(userId), out Checked? known) && known is not null && now - known.At < settings.Recheck)
        {
            return known.Active;
        }

        var active = await readActive(userId, ct);
        cache.Set(Key(userId), new Checked(active, now), settings.Recheck);
        return active;
    }

    public void Forget(Guid userId) => cache.Remove(Key(userId));
}

/// <summary>
/// Cookie session rules (ADR-002). The cookie slides on the idle window; the absolute limit is the ticket property
/// <c>issued_utc</c>, set once at sign-in (the ticket's own IssuedUtc moves on every renewal, so it cannot be used).
/// </summary>
public static class SessionRules
{
    public const string IssuedUtcKey = "issued_utc";

    public static void Stamp(AuthenticationProperties properties, DateTimeOffset now) =>
        properties.Items[IssuedUtcKey] = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Rejects (and signs out) a session older than the absolute limit, without <c>issued_utc</c>, or whose user is no longer
    /// active. A valid session is renewed on every request so the idle window counts from the last activity.
    /// </summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var services = context.HttpContext.RequestServices;
        var settings = services.GetRequiredService<SessionSettings>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        var valid = context.Properties.Items.TryGetValue(IssuedUtcKey, out var raw)
            && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var issued)
            && now - DateTimeOffset.FromUnixTimeSeconds(issued) <= settings.Absolute
            && Guid.TryParse(context.Principal?.FindFirst(HttpCurrentUser.ClaimType)?.Value, out var userId)
            && await services.GetRequiredService<IUserStatus>().IsActiveAsync(userId, context.HttpContext.RequestAborted);
        if (valid)
        {
            context.ShouldRenew = true;
            return;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(AuthModule.CookieScheme);
    }
}
