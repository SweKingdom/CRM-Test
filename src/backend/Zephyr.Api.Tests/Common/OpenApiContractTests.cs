using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Zephyr.Api.Tests.Support;

namespace Zephyr.Api.Tests.Common;

/// <summary>
/// US-136: the generated docs/api/openapi.yaml and the routes the API registers describe the same operations, so the
/// generator cannot describe a route the code lacks, and the code cannot register a route the file does not document.
/// </summary>
[Collection("db")]
public class OpenApiContractTests(DbFixture fixture) : IAsyncLifetime
{
    private const string ApiBase = "/api/v1";

    private static readonly Regex RouteParameter = new(@"\{(\w+)[^}]*\}", RegexOptions.CultureInvariant);
    private static readonly Regex PathLine = new(@"^  (/\S*):$", RegexOptions.CultureInvariant);
    private static readonly Regex MethodLine = new(@"^    (get|put|post|patch|delete):$", RegexOptions.CultureInvariant);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class PathItem(string path)
    {
        public string Path { get; } = path;

        /// <summary>True when the path item has its own <c>servers</c>: it is served from the site root, not under /api/v1.</summary>
        public bool SiteRoot { get; set; }

        public List<string> Methods { get; } = [];
    }

    private static string FindOpenApiFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "api", "openapi.yaml")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new FileNotFoundException("docs/api/openapi.yaml was not found above the test binaries. Run: node tools/openapi-from-metadata/generate.mjs")
            : Path.Combine(directory.FullName, "docs", "api", "openapi.yaml");
    }

    /// <summary>"METHOD /full/path" for every operation under <c>paths:</c> of the generated YAML (block style, two-space indent).</summary>
    private static List<string> DocumentedRoutes()
    {
        var items = new List<PathItem>();
        var inPaths = false;
        foreach (var line in File.ReadAllLines(FindOpenApiFile()).Select(l => l.TrimEnd()).Where(l => l.Length > 0))
        {
            if (!line.StartsWith(' '))
            {
                inPaths = line == "paths:";
            }
            else if (inPaths && PathLine.Match(line) is { Success: true } path)
            {
                items.Add(new PathItem(path.Groups[1].Value));
            }
            else if (inPaths && line == "    servers:")
            {
                items[^1].SiteRoot = true;
            }
            else if (inPaths && MethodLine.Match(line) is { Success: true } method)
            {
                items[^1].Methods.Add(method.Groups[1].Value.ToUpperInvariant());
            }
        }

        return items
            .SelectMany(item => item.Methods.Select(method => $"{method} {(item.SiteRoot ? "" : ApiBase)}{item.Path}"))
            .Order()
            .ToList();
    }

    [DbFact]
    public void Every_route_the_api_registers_is_in_openapi_yaml_and_the_file_describes_no_other_route()
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
