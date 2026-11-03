using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zephyr.Api.Common;
using Zephyr.Api.Data;

namespace Zephyr.Api.Auth;

/// <summary>
/// Personal API tokens (US-142): scripts and an AI assistant act as their owner with <c>Authorization: Bearer zph_...</c>.
/// A token is 32 random bytes (256 bits), shown once and stored only as a hash. The hash algorithm is recorded next to each
/// hash so it can be changed later without a data migration. Unsalted SHA-256 is acceptable here (product owner, question
/// #74): a salt protects guessable secrets, and a deterministic hash is what makes the lookup per request possible.
/// </summary>
public static class ApiTokens
{
    public const string Scheme = "ApiToken";
    public const string Selector = "Zephyr";
    public const string Prefix = "zph_";
    public const string HashAlgorithm = "sha256";
    public const int PrefixLength = 12;
    public const int MaxActive = 10;
    public const int DefaultDays = 90;
    public const int MaxDays = 365;
    public static readonly TimeSpan LastUsedInterval = TimeSpan.FromMinutes(1);

    private const string Route = "/api/v1/me/tokens";
    private const string BearerPrefix = "Bearer ";

    // Serialises token creation per user so two concurrent requests cannot both create the 10th and 11th token.
    private const string UserLock = "select pg_advisory_xact_lock(hashtextextended({0}, 7142))";

    /// <summary>The plaintext: <c>zph_</c> + base64url (no padding) of 32 random bytes, 47 characters.</summary>
    public static string NewToken() => Prefix + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>Lowercase hex SHA-256 of the token's UTF-8 bytes. The only function to change if the hash algorithm changes.</summary>
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>The stored hash equals <paramref name="hash"/>, compared in constant time, and was made with the current algorithm.</summary>
    public static bool HashMatches(ApiToken stored, string hash) =>
        stored.HashAlgorithm == HashAlgorithm
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(stored.TokenHash), Encoding.ASCII.GetBytes(hash));

    /// <summary>The bearer token of a request, when the Authorization header carries one that starts with <c>zph_</c>.</summary>
    public static bool TryGetToken(HttpRequest request, out string token)
    {
        var header = request.Headers.Authorization.ToString();
        token = header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) ? header[BearerPrefix.Length..].Trim() : "";
        return token.StartsWith(Prefix, StringComparison.Ordinal);
    }

    /// <summary>Registers the default scheme that routes a request to the token handler or the session cookie, and the token handler.</summary>
    public static AuthenticationBuilder AddApiTokenSchemes(this AuthenticationBuilder builder) => builder
        .AddPolicyScheme(Selector, null, options =>
            options.ForwardDefaultSelector = context => TryGetToken(context.Request, out _) ? Scheme : AuthModule.CookieScheme)
        .AddScheme<AuthenticationSchemeOptions, ApiTokenHandler>(Scheme, _ => { });

    public static WebApplication MapApiTokenEndpoints(this WebApplication app)
    {
        app.MapPost(Route, CreateAsync).WithWriteHooks();
        app.MapGet(Route, ListAsync);
        app.MapDelete(Route + "/{id}", RevokeAsync);
        return app;
    }

    /// <summary>
    /// 201 with the plaintext, once. Needs a signed-in session: a token cannot create tokens (403). 422 for a bad name,
    /// <c>expiresInDays</c> outside 1-365 (OUT_OF_RANGE) or an owner who already has 10 active tokens (TOKEN_LIMIT).
    /// </summary>
    private static async Task<IResult> CreateAsync(
        HttpContext context, ZephyrDbContext db, ICurrentUser current, TimeProvider time, CancellationToken ct)
    {
        if (context.User.Identity?.AuthenticationType == Scheme)
        {
            throw new ProblemException(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, "Forbidden",
                "An API token cannot create tokens. Sign in to Zephyr in the browser and create the token there.");
        }

        if (current.UserId is not { } userId)
        {
            return Results.Unauthorized();
        }

        var body = await WriteHooks.ReadBodyAsync(context.Request, ct);
        await WriteHooks.Run(context, "api_token", body);
        var (name, days) = ReadInput(body);
        var now = time.GetUtcNow().UtcDateTime;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync(UserLock, [userId.ToString()], ct);
        if (await db.ApiTokens.CountAsync(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now, ct) >= MaxActive)
        {
            const string message = "You already have 10 active API tokens. Revoke one you no longer use, then create a new one.";
            throw new ProblemException(StatusCodes.Status422UnprocessableEntity, ErrorCodes.TokenLimit, "Validation failed", message,
                [new FieldError("token", ErrorCodes.TokenLimit, message)]);
        }

        var plaintext = NewToken();
        var token = new ApiToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            TokenHash = Hash(plaintext),
            HashAlgorithm = HashAlgorithm,
            Prefix = plaintext[..PrefixLength],
            ExpiresAt = now.AddDays(days)
        };
        db.ApiTokens.Add(token);
        db.AuditLogs.Add(Audit("api_token_created", userId, token));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        // The plaintext must not be cached or kept by an intermediary.
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(new { token.Id, token.Name, token.Prefix, token.ExpiresAt, Token = plaintext }, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>The caller's own tokens, newest first: prefix and dates only, never the hash or the plaintext.</summary>
    private static async Task<IResult> ListAsync(ZephyrDbContext db, ICurrentUser current, CancellationToken ct)
    {
        if (current.UserId is not { } userId)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(await db.ApiTokens.AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new { t.Id, t.Name, t.Prefix, t.ExpiresAt, t.LastUsedAt, t.RevokedAt })
            .ToListAsync(ct));
    }

    /// <summary>204. Idempotent: revoking a revoked token keeps its first revocation time. 404 when the token is not the caller's.</summary>
    private static async Task<IResult> RevokeAsync(Guid id, ZephyrDbContext db, ICurrentUser current, TimeProvider time, CancellationToken ct)
    {
        if (current.UserId is not { } userId)
        {
            return Results.Unauthorized();
        }

        var token = await db.ApiTokens.SingleOrDefaultAsync(t => t.Id == id && t.UserId == userId, ct);
        if (token is null)
        {
            return Results.NotFound();
        }

        if (token.RevokedAt is null)
        {
            token.RevokedAt = time.GetUtcNow().UtcDateTime;
            db.AuditLogs.Add(Audit("api_token_revoked", userId, token));
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    // Only the prefix is audited and logged, never the plaintext.
    private static AuditLog Audit(string action, Guid userId, ApiToken token) => new()
    {
        UserId = userId,
        Action = action,
        EntityType = "api_token",
        EntityId = token.Id,
        Detail = JsonSerializer.Serialize(new { token.Prefix })
    };

    /// <summary>The name (1-100 characters) and the lifetime in days (omitted = 90, whole number 1-365); every problem is reported at once.</summary>
    private static (string Name, int Days) ReadInput(JsonElement body)
    {
        var errors = new List<FieldError>();
        var name = body.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String ? nameValue.GetString()!.Trim() : "";
        if (name.Length is < 1 or > 100)
        {
            errors.Add(new FieldError("name", ErrorCodes.Validation, "name is required and must be 1 to 100 characters."));
        }

        var days = DefaultDays;
        if (body.TryGetProperty("expiresInDays", out var daysValue) && daysValue.ValueKind != JsonValueKind.Null)
        {
            if (daysValue.ValueKind != JsonValueKind.Number || !daysValue.TryGetInt32(out days))
            {
                errors.Add(new FieldError("expiresInDays", ErrorCodes.Validation, "expiresInDays must be a whole number of days."));
            }
            else if (days is < 1 or > MaxDays)
            {
                errors.Add(new FieldError("expiresInDays", ErrorCodes.OutOfRange, $"expiresInDays must be between 1 and {MaxDays}."));
            }
        }

        return errors.Count == 0 ? (name, days) : throw Problems.Validation(errors);
    }
}

/// <summary>
/// Authenticates <c>Authorization: Bearer zph_...</c>: looks the token up by the hash of the header value, on every request
/// and straight from the database (no cache), so a revocation is effective at once. The principal is the owner, with the
/// owner's roles. A missing, revoked or expired token, or an owner who is inactive or deleted, is a 401 with
/// <c>WWW-Authenticate: Bearer</c>. Only the token prefix is logged.
/// </summary>
public sealed class ApiTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory loggerFactory, UrlEncoder encoder,
    ZephyrDbContext db, IUserStatus status, TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!ApiTokens.TryGetToken(Request, out var token))
        {
            return AuthenticateResult.NoResult();
        }

        var prefix = token.Length > ApiTokens.PrefixLength ? token[..ApiTokens.PrefixLength] : token;
        var hash = ApiTokens.Hash(token);
        var ct = Context.RequestAborted;
        // The inner join drops a deleted owner (soft-delete filter on app_user).
        var found = await (from t in db.ApiTokens.AsNoTracking()
                           join u in db.AppUsers.AsNoTracking() on t.UserId equals u.Id
                           where t.TokenHash == hash
                           select new { Token = t, User = u }).SingleOrDefaultAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;

        if (found is null || !ApiTokens.HashMatches(found.Token, hash))
        {
            return Refuse(prefix, "unknown token");
        }

        if (found.Token.RevokedAt is not null)
        {
            return Refuse(prefix, "revoked");
        }

        if (found.Token.ExpiresAt <= now)
        {
            return Refuse(prefix, "expired");
        }

        if (!await status.IsActiveAsync(found.User.Id, ct))
        {
            return Refuse(prefix, "owner is inactive or deleted");
        }

        if (found.Token.LastUsedAt is not { } last || now - last >= ApiTokens.LastUsedInterval)
        {
            await db.ApiTokens.Where(t => t.Id == found.Token.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), ct);
        }

        var principal = UserMatching.ToPrincipal(found.User, ApiTokens.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, ApiTokens.Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    private AuthenticateResult Refuse(string prefix, string reason)
    {
        Logger.LogWarning("API token refused ({Reason}): prefix {Prefix}", reason, prefix);
        return AuthenticateResult.Fail(reason);
    }
}
