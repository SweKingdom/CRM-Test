using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Zephyr.Api.Common;

/// <summary>Collects one error per failing field; becomes the 422 <c>errors[]</c>.</summary>
public sealed class ModelState
{
    private readonly Dictionary<string, FieldError> errors = [];

    public bool IsValid => errors.Count == 0;

    public IReadOnlyList<FieldError> Errors => errors.Values.ToList();

    /// <summary>Records an error for <paramref name="field"/>; the first error per field wins.</summary>
    public void Add(string field, string code, string message) => errors.TryAdd(field, new FieldError(field, code, message));
}

/// <summary>Extra validation applied to every entity write (the metadata validator registers here).</summary>
public interface IWriteValidator
{
    Task ValidateAsync(string entityType, JsonElement body, ModelState state);
}

public sealed class NoOpWriteValidator : IWriteValidator
{
    public Task ValidateAsync(string entityType, JsonElement body, ModelState state) => Task.CompletedTask;
}

/// <summary>Marks an endpoint that went through <see cref="WriteHooks.WithWriteHooks{TBuilder}"/>.</summary>
public sealed class WriteHooksMarker
{
}

/// <summary>Every entity create/update endpoint (including upsert) reads its body here and calls <see cref="Run"/> before saving.</summary>
public static class WriteHooks
{
    private const string RanKey = "Zephyr.WriteHooks.Ran";

    public static IServiceCollection AddWriteHooks(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWriteValidator, NoOpWriteValidator>());
        return services;
    }

    /// <summary>Reads the JSON object body; malformed JSON or a non-object body is a 400.</summary>
    public static async Task<JsonElement> ReadBodyAsync(HttpRequest request, CancellationToken ct = default)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, default, ct);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Problems.BadRequest(ErrorCodes.Validation, "body", "The request body must be a JSON object.");
            }

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw Problems.BadRequest(ErrorCodes.Validation, "body", "The request body is not valid JSON.");
        }
    }

    /// <summary>Runs every registered <see cref="IWriteValidator"/>; any error becomes a 422 with <c>errors[]</c>.</summary>
    public static async Task Run(HttpContext context, string entityType, JsonElement body)
    {
        context.Items[RanKey] = true;
        var state = new ModelState();
        foreach (var validator in context.RequestServices.GetServices<IWriteValidator>())
        {
            await validator.ValidateAsync(entityType, body, state);
        }

        if (!state.IsValid)
        {
            throw Problems.Validation(state.Errors);
        }
    }

    /// <summary>
    /// Declares a create/update endpoint as a write endpoint. The endpoint is tagged (the endpoint test fails for an
    /// untagged POST/PUT/PATCH under /api/v1) and a successful response without a <see cref="Run"/> call fails loudly.
    /// </summary>
    public static TBuilder WithWriteHooks<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new WriteHooksMarker());
        builder.AddEndpointFilter(async (invocation, next) =>
        {
            var result = await next(invocation);
            if (result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 } && !invocation.HttpContext.Items.ContainsKey(RanKey))
            {
                throw new InvalidOperationException("A write endpoint returned success without calling WriteHooks.Run.");
            }

            return result;
        });
        return builder;
    }
}
