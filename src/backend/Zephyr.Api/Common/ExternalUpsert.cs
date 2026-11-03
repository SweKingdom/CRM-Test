using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zephyr.Api.Data;
using Zephyr.Api.Permissions;

namespace Zephyr.Api.Common;

/// <summary>
/// How the generic upsert and soft delete handle one entity: its registry entry, how a JSON body is applied to a row
/// (adding one error per invalid field to the <see cref="ModelState"/>) and how a row is shown in a response.
/// </summary>
public sealed record EntityBinding<T>(EntityInfo Info, Action<T, JsonElement, ModelState> Apply, Func<T, object> ToDto)
    where T : class, IEntity, new();

/// <summary>Idempotent writes by external id and soft delete (US-135, ADR-011).</summary>
public static class ExternalUpsert
{
    private static readonly string[] Systems = ["dynamics", "fortnox"];
    private const int MaxValueLength = 200;

    /// <summary>
    /// Org-wide create and update on every entity (design v22 R7). The matrix cannot say this: Integration is an API client,
    /// not a matrix role, so these routes check the caller's roles instead of an (entity, action) cell.
    /// </summary>
    public static readonly string[] OrgWriteRoles = ["Admin", "Integration"];

    /// <summary>Least privilege until the matrix defines delete per role.</summary>
    public static readonly string[] DeleteRoles = ["Admin"];

    /// <summary>
    /// <c>PUT /api/v1/{plural}/by-external-id/{system}/{value}</c>: 201 when the key is new, 200 when it already maps to a row.
    /// Order: role check, body and hooks, field validation (nothing is written until all of it passed), then one transaction:
    /// <c>INSERT … ON CONFLICT DO NOTHING</c> on <c>external_id</c> with a pre-generated entity id decides who creates the
    /// row; everyone else locks the mapping <c>FOR UPDATE</c> and updates. <c>If-Match</c> is ignored.
    /// </summary>
    public static IEndpointRouteBuilder MapExternalUpsert<T>(this IEndpointRouteBuilder routes, EntityBinding<T> binding)
        where T : class, IEntity, new()
    {
        var plural = binding.Info.Plural;
        var entityType = binding.Info.Entity;
        routes.MapPut(
            $"/api/v1/{plural}/by-external-id/{{system}}/{{value}}",
            async Task<IResult> (string system, string value, HttpContext context, ZephyrDbContext db, ICurrentUser current) =>
            {
                var ct = context.RequestAborted;
                var body = await WriteHooks.ReadBodyAsync(context.Request, ct);
                await WriteHooks.Run(context, entityType, body);

                var state = new ModelState();
                if (!Systems.Contains(system))
                {
                    state.Add("system", "INVALID", $"system must be one of: {string.Join(", ", Systems)}.");
                }

                if (value.Length > MaxValueLength)
                {
                    state.Add("value", "INVALID", $"The external value can be at most {MaxValueLength} characters.");
                }

                // The candidate is the new row if this call wins the insert; it also validates the body before anything is written.
                var candidate = new T { Id = Guid.NewGuid() };
                binding.Apply(candidate, body, state);
                if (!state.IsValid)
                {
                    throw Problems.Validation(state.Errors);
                }

                var userId = current.UserId ?? SystemUser.Id;
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO external_id (id, entity_type, entity_id, system, value, created_by, updated_by)
                    VALUES ({Guid.NewGuid()}, {entityType}, {candidate.Id}, {system}, {value}, {userId}, {userId})
                    ON CONFLICT (system, entity_type, value) DO NOTHING
                    """,
                    ct);

                T entity;
                if (inserted == 1)
                {
                    entity = candidate;
                    db.Set<T>().Add(entity);
                }
                else
                {
                    // Lock the mapping so concurrent upserts of the same key run one after the other.
                    var entityIds = await db.Database.SqlQuery<Guid>(
                        $"""
                        SELECT entity_id AS "Value" FROM external_id
                        WHERE system = {system} AND entity_type = {entityType} AND value = {value}
                        FOR UPDATE
                        """).ToListAsync(ct);
                    var existing = await db.Set<T>().FindAsync([entityIds.Single()], ct)
                        ?? throw new InvalidOperationException($"external_id {system}/{entityType}/{value} points to a missing row.");
                    if (existing.DeletedAt is not null)
                    {
                        throw new ProblemException(StatusCodes.Status409Conflict, ErrorCodes.RecordDeleted, "Record deleted",
                            "The record behind this external id was deleted and is not restored by an upsert. Ask an administrator to restore it.");
                    }

                    entity = existing;
                    binding.Apply(entity, body, new ModelState());
                }

                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                ETags.Set(context.Response, entity);
                return inserted == 1
                    ? Results.Created($"/api/v1/{plural}/{entity.Id}", binding.ToDto(entity))
                    : Results.Ok(binding.ToDto(entity));
            }).RequireRoles(OrgWriteRoles).WithWriteHooks();
        return routes;
    }

    /// <summary>
    /// <c>DELETE /api/v1/{plural}/{id}</c>: sets <c>deleted_at</c> (the audit interceptor turns the removal into a soft delete)
    /// and answers 204. A missing or already deleted row is a 404.
    /// </summary>
    public static IEndpointRouteBuilder MapSoftDelete<T>(this IEndpointRouteBuilder routes, EntityBinding<T> binding)
        where T : class, IEntity, new()
    {
        routes.MapDelete(
            $"/api/v1/{binding.Info.Plural}/{{id:guid}}",
            async Task<IResult> (Guid id, HttpContext context, ZephyrDbContext db) =>
            {
                // Find ignores the soft-delete filter, so the deleted check is explicit.
                var entity = await db.Set<T>().FindAsync([id], context.RequestAborted);
                if (entity is null || entity.DeletedAt is not null)
                {
                    return Results.NotFound();
                }

                db.Set<T>().Remove(entity);
                await db.SaveChangesAsync(context.RequestAborted);
                return Results.NoContent();
            }).RequireRoles(DeleteRoles);
        return routes;
    }
}
