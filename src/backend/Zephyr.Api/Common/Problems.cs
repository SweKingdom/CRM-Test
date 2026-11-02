using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Zephyr.Api.Common;

/// <summary>The error code catalogue: the <c>code</c> of a problem and of each entry in <c>errors[]</c>.</summary>
public static class ErrorCodes
{
    public const string StatusChangeNotAllowed = "STATUS_CHANGE_NOT_ALLOWED";
    public const string Validation = "VALIDATION";
    public const string PreconditionFailed = "PRECONDITION_FAILED";
    public const string PreconditionRequired = "PRECONDITION_REQUIRED";
    public const string InvalidFilter = "INVALID_FILTER";
    public const string PeriodLocked = "PERIOD_LOCKED";
    public const string RecordDeleted = "RECORD_DELETED";
    public const string CsrfRejected = "CSRF_REJECTED";
    public const string NotInScope = "NOT_IN_SCOPE";
    public const string Forbidden = "FORBIDDEN";
}

/// <summary>One failing field in a problem's <c>errors[]</c>.</summary>
public sealed record FieldError(string Field, string Code, string Message);

/// <summary>Thrown anywhere in a request to end it with an RFC 9457 problem; <see cref="Problems.HandleExceptionAsync"/> writes it.</summary>
public sealed class ProblemException(int status, string code, string title, string detail, IReadOnlyList<FieldError>? errors = null)
    : Exception(detail)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public IReadOnlyList<FieldError>? Errors { get; } = errors;
}

/// <summary>RFC 9457 <c>application/problem+json</c> builder and the global handlers that write it.</summary>
public static class Problems
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>400 for a malformed request or an unknown parameter; <paramref name="field"/> names what is wrong.</summary>
    public static ProblemException BadRequest(string code, string field, string message) =>
        new(StatusCodes.Status400BadRequest, code, "Bad request", message, [new FieldError(field, code, message)]);

    /// <summary>422 with one entry per failing field.</summary>
    public static ProblemException Validation(IReadOnlyList<FieldError> errors) =>
        new(StatusCodes.Status422UnprocessableEntity, ErrorCodes.Validation, "Validation failed",
            "One or more fields are invalid. See errors for details.", errors);

    public static ProblemException PreconditionRequired() =>
        new(StatusCodes.Status428PreconditionRequired, ErrorCodes.PreconditionRequired, "Precondition required",
            "Send the current ETag in the If-Match header. Read the resource first to get it.");

    public static ProblemException PreconditionFailed() =>
        new(StatusCodes.Status412PreconditionFailed, ErrorCodes.PreconditionFailed, "Precondition failed",
            "The resource was changed since you read it. Read it again and retry with the new ETag.");

    private sealed record Body(
        string Type, string Title, int Status, string Detail, string? Code, IReadOnlyList<FieldError>? Errors, string TraceId);

    public static Task WriteAsync(
        HttpContext context, int status, string title, string detail, string? code = null, IReadOnlyList<FieldError>? errors = null)
    {
        var type = code is null ? "about:blank" : $"urn:zephyr:problem:{code.ToLowerInvariant().Replace('_', '-')}";
        context.Response.StatusCode = status;
        var body = new Body(type, title, status, detail, code, errors, Activity.Current?.Id ?? context.TraceIdentifier);
        return context.Response.WriteAsJsonAsync(body, Json, "application/problem+json");
    }

    private static Task Write(HttpContext context, ProblemException problem) =>
        WriteAsync(context, problem.Status, problem.Title, problem.Detail, problem.Code, problem.Errors);

    /// <summary>Global exception handler: no stack trace or exception text reaches the client for unexpected errors.</summary>
    public static Task HandleExceptionAsync(HttpContext context)
    {
        var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        return error switch
        {
            ProblemException problem => Write(context, problem),
            BadHttpRequestException bad => Write(context, new ProblemException(bad.StatusCode, ErrorCodes.Validation, "Bad request", bad.Message)),
            DbUpdateConcurrencyException => Write(context, PreconditionFailed()),
            _ => WriteAsync(context, StatusCodes.Status500InternalServerError, "Internal server error",
                "Something went wrong on our side. Quote the traceId when you report it.")
        };
    }

    /// <summary>Gives bare 4xx/5xx responses (404, 401, 403, 405...) a problem body.</summary>
    public static Task WriteStatusAsync(StatusCodeContext context)
    {
        var status = context.HttpContext.Response.StatusCode;
        var detail = status switch
        {
            StatusCodes.Status401Unauthorized => "Sign in to continue.",
            StatusCodes.Status403Forbidden => "You do not have access to this resource.",
            StatusCodes.Status404NotFound => "The resource was not found.",
            _ => "The request could not be completed."
        };
        return WriteAsync(context.HttpContext, status, ReasonPhrases.GetReasonPhrase(status), detail);
    }
}
