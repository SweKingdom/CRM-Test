namespace Zephyr.Api.Permissions;

/// <summary>
/// The only endpoints that may be called without signing in (default deny, design v22 R6). One entry is <c>METHOD /path</c> or
/// just <c>/path</c> for any method; a trailing <c>*</c> matches any path with that prefix. An endpoint marked
/// <c>AllowAnonymous</c> that is not listed here fails the endpoint inventory test, and so does any change to this list that the
/// test does not expect.
/// </summary>
public static class AnonymousAllowList
{
    public static readonly string[] Entries =
    [
        "GET /health",
        "GET /auth/login",
        "GET /auth/denied",
        "/signin-oidc-*",
        "POST /api/v1/public/*"
    ];

    /// <summary>Whether an endpoint with this route pattern and these HTTP methods (empty means any) is on the list.</summary>
    public static bool Allows(string pattern, IReadOnlyCollection<string> methods) =>
        Entries.Any(entry =>
        {
            var parts = entry.Split(' ', 2);
            var (method, path) = parts.Length == 2 ? (parts[0], parts[1]) : (null, parts[0]);
            var samePath = path.EndsWith('*') ? pattern.StartsWith(path[..^1], StringComparison.Ordinal) : pattern == path;
            return samePath && (method is null || methods.Count == 0 || methods.Contains(method));
        });
}
