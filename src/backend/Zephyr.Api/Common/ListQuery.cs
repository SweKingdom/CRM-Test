using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Zephyr.Api.Common;

/// <summary>A page of a list response: <c>{items, nextCursor}</c>; <c>nextCursor</c> is null on the last page.</summary>
public sealed record ListPage<T>(IReadOnlyList<T> Items, string? NextCursor)
{
    public ListPage<TOut> Select<TOut>(Func<T, TOut> map) => new(Items.Select(map).ToList(), NextCursor);
}

/// <summary>
/// The fields a list endpoint lets clients filter and sort on (camelCase API names, each matching a property of
/// <typeparamref name="T"/>). <c>id</c> is always included. Every field listed here must have an index.
/// </summary>
public sealed class ListSpec<T>
{
    public ListSpec(params string[] fields)
    {
        var map = new Dictionary<string, PropertyInfo>();
        foreach (var field in fields.Append("id").Distinct())
        {
            map[field] = typeof(T).GetProperty(char.ToUpperInvariant(field[0]) + field[1..])
                ?? throw new ArgumentException($"{typeof(T).Name} has no property for list field '{field}'.", nameof(fields));
        }

        Fields = map;
    }

    public IReadOnlyDictionary<string, PropertyInfo> Fields { get; }
}

internal sealed record ListFilter(PropertyInfo Property, string Op, object Value);

internal sealed record ListSort(string Name, PropertyInfo Property, bool Descending);

/// <summary>Wrapper so EF binds a filter or cursor value as a query parameter instead of inlining it as a literal.</summary>
internal sealed class QueryValue<TValue>(TValue value)
{
    public TValue Value { get; } = value;
}

/// <summary>Value parsing and expression helpers shared by <see cref="ListQuery{T}"/>.</summary>
internal static class ListValues
{
    public static readonly string[] Operators = ["eq", "ne", "gt", "gte", "lt", "lte", "contains", "in"];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly MethodInfo ILike = typeof(NpgsqlDbFunctionsExtensions)
        .GetMethod(nameof(NpgsqlDbFunctionsExtensions.ILike), [typeof(DbFunctions), typeof(string), typeof(string)])!;

    private static readonly PropertyInfo EfFunctions = typeof(EF).GetProperty(nameof(EF.Functions))!;

    public static ProblemException Bad(string field, string message, string code = ErrorCodes.InvalidFilter) =>
        Problems.BadRequest(code, field, message);

    /// <summary>Numbers and dates: types with ordering operators.</summary>
    public static bool Ordered(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(decimal)
            || (type.IsPrimitive && type != typeof(bool) && type != typeof(char));
    }

    /// <summary>Sort keys are never null (NULL ordering would break keyset paging), so nullable columns are refused.</summary>
    public static bool Sortable(Type type) =>
        Nullable.GetUnderlyingType(type) is null && (Ordered(type) || type == typeof(string) || type == typeof(Guid) || type == typeof(bool));

    public static bool TryParse(Type type, string raw, out object? value)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        var culture = CultureInfo.InvariantCulture;
        value = null;
        if (target == typeof(string))
        {
            value = raw;
        }
        else if (target == typeof(Guid))
        {
            if (Guid.TryParse(raw, out var guid))
            {
                value = guid;
            }
        }
        else if (target == typeof(bool))
        {
            if (bool.TryParse(raw, out var flag))
            {
                value = flag;
            }
        }
        else if (target == typeof(int))
        {
            if (int.TryParse(raw, NumberStyles.Integer, culture, out var number))
            {
                value = number;
            }
        }
        else if (target == typeof(long))
        {
            if (long.TryParse(raw, NumberStyles.Integer, culture, out var big))
            {
                value = big;
            }
        }
        else if (target == typeof(decimal))
        {
            if (decimal.TryParse(raw, NumberStyles.Number, culture, out var money))
            {
                value = money;
            }
        }
        else if (target == typeof(double))
        {
            if (double.TryParse(raw, NumberStyles.Float, culture, out var real))
            {
                value = real;
            }
        }
        else if (target == typeof(DateTime))
        {
            if (DateTime.TryParse(raw, culture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var moment))
            {
                value = moment;
            }
        }
        else if (target == typeof(DateTimeOffset))
        {
            if (DateTimeOffset.TryParse(raw, culture, DateTimeStyles.AssumeUniversal, out var offset))
            {
                value = offset.ToUniversalTime();
            }
        }
        else if (target == typeof(DateOnly))
        {
            if (DateOnly.TryParse(raw, culture, DateTimeStyles.None, out var day))
            {
                value = day;
            }
        }
        else if (target.IsEnum)
        {
            if (Enum.TryParse(target, raw, true, out var member) && Enum.IsDefined(target, member))
            {
                value = member;
            }
        }

        return value is not null;
    }

    /// <summary>Invariant text of a sort value, for the cursor; <see cref="TryParse"/> reads it back exactly.</summary>
    public static string Format(object value) => value switch
    {
        DateTime moment => moment.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.ToString("O", CultureInfo.InvariantCulture),
        DateOnly day => day.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    public static Expression Parameter(object value, Type type) =>
        Expression.Property(
            Expression.Constant(Activator.CreateInstance(typeof(QueryValue<>).MakeGenericType(type), value)),
            nameof(QueryValue<int>.Value));

    public static Expression Compare(Expression left, Expression right, ExpressionType op) =>
        Ordered(left.Type)
            ? Expression.MakeBinary(op, left, right)
            : Expression.MakeBinary(op, Expression.Call(left, left.Type.GetMethod("CompareTo", [left.Type])!), Expression.Constant(0));

    public static Expression Contains(Expression property, string text)
    {
        var pattern = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        return Expression.Call(ILike, Expression.Property(null, EfFunctions), property, Parameter(pattern, typeof(string)));
    }
}

/// <summary>
/// The parsed <c>limit</c>, <c>cursor</c>, <c>filter[field][op]</c> and <c>sort</c> of a list request. Parsing validates
/// against a <see cref="ListSpec{T}"/> and throws a 400 problem naming the offending parameter; values are bound as
/// parameters, never concatenated into SQL.
/// </summary>
public sealed class ListQuery<T>
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    private static readonly Regex FilterKey = new(@"^filter\[(?<field>[^\[\]]+)\]\[(?<op>[^\[\]]+)\]$", RegexOptions.CultureInvariant);

    public int Limit { get; private init; } = DefaultLimit;

    internal IReadOnlyList<ListFilter> Filters { get; private init; } = [];

    internal IReadOnlyList<ListSort> Sort { get; private init; } = [];

    private object?[]? After { get; init; }

    private sealed record CursorToken(string Sort, string[] Values);

    public static ListQuery<T> Parse(IQueryCollection query, ListSpec<T> spec)
    {
        var limit = DefaultLimit;
        string? cursor = null;
        string? sortRaw = null;
        var filters = new List<ListFilter>();
        foreach (var (key, values) in query)
        {
            if (values.Count > 1)
            {
                throw ListValues.Bad(key, $"'{key}' was given more than once.");
            }

            var value = values.ToString();
            switch (key)
            {
                case "limit":
                    limit = ParseLimit(value);
                    break;
                case "cursor":
                    cursor = value;
                    break;
                case "sort":
                    sortRaw = value;
                    break;
                default:
                    filters.Add(ParseFilter(key, value, spec));
                    break;
            }
        }

        var sort = ParseSort(sortRaw, spec);
        return new ListQuery<T>
        {
            Limit = limit,
            Filters = filters,
            Sort = sort,
            After = cursor is null ? null : DecodeCursor(cursor, sort)
        };
    }

    /// <summary>Applies filters, order and the cursor to <paramref name="source"/> and reads one page.</summary>
    public async Task<ListPage<T>> ToPageAsync(IQueryable<T> source, CancellationToken cancellationToken = default)
    {
        var row = Expression.Parameter(typeof(T), "e");
        var conditions = Filters.Select(filter => FilterPredicate(row, filter)).ToList();
        if (After is { } after)
        {
            conditions.Add(AfterPredicate(row, after));
        }

        var query = source;
        if (conditions.Count > 0)
        {
            query = query.Where(Expression.Lambda<Func<T, bool>>(conditions.Aggregate((a, b) => Expression.AndAlso(a, b)), row));
        }

        IOrderedQueryable<T>? ordered = null;
        foreach (var key in Sort)
        {
            ordered = OrderBy(ordered ?? query, key, ordered is null);
        }

        var rows = await ordered!.Take(Limit + 1).ToListAsync(cancellationToken);
        if (rows.Count <= Limit)
        {
            return new ListPage<T>(rows, null);
        }

        var items = rows.GetRange(0, Limit);
        return new ListPage<T>(items, EncodeCursor(items[^1]));
    }

    private static int ParseLimit(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit < 1 || limit > MaxLimit)
        {
            throw ListValues.Bad("limit", $"limit must be a whole number from 1 to {MaxLimit} (max {MaxLimit}).", ErrorCodes.Validation);
        }

        return limit;
    }

    private static ListFilter ParseFilter(string key, string raw, ListSpec<T> spec)
    {
        var match = FilterKey.Match(key);
        if (!match.Success)
        {
            throw ListValues.Bad(key, $"Unknown query parameter '{key}'. Use limit, cursor, sort or filter[field][op].");
        }

        var field = match.Groups["field"].Value;
        var op = match.Groups["op"].Value;
        if (!spec.Fields.TryGetValue(field, out var property))
        {
            throw ListValues.Bad(key, $"Unknown filter field '{field}'. Allowed fields: {string.Join(", ", spec.Fields.Keys)}.");
        }

        if (!ListValues.Operators.Contains(op))
        {
            throw ListValues.Bad(key, $"Unknown filter operator '{op}'. Allowed operators: {string.Join(", ", ListValues.Operators)}.");
        }

        var type = property.PropertyType;
        if (op == "contains" && type != typeof(string))
        {
            throw ListValues.Bad(key, $"Operator 'contains' only works on text fields and '{field}' is not one.");
        }

        if ((op is "gt" or "gte" or "lt" or "lte") && !ListValues.Ordered(type))
        {
            throw ListValues.Bad(key, $"Operator '{op}' only works on numbers and dates and '{field}' is not one.");
        }

        if (op != "in")
        {
            return new ListFilter(property, op, ParseValue(key, field, type, raw));
        }

        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;
        foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            list.Add(ParseValue(key, field, type, part));
        }

        if (list.Count == 0)
        {
            throw ListValues.Bad(key, $"Operator 'in' needs a comma-separated list of values for '{field}'.");
        }

        return new ListFilter(property, op, list);
    }

    private static object ParseValue(string key, string field, Type type, string raw) =>
        ListValues.TryParse(type, raw, out var value)
            ? value!
            : throw ListValues.Bad(key, $"'{raw}' is not a valid value for field '{field}'.");

    private static IReadOnlyList<ListSort> ParseSort(string? raw, ListSpec<T> spec)
    {
        raw ??= spec.Fields.ContainsKey("updatedAt") ? "-updatedAt" : "id";
        var keys = new List<ListSort>();
        foreach (var part in raw.Split(','))
        {
            var descending = part.StartsWith('-');
            var name = descending ? part[1..] : part;
            if (!spec.Fields.TryGetValue(name, out var property))
            {
                throw ListValues.Bad("sort", $"Unknown sort key '{name}'. Allowed keys: {string.Join(", ", spec.Fields.Keys)}.");
            }

            if (!ListValues.Sortable(property.PropertyType))
            {
                throw ListValues.Bad("sort", $"'{name}' cannot be used as a sort key.");
            }

            if (keys.Any(k => k.Name == name))
            {
                throw ListValues.Bad("sort", $"Sort key '{name}' was given more than once.");
            }

            keys.Add(new ListSort(name, property, descending));
        }

        if (keys.All(k => k.Name != "id"))
        {
            keys.Add(new ListSort("id", spec.Fields["id"], false));
        }

        return keys;
    }

    private static string Signature(IEnumerable<ListSort> sort) =>
        string.Join(',', sort.Select(s => (s.Descending ? "-" : "") + s.Name));

    private string EncodeCursor(T last)
    {
        var values = Sort.Select(s => ListValues.Format(s.Property.GetValue(last)!)).ToArray();
        return WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new CursorToken(Signature(Sort), values), ListValues.Json));
    }

    /// <summary>A cursor that is not base64url JSON, was made for another sort, or holds a bad value is a 400.</summary>
    private static object?[] DecodeCursor(string cursor, IReadOnlyList<ListSort> sort)
    {
        object?[]? values = null;
        try
        {
            var token = JsonSerializer.Deserialize<CursorToken>(WebEncoders.Base64UrlDecode(cursor), ListValues.Json);
            if (token?.Values is { } raw && token.Sort == Signature(sort) && raw.Length == sort.Count)
            {
                var parsed = new object?[raw.Length];
                var ok = true;
                for (var i = 0; i < raw.Length && ok; i++)
                {
                    ok = raw[i] is not null && ListValues.TryParse(sort[i].Property.PropertyType, raw[i], out parsed[i]);
                }

                if (ok)
                {
                    values = parsed;
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            values = null;
        }

        return values ?? throw ListValues.Bad("cursor", "The cursor is not valid. Start again without a cursor.", ErrorCodes.Validation);
    }

    private static Expression FilterPredicate(ParameterExpression row, ListFilter filter)
    {
        var property = Expression.Property(row, filter.Property);
        var type = filter.Property.PropertyType;
        if (filter.Op == "contains")
        {
            return ListValues.Contains(property, (string)filter.Value);
        }

        if (filter.Op == "in")
        {
            var listType = typeof(List<>).MakeGenericType(type);
            return Expression.Call(ListValues.Parameter(filter.Value, listType), listType.GetMethod("Contains")!, property);
        }

        var value = ListValues.Parameter(filter.Value, type);
        return filter.Op switch
        {
            "eq" => Expression.Equal(property, value),
            "ne" => Expression.NotEqual(property, value),
            "gt" => Expression.GreaterThan(property, value),
            "gte" => Expression.GreaterThanOrEqual(property, value),
            "lt" => Expression.LessThan(property, value),
            _ => Expression.LessThanOrEqual(property, value)
        };
    }

    /// <summary>Rows strictly after the cursor row in sort order: (k1 beyond v1) OR (k1 = v1 AND (k2 beyond v2 OR ...)).</summary>
    private Expression AfterPredicate(ParameterExpression row, object?[] values)
    {
        Expression? result = null;
        for (var i = Sort.Count - 1; i >= 0; i--)
        {
            var key = Expression.Property(row, Sort[i].Property);
            var value = ListValues.Parameter(values[i]!, key.Type);
            var beyond = ListValues.Compare(key, value, Sort[i].Descending ? ExpressionType.LessThan : ExpressionType.GreaterThan);
            result = result is null ? beyond : Expression.OrElse(beyond, Expression.AndAlso(Expression.Equal(key, value), result));
        }

        return result!;
    }

    private static IOrderedQueryable<T> OrderBy(IQueryable<T> source, ListSort key, bool first)
    {
        var row = Expression.Parameter(typeof(T), "e");
        var selector = Expression.Lambda(Expression.Property(row, key.Property), row);
        var method = (first ? "OrderBy" : "ThenBy") + (key.Descending ? "Descending" : "");
        var call = Expression.Call(typeof(Queryable), method, [typeof(T), key.Property.PropertyType], source.Expression, Expression.Quote(selector));
        return (IOrderedQueryable<T>)source.Provider.CreateQuery<T>(call);
    }
}
