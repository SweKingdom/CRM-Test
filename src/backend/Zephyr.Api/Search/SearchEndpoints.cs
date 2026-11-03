using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Zephyr.Api.Auth;
using Zephyr.Api.Common;
using Zephyr.Api.Data;
using Zephyr.Api.Permissions;

namespace Zephyr.Api.Search;

/// <summary>
/// <c>GET /api/v1/search?q=&amp;types=&amp;limit=</c> (US-147): full-text search over <c>search_document</c> first (every query word as a
/// prefix, accents ignored), then a trigram step when fewer than 5 records were found (typos). The caller's read scope comes from the
/// permission matrix (<see cref="IPermissionService.Scope"/>, one answer per entity type) and is applied in SQL before ranking and
/// limit; nothing is filtered afterwards. Everything is bound as parameters.
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

    /// <summary>
    /// Every user role may call the route. What a caller finds depends on their scope per entity type; a caller whose scope is
    /// <c>none</c> for all of them gets a 403 (Integration is an API client, not a matrix role, so it is refused too).
    /// </summary>
    private static readonly string[] AnyRole = PermissionSeeder.Roles.Select(r => r.Key).ToArray();

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

    /// <summary>
    /// Entity type filter and the read scope of the permission matrix: per type the caller reads everything (<c>org</c>), the
    /// records they own (<c>own</c>), the records of their team (<c>team</c>), the records of their home country (<c>country</c>) or
    /// nothing (the type is in none of the arrays). Applied in the WHERE clause, before ranking and limit. A null home country or
    /// an empty team matches no row.
    /// </summary>
    private const string Filter =
        """
        (cardinality(@types) = 0 or sd.entity_type = any(@types))
          and (sd.entity_type = any(@org)
               or (sd.entity_type = any(@own) and sd.owner_id = @uid)
               or (sd.entity_type = any(@team) and sd.owner_id = any(@teamIds))
               or (sd.entity_type = any(@country) and sd.country_code = @home))
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

    /// <summary>The entity types the caller reads per scope, and the facts the scopes compare with.</summary>
    private sealed record Scope(string[] Org, string[] Own, string[] Team, string[] Country, Guid UserId, Guid[] TeamIds, string? Home);

    private sealed record Hit(string Type, Guid Id, string Title, string Snippet, double Score);

    private static ProblemException Bad(string field, string message) => Problems.BadRequest(ErrorCodes.Validation, field, message);

    public static IEndpointRouteBuilder MapSearch(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Url, async Task<IResult> (HttpContext context, ZephyrDbContext db, IPermissionService permissions) =>
        {
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

            await permissions.EnsureLoadedAsync(context.RequestAborted);
            var scope = ReadScope(permissions, await context.CurrentUserAsync());
            var tokens = Word.Matches(text).Select(m => m.Value).ToArray();
            if (tokens.Length == 0)
            {
                return Results.Ok(new { Items = Array.Empty<Hit>(), Fallback = false });
            }

            var (hits, fallback) = await RunAsync(db, scope, tokens, types, limit, context.RequestAborted);
            return Results.Ok(new { Items = hits, Fallback = fallback });
        }).RequireRoles(AnyRole);

        return routes;
    }

    /// <summary>
    /// The caller's read scope per entity type, from the permission matrix. A caller who reads none of the types gets a 403;
    /// a <c>types</c> filter that names only types they cannot read finds nothing, so it does not reveal which types exist.
    /// </summary>
    private static Scope ReadScope(IPermissionService permissions, UserAccess user)
    {
        string[] TypesWith(string mode) => Types.Where(type => permissions.Scope(user, type, "read") == mode).ToArray();

        var scope = new Scope(TypesWith("org"), TypesWith("own"), TypesWith("team"), TypesWith("country"), user.Id, user.Team.ToArray(), user.HomeCountry?.Trim());
        return scope.Org.Length + scope.Own.Length + scope.Team.Length + scope.Country.Length == 0 ? throw Problems.Forbidden() : scope;
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
            command.Parameters.AddWithValue("own", scope.Own);
            command.Parameters.AddWithValue("team", scope.Team);
            command.Parameters.AddWithValue("country", scope.Country);
            command.Parameters.AddWithValue("uid", scope.UserId);
            command.Parameters.AddWithValue("teamIds", scope.TeamIds);
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
