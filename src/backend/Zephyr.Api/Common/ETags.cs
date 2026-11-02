using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Zephyr.Api.Data;

namespace Zephyr.Api.Common;

/// <summary>
/// Strong ETags from id + updated_at. PATCH compares the ETag against the loaded row, and the UPDATE itself is guarded
/// by <c>WHERE updated_at = @loaded</c> (updated_at is the concurrency token), so a race ends in 412 as well.
/// </summary>
public static class ETags
{
    public static string Compute(Guid id, DateTime updatedAt) =>
        $"\"{WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes($"{id:D}{updatedAt.Ticks}")))}\"";

    public static string Compute(IEntity entity) => Compute(entity.Id, entity.UpdatedAt);

    public static void Set(HttpResponse response, IEntity entity) => response.Headers.ETag = Compute(entity);

    /// <summary>200 with the body and an ETag header, or 304 when <c>If-None-Match</c> matches the current ETag.</summary>
    public static IResult Ok(HttpContext context, IEntity entity, object body)
    {
        var etag = Compute(entity);
        context.Response.Headers.ETag = etag;
        return Matches(context.Request.Headers.IfNoneMatch, etag, allowWeak: true)
            ? Results.StatusCode(StatusCodes.Status304NotModified)
            : Results.Ok(body);
    }

    /// <summary>428 when <c>If-Match</c> is missing, 412 when it does not match the row as loaded.</summary>
    public static void RequireMatch(HttpRequest request, IEntity current)
    {
        var header = request.Headers.IfMatch;
        if (header.Count == 0)
        {
            throw Problems.PreconditionRequired();
        }

        if (!Matches(header, Compute(current), allowWeak: false))
        {
            throw Problems.PreconditionFailed();
        }
    }

    private static bool Matches(StringValues header, string etag, bool allowWeak)
    {
        foreach (var value in header)
        {
            foreach (var candidate in (value ?? "").Split(','))
            {
                var tag = candidate.Trim();
                if (tag == "*")
                {
                    return true;
                }

                if (allowWeak && tag.StartsWith("W/", StringComparison.Ordinal))
                {
                    tag = tag[2..];
                }

                if (tag == etag)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
