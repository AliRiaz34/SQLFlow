using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One column of an object as the policy admin screen needs it: identity, type, and whether it is
/// currently restricted. Unlike <see cref="ObjectColumnDto"/> this is never filtered by policy, since managing
/// the policy requires seeing every column, restricted or not.</summary>
public sealed record ColumnPolicyStateDto(
    string ObjectKey, string ColumnName, int Ordinal, string? DataType, bool Nullable,
    bool IsSensitive, string? Reason, string? UpdatedBy, DateTime? UpdatedUtc);

/// <summary>One restricted column, with enough of its owning object to render a flat "everything currently
/// restricted" list without a second lookup per row.</summary>
public sealed record RestrictedColumnDto(
    string ObjectKey, string ObjectName, string? Database, string? Schema, string ColumnName,
    string? Reason, string? UpdatedBy, DateTime UpdatedUtc);

/// <summary>Sets or clears the sensitivity flag on one column. Setting <see cref="IsSensitive"/> false clears
/// the restriction but keeps the row (and its audit trail) rather than deleting it.</summary>
public sealed record SetColumnPolicyRequest(
    string? ObjectKey, string? ColumnName, bool IsSensitive, string? Reason);

/// <summary>
/// Administers <see cref="CatalogColumnPolicy"/>: which columns an AI assistant (and the ad-hoc query surface
/// generally) may never read. This is the write side of the restriction that <see cref="SearchEndpoints"/>,
/// <see cref="LineageEndpoints"/>'s object columns/dossier, and <see cref="ColumnPolicyGuard"/> all enforce.
///
/// It lives under the "admin" scope, not "operate": deciding what data an assistant may see is a governance
/// decision about the estate's access surface, the same category as user/role administration, not a day-to-day
/// operational action.
/// </summary>
public static class ColumnPolicyEndpoints
{
    public static RouteGroupBuilder MapColumnPolicyEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/powerai/column-policies", ListRestrictedAsync)
            .WithTags("PowerAI").WithName("ListColumnPolicies");
        group.MapGet("/powerai/column-policies/objects/{key}", GetObjectPolicyStateAsync)
            .WithTags("PowerAI").WithName("GetObjectColumnPolicyState");
        group.MapPut("/powerai/column-policies", SetAsync)
            .WithTags("PowerAI").WithName("SetColumnPolicy");

        return group;
    }

    /// <summary>Every column currently marked sensitive, across the whole catalog, for the policy screen's
    /// overview list.</summary>
    private static async Task<Results<Ok<PagedResult<RestrictedColumnDto>>, ProblemHttpResult>> ListRestrictedAsync(
        CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var rows = from pol in db.ColumnPolicies.AsNoTracking()
                   join o in db.Objects.AsNoTracking() on pol.ObjectKey equals o.Key
                   where pol.IsSensitive
                   orderby o.Name, pol.ColumnName
                   select new RestrictedColumnDto(
                       pol.ObjectKey, o.Name, o.Database, o.Schema, pol.ColumnName,
                       pol.Reason, pol.UpdatedBy, pol.UpdatedUtc);

        var total = await rows.LongCountAsync(ct).ConfigureAwait(false);
        var items = await rows.Skip((p - 1) * size).Take(size).ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<RestrictedColumnDto>(items, p, size, total));
    }

    /// <summary>Every column of one object with its current policy state, so the admin screen can render a
    /// toggle per column without the caller having to diff two separate lists.</summary>
    private static async Task<Results<Ok<IReadOnlyList<ColumnPolicyStateDto>>, ProblemHttpResult>> GetObjectPolicyStateAsync(
        string key, CatalogDbContext db, CancellationToken ct)
    {
        var exists = await db.Objects.AsNoTracking().AnyAsync(o => o.Key == key, ct).ConfigureAwait(false);
        if (!exists)
        {
            return NotFound("object", key);
        }

        var rows = await (from c in db.ObjectColumns.AsNoTracking()
                           where c.ObjectKey == key
                           join pol in db.ColumnPolicies.AsNoTracking()
                               on new { c.ObjectKey, ColumnName = c.Name } equals new { pol.ObjectKey, pol.ColumnName }
                               into policies
                           from pol in policies.DefaultIfEmpty()
                           orderby c.Ordinal, c.Name
                           select new ColumnPolicyStateDto(
                               c.ObjectKey, c.Name, c.Ordinal, c.DataType, c.Nullable,
                               pol != null && pol.IsSensitive, pol != null ? pol.Reason : null,
                               pol != null ? pol.UpdatedBy : null, pol != null ? pol.UpdatedUtc : (DateTime?)null))
            .ToListAsync(ct).ConfigureAwait(false);

        return TypedResults.Ok<IReadOnlyList<ColumnPolicyStateDto>>(rows);
    }

    /// <summary>Sets (or clears) one column's sensitivity flag. Upserts on (ObjectKey, ColumnName), so calling it
    /// twice for the same column updates the one row rather than accumulating history.</summary>
    private static async Task<Results<Ok<ColumnPolicyStateDto>, ProblemHttpResult>> SetAsync(
        SetColumnPolicyRequest request, CatalogDbContext db, TimeProvider clock, ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ObjectKey) || string.IsNullOrWhiteSpace(request.ColumnName))
        {
            return Problem(
                "objectKey and columnName are both required.", StatusCodes.Status400BadRequest, "Invalid request");
        }

        var objectKey = request.ObjectKey.Trim();
        var columnName = request.ColumnName.Trim();

        var column = await db.ObjectColumns.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ObjectKey == objectKey && c.Name == columnName, ct).ConfigureAwait(false);
        if (column is null)
        {
            return Problem(
                $"No column '{columnName}' is known on object '{objectKey}'. Sync the catalog first, or check " +
                "the spelling.",
                StatusCodes.Status404NotFound, "Not found");
        }

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        var updatedBy = user.FindFirst("sub")?.Value ?? user.Identity?.Name;
        var now = clock.GetUtcNow().UtcDateTime;

        var policy = await db.ColumnPolicies
            .FirstOrDefaultAsync(p => p.ObjectKey == objectKey && p.ColumnName == columnName, ct)
            .ConfigureAwait(false);
        if (policy is null)
        {
            policy = new CatalogColumnPolicy { ObjectKey = objectKey, ColumnName = columnName };
            db.ColumnPolicies.Add(policy);
        }

        policy.IsSensitive = request.IsSensitive;
        policy.Reason = reason;
        policy.UpdatedBy = updatedBy;
        policy.UpdatedUtc = now;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return TypedResults.Ok(new ColumnPolicyStateDto(
            objectKey, columnName, column.Ordinal, column.DataType, column.Nullable,
            policy.IsSensitive, policy.Reason, policy.UpdatedBy, policy.UpdatedUtc));
    }

    private static ProblemHttpResult NotFound(string resource, string key)
        => TypedResults.Problem(detail: $"No {resource} with key '{key}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");

    private static ProblemHttpResult Problem(string detail, int statusCode, string title)
        => TypedResults.Problem(detail: detail, statusCode: statusCode, title: title);
}
