using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Zephyr.Api.Tests.Support;

namespace Zephyr.Api.Tests.Common;

/// <summary>
/// US-136: the generated docs/api/openapi.json and the routes the API registers describe the same operations, so the
/// generator cannot describe a route the code lacks, and the code cannot register a route the file does not document.
/// </summary>
[Collection("db")]
public class OpenApiContractTests(DbFixture fixture) : IAsyncLifetime
{
    private const string ApiBase = "/api/v1";

    private static readonly Regex RouteParameter = new(@"\{(\w+)[^}]*\}", RegexOptions.CultureInvariant);
    private static readonly string[] HttpMethods = ["get", "put", "post", "patch", "delete"];

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static string FindOpenApiFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "api", "openapi.json")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new FileNotFoundException("docs/api/openapi.json was not found above the test binaries. Run: node tools/openapi-from-metadata/generate.mjs")
            : Path.Combine(directory.FullName, "docs", "api", "openapi.json");
    }

    /// <summary>"METHOD /full/path" for every operation under <c>paths</c>. A path item with its own <c>servers</c> is served from the site root, not under /api/v1.</summary>
    private static List<string> DocumentedRoutes()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindOpenApiFile()));
        return document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path =>
            {
                var prefix = path.Value.TryGetProperty("servers", out _) ? "" : ApiBase;
                return path.Value.EnumerateObject()
                    .Where(member => HttpMethods.Contains(member.Name))
                    .Select(member => $"{member.Name.ToUpperInvariant()} {prefix}{path.Name}");
            })
            .Order()
            .ToList();
    }

    [DbFact]
    public void Every_route_the_api_registers_is_in_openapi_json_and_the_file_describes_no_other_route()
    {
        using var factory = new WebApplicationFactory<Program>();
        var registered = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
            {
                var path = RouteParameter.Replace(endpoint.RoutePattern.RawText ?? "", "{$1}");
                var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? new[] { "GET" };
                return methods.Select(method => $"{method} {path}");
            })
            .Distinct()
            .Order()
            .ToList();
        var documented = DocumentedRoutes();

        Assert.Contains("PUT /api/v1/accounts/by-external-id/{system}/{value}", registered);
        Assert.Empty(registered.Except(documented).ToList());
        Assert.Empty(documented.Except(registered).ToList());
    }
}
