using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zephyr.Api.Auth;
using Zephyr.Api.Common;
using Zephyr.Api.Data;

namespace Zephyr.Api.Permissions;

/// <summary>One cell the caller holds: what they may do (<paramref name="Action"/>) to <paramref name="Entity"/>, and how widely.</summary>
public sealed record PermissionGrant(string Entity, string Action, string Scope);

/// <summary>
/// The properties of an entity's CLR type that its scope predicates read (names, resolved with reflection):
/// <paramref name="Owner"/> for <c>own</c> and <c>team</c>, <paramref name="Country"/> (the contracting country) for <c>country</c>.
/// </summary>
public sealed record ScopeColumns(string Owner, string? Country = null);

/// <summary>
/// The one permission engine (US-144, FT-112): the API and the UI both ask it. Rows come from <c>role_permission</c> (kept in memory,
/// reloaded at start) and a user's roles from the cached status (<see cref="IUserStatus"/>), so one set of rules answers
/// <see cref="Scope"/>, filters queries (<see cref="Filter{T}"/>) and judges single records (<see cref="Can{T}"/>).
/// </summary>
public interface IPermissionService
{
    /// <summary>Loads the matrix from the database once; later calls return at once.</summary>
    Task EnsureLoadedAsync(CancellationToken ct = default);

    /// <summary>Reads the matrix again (at start, after the seed).</summary>
    Task ReloadAsync(CancellationToken ct = default);

    /// <summary>The widest scope the user's roles give for <paramref name="action"/> on <paramref name="entity"/>; <c>none</c> when absent.</summary>
    string Scope(UserAccess user, string entity, string action);

    /// <summary>
    /// Keeps only the rows the user may read. A search or a count built on this result excludes out-of-scope rows, because the
    /// predicate is part of the query before any limit or paging.
    /// </summary>
    IQueryable<T> Filter<T>(IQueryable<T> query, UserAccess user, string entity)
        where T : class;

    /// <summary>Whether the user may do <paramref name="action"/> to this record: the same predicate as <see cref="Filter{T}"/>, for one row.</summary>
    bool Can<T>(UserAccess user, string entity, string action, T record)
        where T : class;

    /// <summary>
    /// The one record matching <paramref name="match"/> that the user may read, or a 404 problem. For an action other than
    /// <c>read</c> a record the user can see but may not change is a 403 problem.
    /// </summary>
    Task<T> FindAsync<T>(IQueryable<T> source, UserAccess user, string entity, string action, Expression<Func<T, bool>> match, CancellationToken ct = default)
        where T : class;

    /// <summary>Every (entity, action, scope) the user holds with a scope other than <c>none</c>.</summary>
    IReadOnlyList<PermissionGrant> Grants(UserAccess user);
}

/// <summary>
/// <see cref="IPermissionService"/> over the <c>role_permission</c> rows. Scope predicates are expression trees whose values
/// are bound as query parameters, never concatenated; the columns each entity uses are declared once in <see cref="Columns"/>.
/// </summary>
public sealed class PermissionService(IServiceScopeFactory? scopes = null) : IPermissionService
{
    /// <summary>
    /// Where each entity keeps its owner and contracting country. An entity that is filtered by <c>own</c>, <c>team</c> or
    /// <c>country</c> must be here (a missing entry is a programming error and fails loudly). The account's owner is
    /// <c>created_by</c> until the account feature adds an owner column; a time entry's owner is the person it belongs to.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, ScopeColumns> Columns = new Dictionary<string, ScopeColumns>
    {
        ["account"] = new(nameof(Account.CreatedBy), nameof(Account.CountryCode)),
        ["time_entry"] = new(nameof(TimeEntry.UserId))
    };

    private readonly SemaphoreSlim loading = new(1, 1);
    private volatile Dictionary<(string Role, string Entity, string Action), string>? matrix;

    /// <summary>Replaces the matrix in memory (the rows of the database, or of a test).</summary>
    public void Load(IEnumerable<PermissionRow> rows) =>
        matrix = rows.ToDictionary(r => (r.Role, r.Entity, r.Action), r => r.Scope);

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (matrix is not null)
        {
            return;
        }

        await loading.WaitAsync(ct);
        try
        {
            if (matrix is null)
            {
                await ReloadAsync(ct);
            }
        }
        finally
        {
            loading.Release();
        }
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var factory = scopes ?? throw new InvalidOperationException("This permission service has no database to read the matrix from.");
        await using var serviceScope = factory.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<ZephyrDbContext>();
        var rows = new List<PermissionRow>();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "select role_key, entity, action, scope from role_permission";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new PermissionRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        Load(rows);
    }

    private static int Rank(string scope) => Array.IndexOf(PermissionSeeder.Scopes, scope);

    public string Scope(UserAccess user, string entity, string action)
    {
        var cells = matrix ?? throw new InvalidOperationException("The permission matrix is not loaded. Call EnsureLoadedAsync first.");
        var widest = "none";
        foreach (var role in user.Roles)
        {
            if (cells.TryGetValue((role, entity, action), out var scope) && Rank(scope) > Rank(widest))
            {
                widest = scope;
            }
        }

        return widest;
    }

    public IQueryable<T> Filter<T>(IQueryable<T> query, UserAccess user, string entity)
        where T : class
    {
        var scope = Scope(user, entity, "read");
        return scope == "org" ? query : query.Where(Predicate<T>(user, entity, scope));
    }

    public bool Can<T>(UserAccess user, string entity, string action, T record)
        where T : class
    {
        var scope = Scope(user, entity, action);
        if (scope == "none")
        {
            return false;
        }

        // Nobody approves their own work, whatever the matrix says.
        if (action == "approve" && Columns.TryGetValue(entity, out var columns)
            && typeof(T).GetProperty(columns.Owner)?.GetValue(record) is Guid owner && owner == user.Id)
        {
            return false;
        }

        return scope == "org" || Predicate<T>(user, entity, scope).Compile()(record);
    }

    public async Task<T> FindAsync<T>(IQueryable<T> source, UserAccess user, string entity, string action, Expression<Func<T, bool>> match, CancellationToken ct = default)
        where T : class
    {
        var record = await Filter(source, user, entity).FirstOrDefaultAsync(match, ct) ?? throw Problems.NotFound();
        return action == "read" || Can(user, entity, action, record) ? record : throw Problems.Forbidden();
    }

    public IReadOnlyList<PermissionGrant> Grants(UserAccess user)
    {
        var grants = new List<PermissionGrant>();
        foreach (var entity in PermissionSeeder.Entities)
        {
            foreach (var action in PermissionSeeder.Actions)
            {
                var scope = Scope(user, entity, action);
                if (scope != "none")
                {
                    grants.Add(new PermissionGrant(entity, action, scope));
                }
            }
        }

        return grants;
    }

    private static Expression<Func<T, bool>> Predicate<T>(UserAccess user, string entity, string scope)
    {
        var row = Expression.Parameter(typeof(T), "e");
        return Expression.Lambda<Func<T, bool>>(Body(row, user, entity, scope), row);
    }

    private static Expression Body(ParameterExpression row, UserAccess user, string entity, string scope)
    {
        switch (scope)
        {
            case "org":
                return Expression.Constant(true);
            case "own":
                return Expression.Equal(Owner(row, entity), ListValues.Parameter(user.Id, typeof(Guid?)));
            case "team":
                var team = user.Team.Select(id => (Guid?)id).ToList();
                return Expression.Call(
                    ListValues.Parameter(team, typeof(List<Guid?>)), typeof(List<Guid?>).GetMethod(nameof(List<Guid?>.Contains))!, Owner(row, entity));
            case "country":
                // A user without a home country has no country scope: comparing with null would match every row without a country.
                return user.HomeCountry is null
                    ? Expression.Constant(false)
                    : Expression.Equal(Country(row, entity), ListValues.Parameter(user.HomeCountry, typeof(string)));
            default:
                return Expression.Constant(false);
        }
    }

    private static Expression Owner(ParameterExpression row, string entity)
    {
        var property = Expression.Property(row, ColumnsOf(entity).Owner);
        return property.Type == typeof(Guid) ? Expression.Convert(property, typeof(Guid?)) : property;
    }

    private static Expression Country(ParameterExpression row, string entity) =>
        Expression.Property(
            row,
            ColumnsOf(entity).Country
                ?? throw new InvalidOperationException($"Entity '{entity}' has a country scope in the matrix but declares no country column in PermissionService.Columns."));

    private static ScopeColumns ColumnsOf(string entity) =>
        Columns.TryGetValue(entity, out var columns)
            ? columns
            : throw new InvalidOperationException($"Entity '{entity}' has an own, team or country scope but declares no columns in PermissionService.Columns.");
}

public static class PermissionServices
{
    /// <summary>
    /// Registers the engine and what it reads: the in-memory cache, the session settings and the cached user status. Hosts
    /// that sign users in replace the settings and the status with the same types (<c>AddZephyrAuth</c>).
    /// </summary>
    public static IServiceCollection AddPermissions(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.TryAddSingleton(sp => SessionSettings.From(sp.GetRequiredService<IConfiguration>()));
        services.TryAddScoped<IUserStatus>(UserStatus.Create);
        services.TryAddSingleton<IPermissionService, PermissionService>();
        return services;
    }
}
