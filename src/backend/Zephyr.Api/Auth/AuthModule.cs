using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Zephyr.Api.Common;
using Zephyr.Api.Data;

namespace Zephyr.Api.Auth;

/// <summary>One OpenID Connect provider (Keycloak realm, later Entra), read from <c>Auth:Providers[n]</c>. Added by configuration only.</summary>
public sealed class AuthProvider
{
    public string Name { get; set; } = "";
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>True when the provider verifies email addresses, so a verified email may link a pre-provisioned user.</summary>
    public bool EmailTrusted { get; set; }
}

public sealed record AuthProviders(IReadOnlyList<AuthProvider> All);

/// <summary>Issuer and subject of the first administrator (ZEPHYR_BOOTSTRAP_ADMIN_ISSUER / _SUBJECT). Used only while no admin exists.</summary>
public sealed record BootstrapAdmin(string? Issuer, string? Subject);

/// <summary>
/// Sign-in (US-140): OpenID Connect code flow with PKCE per configured provider, a session cookie, user matching,
/// the bootstrap admin, the CSRF check for cookie writes and the /auth/* endpoints. Zephyr stores no passwords.
/// Session limits and the user-status recheck (US-141) are in <see cref="SessionRules"/>.
/// </summary>
public static class AuthModule
{
    public const string CookieScheme = "Cookies";
    public const string CsrfHeader = "X-Zephyr-Csrf";
    public const string NotProvisionedMessage = "Your account is not set up in Zephyr. Contact an administrator.";
    private const string RefusedMarker = "zephyr:user-not-provisioned";
    private static readonly Regex ProviderName = new("^[a-z0-9-]+$");

    public static string SchemeName(AuthProvider provider) => "oidc-" + provider.Name;

    /// <summary>Registers the session cookie, one OIDC scheme per configured provider and default-deny authorisation.</summary>
    public static WebApplicationBuilder AddZephyrAuth(this WebApplicationBuilder builder)
    {
        // ZEPHYR_AUTH__PROVIDERS__0__NAME and friends bind to Auth:Providers:0:Name.
        builder.Configuration.AddEnvironmentVariables("ZEPHYR_");
        var providers = builder.Configuration.GetSection("Auth:Providers").Get<List<AuthProvider>>() ?? [];
        ValidateProviders(providers);
        builder.Services.AddSingleton(new AuthProviders(providers));

        var session = SessionSettings.From(builder.Configuration);
        builder.Services.AddSingleton(session);
        builder.Services.AddMemoryCache();
        builder.Services.AddScoped<IUserStatus>(UserStatus.Create);
        // Cookie lifetimes follow the injected clock (a fake one in tests).
        builder.Services.AddOptions<CookieAuthenticationOptions>(CookieScheme)
            .Configure<TimeProvider>((options, time) => options.TimeProvider = time);

        var authentication = builder.Services.AddAuthentication(CookieScheme).AddCookie(CookieScheme, options =>
        {
            options.Cookie = new SessionCookieBuilder { Name = "zephyr.session", HttpOnly = true, SameSite = SameSiteMode.Lax, IsEssential = true };
            options.ExpireTimeSpan = session.Idle;
            options.SlidingExpiration = true;
            options.Events.OnValidatePrincipal = SessionRules.ValidateAsync;
            // An API answers 401/403, it does not redirect to a login page.
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });
        foreach (var provider in providers)
        {
            authentication.AddOpenIdConnect(SchemeName(provider), options => ConfigureProvider(options, provider));
        }

        // Default deny: every endpoint needs a session unless it says AllowAnonymous (design v22 R6).
        builder.Services.AddAuthorization(options =>
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        return builder;
    }

    /// <summary>Authentication, the CSRF check, authorisation and the /auth/* endpoints. Call after <c>UseApiConventions</c>.</summary>
    public static WebApplication UseZephyrAuth(this WebApplication app)
    {
        app.UseAuthentication();
        app.Use(RejectCsrf);
        app.UseAuthorization();

        app.MapGet("/auth/login", (string? provider, string? returnUrl, AuthProviders providers) =>
        {
            if (returnUrl is not null && !IsLocalPath(returnUrl))
            {
                throw Problems.BadRequest(ErrorCodes.Validation, "returnUrl", "returnUrl must be a path on this site, for example /accounts.");
            }

            var chosen = provider is null ? providers.All.FirstOrDefault() : providers.All.FirstOrDefault(p => p.Name == provider);
            if (chosen is null)
            {
                var names = string.Join(", ", providers.All.Select(p => p.Name));
                throw Problems.BadRequest(ErrorCodes.Validation, "provider",
                    names == "" ? "No sign-in provider is configured." : $"Unknown sign-in provider. Use one of: {names}.");
            }

            return Results.Challenge(new AuthenticationProperties { RedirectUri = returnUrl ?? "/" }, [SchemeName(chosen)]);
        }).AllowAnonymous();

        app.MapPost("/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieScheme);
            return Results.NoContent();
        });

        app.MapGet("/auth/me", async (ICurrentUser current, ZephyrDbContext db) =>
        {
            var id = current.UserId;
            var user = id is null ? null : await db.AppUsers.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id);
            return user is { Active: true }
                ? Results.Ok(new { user.Id, user.Name, user.Email, user.Roles, user.HomeCountry })
                : Results.Unauthorized();
        });

        app.MapGet("/auth/denied", (HttpContext context, string? reason) =>
            Problems.WriteAsync(context, StatusCodes.Status403Forbidden, "Forbidden",
                reason == "failed" ? "Sign-in did not complete. Go back and try again." : NotProvisionedMessage)).AllowAnonymous();
        return app;
    }

    /// <summary>A return URL is accepted only when it is a path on this site: no scheme, no host, no <c>//</c> or backslash tricks.</summary>
    public static bool IsLocalPath(string value) =>
        value.StartsWith('/')
        && !value.StartsWith("//")
        && !value.Contains('\\')
        && !value.Any(char.IsControl);

    private static void ValidateProviders(List<AuthProvider> providers)
    {
        for (var i = 0; i < providers.Count; i++)
        {
            var provider = providers[i];
            var where = $"Auth:Providers:{i} (ZEPHYR_AUTH__PROVIDERS__{i}__...)";
            if (!ProviderName.IsMatch(provider.Name))
            {
                throw new InvalidOperationException($"{where}: NAME must be lowercase letters, digits and dashes, because it becomes the callback path /signin-oidc-{{name}}.");
            }

            if (providers.Count(p => p.Name == provider.Name) > 1)
            {
                throw new InvalidOperationException($"{where}: NAME '{provider.Name}' is used by more than one provider.");
            }

            if (!Uri.TryCreate(provider.Authority, UriKind.Absolute, out _) || provider.ClientId == "" || provider.ClientSecret == "")
            {
                throw new InvalidOperationException($"{where}: AUTHORITY (an absolute URL), CLIENTID and CLIENTSECRET are required.");
            }
        }
    }

    private static void ConfigureProvider(OpenIdConnectOptions options, AuthProvider provider)
    {
        options.SignInScheme = CookieScheme;
        options.Authority = provider.Authority;
        options.ClientId = provider.ClientId;
        options.ClientSecret = provider.ClientSecret;
        options.CallbackPath = "/signin-oidc-" + provider.Name;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.SaveTokens = false;
        options.MapInboundClaims = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        // Plain http is accepted only for a provider on this machine (the Compose Keycloak).
        options.RequireHttpsMetadata = !(Uri.TryCreate(provider.Authority, UriKind.Absolute, out var authority) && authority.IsLoopback);
        options.TokenValidationParameters.NameClaimType = "name";

        options.Events.OnTokenValidated = async context =>
        {
            var services = context.HttpContext.RequestServices;
            var configuration = services.GetRequiredService<IConfiguration>();
            var bootstrap = new BootstrapAdmin(configuration["ZEPHYR_BOOTSTRAP_ADMIN_ISSUER"], configuration["ZEPHYR_BOOTSTRAP_ADMIN_SUBJECT"]);
            var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("Zephyr.Api.Auth.UserMatching");
            var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
            var user = await UserMatching.MatchAsync(
                services.GetRequiredService<ZephyrDbContext>(), provider, bootstrap, context.Principal!, now.UtcDateTime, log);
            if (user is null)
            {
                context.Fail(RefusedMarker);
                return;
            }

            context.Principal = UserMatching.ToPrincipal(user);
            // The absolute session limit counts from here (US-141).
            context.Properties ??= new AuthenticationProperties();
            SessionRules.Stamp(context.Properties, now);
        };
        options.Events.OnRemoteFailure = context =>
        {
            context.Response.Redirect(context.Failure?.Message == RefusedMarker ? "/auth/denied" : "/auth/denied?reason=failed");
            context.HandleResponse();
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// CSRF check (design v22 R5): a state-changing request that carries the session cookie must send
    /// <c>X-Zephyr-Csrf: 1</c> and come from the same origin. Requests without the session cookie (bearer tokens,
    /// public forms) are not checked: there is no ambient credential for a foreign site to ride on.
    /// </summary>
    private static async Task RejectCsrf(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        var writes = HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method)
            || HttpMethods.IsPatch(request.Method) || HttpMethods.IsDelete(request.Method);
        if (writes && !IsSameOriginAjax(request) && (await context.AuthenticateAsync(CookieScheme)).Succeeded)
        {
            await Problems.WriteAsync(context, StatusCodes.Status403Forbidden, "Forbidden",
                "The request was blocked because it did not come from the Zephyr web app. Reload the page and try again.",
                ErrorCodes.CsrfRejected);
            return;
        }

        await next(context);
    }

    private static bool IsSameOriginAjax(HttpRequest request)
    {
        if (request.Headers[CsrfHeader].ToString() != "1")
        {
            return false;
        }

        if (request.Headers.TryGetValue("Origin", out var origin) && !IsSameOrigin(origin.ToString(), request))
        {
            return false;
        }

        return !request.Headers.TryGetValue("Sec-Fetch-Site", out var site) || site == "same-origin" || site == "none";
    }

    private static bool IsSameOrigin(string origin, HttpRequest request) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var from)
        && Uri.TryCreate($"{request.Scheme}://{request.Host}", UriKind.Absolute, out var self)
        && from.Scheme == self.Scheme && from.Host == self.Host && from.Port == self.Port;

    private static bool IsLocalhost(HostString host) =>
        host.Host == "localhost" || (IPAddress.TryParse(host.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    /// <summary>The session cookie is Secure everywhere except on localhost (plain http during development).</summary>
    private sealed class SessionCookieBuilder : CookieBuilder
    {
        public override CookieOptions Build(HttpContext context, DateTimeOffset expiresFrom)
        {
            var options = base.Build(context, expiresFrom);
            options.Secure = !IsLocalhost(context.Request.Host);
            return options;
        }
    }
}

/// <summary>Finds or links the Zephyr user for a validated identity-provider token (matching algorithm, US-140).</summary>
public static class UserMatching
{
    /// <summary>
    /// The user to sign in, or null when the sign-in is refused. 1) issuer + subject; 2) the bootstrap admin while no admin
    /// exists; 3) first sign-in links a pre-provisioned user on a verified email, only from a provider whose email is trusted.
    /// Nobody is created except the bootstrap admin: there is no self sign-up. Tokens are never logged.
    /// </summary>
    public static async Task<AppUser?> MatchAsync(
        ZephyrDbContext db, AuthProvider provider, BootstrapAdmin bootstrap, ClaimsPrincipal external, DateTime now, ILogger log)
    {
        var issuer = external.FindFirst("iss")?.Value;
        var subject = external.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(subject))
        {
            return Refuse(log, provider, "the token has no issuer or subject", issuer, subject);
        }

        var user = await db.AppUsers.SingleOrDefaultAsync(u => u.Issuer == issuer && u.Subject == subject);
        if (user is not null)
        {
            return user.Active ? await SignedIn(db, user, now) : Refuse(log, provider, "the user is inactive", issuer, subject);
        }

        var email = external.FindFirst("email")?.Value?.Trim();
        var emailKey = string.IsNullOrEmpty(email) ? null : email.ToLowerInvariant();
        var byEmail = emailKey is null ? null : await db.AppUsers.SingleOrDefaultAsync(u => u.Email.ToLower() == emailKey);

        if (byEmail is null && emailKey is not null && bootstrap.Issuer == issuer && bootstrap.Subject == subject
            && !await db.AppUsers.AnyAsync(u => u.Active && u.Roles.Contains(AppUser.AdminRole)))
        {
            var name = external.FindFirst("name")?.Value ?? external.FindFirst("preferred_username")?.Value ?? email!;
            var admin = new AppUser
            {
                Id = Guid.NewGuid(),
                Name = name,
                Email = email!,
                Issuer = issuer,
                Subject = subject,
                Roles = [AppUser.AdminRole],
                Active = true,
                LastLoginAt = now
            };
            db.AppUsers.Add(admin);
            db.AuditLogs.Add(new AuditLog
            {
                UserId = admin.Id,
                Action = "bootstrap_admin_used",
                EntityType = "app_user",
                EntityId = admin.Id,
                Detail = JsonSerializer.Serialize(new { issuer, subject })
            });
            await db.SaveChangesAsync();
            log.LogWarning("bootstrap admin used: issuer {Issuer}, subject {Subject}", issuer, subject);
            return admin;
        }

        if (emailKey is null)
        {
            return Refuse(log, provider, "the token has no email and the user is not linked", issuer, subject);
        }

        if (!provider.EmailTrusted)
        {
            return Refuse(log, provider, "the provider's email is not trusted, so it cannot link a user", issuer, subject);
        }

        if (!string.Equals(external.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(log, provider, "the email is not verified", issuer, subject);
        }

        if (byEmail is null)
        {
            return Refuse(log, provider, "no Zephyr user has this email", issuer, subject);
        }

        if (byEmail.Issuer is not null || byEmail.Subject is not null)
        {
            return Refuse(log, provider, "the email belongs to a user already linked to another identity", issuer, subject);
        }

        if (!byEmail.Active)
        {
            return Refuse(log, provider, "the user is inactive", issuer, subject);
        }

        byEmail.Issuer = issuer;
        byEmail.Subject = subject;
        log.LogInformation("Linked user {UserId} to issuer {Issuer}, subject {Subject} on first sign-in", byEmail.Id, issuer, subject);
        return await SignedIn(db, byEmail, now);
    }

    /// <summary>The session principal: <c>sub</c> and <c>zephyr_user_id</c> are the app_user id; roles and issuer come from Zephyr.</summary>
    public static ClaimsPrincipal ToPrincipal(AppUser user)
    {
        var claims = new List<Claim>
        {
            new("sub", user.Id.ToString()),
            new(HttpCurrentUser.ClaimType, user.Id.ToString()),
            new("name", user.Name),
            new("email", user.Email),
            new("issuer", user.Issuer ?? "")
        };
        claims.AddRange(user.Roles.Select(role => new Claim("role", role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthModule.CookieScheme, "name", "role"));
    }

    private static async Task<AppUser> SignedIn(ZephyrDbContext db, AppUser user, DateTime now)
    {
        user.LastLoginAt = now;
        await db.SaveChangesAsync();
        return user;
    }

    private static AppUser? Refuse(ILogger log, AuthProvider provider, string reason, string? issuer, string? subject)
    {
        log.LogWarning("Sign-in refused ({Reason}): provider {Provider}, issuer {Issuer}, subject {Subject}", reason, provider.Name, issuer, subject);
        return null;
    }
}
