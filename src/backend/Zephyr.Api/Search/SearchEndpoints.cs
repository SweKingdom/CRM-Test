using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Zephyr.Api.Common;
using Zephyr.Api.Data;

namespace Zephyr.Api.Search;

/// <summary>
/// <c>GET /api/v1/search?q=&amp;types=&amp;limit=</c> (US-147): full-text search over <c>search_document</c> first (every query word as a
/// prefix, accents ignored), then a trigram step when fewer than 5 records were found (typos). The caller's read scope is
/// applied in SQL before ranking and limit; nothing is filtered afterwards. Everything is bound as parameters.
/// </summary>
public static class SearchEndpoints
{
    public const string Url = "/api/v1/search";
    public const int MinQueryLength = 2;
    public const int MaxQueryLength = 100;
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;

    /// <summary>The trigram step runs when the full-text step found fewer records than this.</summary>
    public const int FallbackBelow = 5;

    public static readonly string[] Types = ["account", "contact", "lead", "opportunity", "project"];

    // TODO(US-144): the read scope must come from PermissionService (entity=search, action=read) and the permission matrix.
    // Until then this interim rule stands in, like InterimRoleGate: these roles read everything ...
    private static readonly string[] OrgRoles = ["Admin", "Management", "Finance", "Integration"];

    // ... and these roles read the records they own or that are in their home country. Any other caller is refused.
    private static readonly string[] OwnRoles = ["Salesperson", "BDR", "PM", "Consultant"];

    private static readonly Regex Word = new(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant);

    /// <summary>The query words as a prefix tsquery (<c>tsq</c>) and as one normalised phrase (<c>phrase</c>).</summary>
    private const string Query =
        """
        with tok as (
            select f_search_norm(t) as t from unnest(@tokens) as x(t)
        ), q as (
            select to_tsquery('simple', string_agg(quote_literal(t) || ':*', ' & ')) as tsq, string_agg(t, ' ') as phrase
            from tok where t <> ''
        )
        """;

    private const string Columns = "sd.entity_type, sd.entity_id, sd.title, coalesce(sd.subtitle, sd.title)";

    /// <summary>Entity type filter and the interim read scope. Applied in the WHERE clause, before ranking and limit.</summary>
    private const string Filter =
        """
        (cardinality(@types) = 0 or sd.entity_type = any(@types))
          and (@org or sd.owner_id = @uid or sd.country_code = @home)
        """;

    private const string Weight =
        "case sd.entity_type when 'account' then 4 when 'opportunity' then 3 when 'contact' then 2 when 'lead' then 2 else 1 end";

    // Order (answer #76): exact title, title prefix, then full-text rank plus a small boost for recent activity (updated_at),
    // then entity weight, then newest first.
    private const string FullTextSql = $"""
        {Query}
        select {Columns},
               (ts_rank_cd(sd.tsv, q.tsq)::float8
                + 0.05 / (1 + greatest(extract(epoch from now() - sd.updated_at)::float8, 0) / 2592000.0)) as score
        from search_document sd cross join q
        where sd.tsv @@ (select tsq from q)
          and {Filter}
        order by case when f_search_norm(sd.title) = q.phrase then 0
                      when starts_with(f_search_norm(sd.title), q.phrase) then 1 else 2 end,
                 score desc, {Weight} desc, sd.updated_at desc
        limit @limit
        """;

    // word_similarity compares the query with the best-matching stretch of the whole text, so a short name with one typo
    // still matches inside a long text (email, phone, city).
    private const string TrigramSql = $"""
        {Query}
        select {Columns}, word_similarity(q.phrase, sd.text)::float8 as score
        from search_document sd cross join q
        where (select phrase from q) <% sd.text
          and {Filter}
          and (sd.entity_type || ':' || sd.entity_id::text) <> all(@found)
        order by score desc, {Weight} desc, sd.updated_at desc
        limit @limit
        """;

    private sealed record Scope(bool Org, Guid UserId, string? Home);

    private sealed record Hit(string Type, Guid Id, string Title, string Snippet, double Score);

    private static ProblemException Bad(string field, string message) => Problems.BadRequest(ErrorCodes.Validation, field, message);

    public static IEndpointRouteBuilder MapSearch(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Url, async Task<IResult> (HttpContext context, ZephyrDbContext db, ICurrentUser current) =>
        {
            if (current.UserId is not { } userId)
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            var query = context.Request.Query;
            foreach (var (key, values) in query)
            {
                if (key is not ("q" or "types" or "limit"))
                {
                    throw Bad(key, $"Unknown query parameter '{key}'. Use q, types or limit.");
                }

                if (values.Count > 1)
                {
                    throw Bad(key, $"'{key}' was given more than once.");
                }
            }

            var text = query["q"].ToString().Trim();
            if (text.Length < MinQueryLength || text.Length > MaxQueryLength)
            {
                throw Bad("q", $"q must be {MinQueryLength} to {MaxQueryLength} characters long.");
            }

            var limit = DefaultLimit;
            if (query.ContainsKey("limit")
                && (!int.TryParse(query["limit"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > MaxLimit))
            {
                throw Bad("limit", $"limit must be a whole number from 1 to {MaxLimit}.");
            }

            var types = query["types"].ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
            if (query.ContainsKey("types") && (types.Length == 0 || types.Except(Types).Any()))
            {
                throw Bad("types", $"types must be a comma-separated list of: {string.Join(", ", Types)}.");
            }

            var scope = await ReadScopeAsync(db, userId, context.RequestAborted);
            var tokens = Word.Matches(text).Select(m => m.Value).ToArray();
            if (tokens.Length == 0)
            {
                return Results.Ok(new { Items = Array.Empty<Hit>(), Fallback = false });
            }

            var (hits, fallback) = await RunAsync(db, scope, tokens, types, limit, context.RequestAborted);
            return Results.Ok(new { Items = hits, Fallback = fallback });
        });

        return routes;
    }

    /// <summary>The caller's read scope. An unknown or inactive user, or one without a role that may search, gets a 403.</summary>
    private static async Task<Scope> ReadScopeAsync(ZephyrDbContext db, Guid userId, CancellationToken ct)
    {
        var user = await db.AppUsers.AsNoTracking()
            .Where(u => u.Id == userId && u.Active)
            .Select(u => new { u.Roles, u.HomeCountry })
            .SingleOrDefaultAsync(ct);
        var org = user is not null && user.Roles.Intersect(OrgRoles).Any();
        if (user is null || (!org && !user.Roles.Intersect(OwnRoles).Any()))
        {
            throw new ProblemException(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, "Forbidden",
                "Your role does not allow searching. Ask an administrator if you need access.");
        }

        return new Scope(org, userId, user.HomeCountry?.Trim());
    }

    private static async Task<(List<Hit> Hits, bool Fallback)> RunAsync(
        ZephyrDbContext db, Scope scope, string[] tokens, string[] types, int limit, CancellationToken ct)
    {
        // One transaction: both steps see the same data, and set_config(..., true) is local to it (SET LOCAL).
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var inner = (NpgsqlTransaction)transaction.GetDbTransaction();

        async Task<List<Hit>> Read(string sql, int rows, string[]? found = null)
        {
            await using var command = new NpgsqlCommand(sql, connection, inner);
            command.Parameters.AddWithValue("tokens", tokens);
            command.Parameters.AddWithValue("types", types);
            command.Parameters.AddWithValue("org", scope.Org);
            command.Parameters.AddWithValue("uid", scope.UserId);
            command.Parameters.Add(new NpgsqlParameter("home", NpgsqlDbType.Text) { Value = (object?)scope.Home ?? DBNull.Value });
            command.Parameters.AddWithValue("limit", rows);
            if (found is not null)
            {
                command.Parameters.AddWithValue("found", found);
            }

            await using var reader = await command.ExecuteReaderAsync(ct);
            var result = new List<Hit>();
            while (await reader.ReadAsync(ct))
            {
                result.Add(new Hit(reader.GetString(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetDouble(4)));
            }

            return result;
        }

        // Step 1 reads at least FallbackBelow rows, so a small limit cannot make a query look like it has few hits.
        var hits = await Read(FullTextSql, Math.Max(limit, FallbackBelow));
        if (hits.Count >= FallbackBelow || hits.Count >= limit)
        {
            return (hits.Take(limit).ToList(), false);
        }

        await using (var threshold = new NpgsqlCommand("select set_config('pg_trgm.word_similarity_threshold', '0.3', true)", connection, inner))
        {
            await threshold.ExecuteNonQueryAsync(ct);
        }

        hits.AddRange(await Read(TrigramSql, limit - hits.Count, hits.Select(h => $"{h.Type}:{h.Id}").ToArray()));
        return (hits, true);
    }
}
