using Asp.Versioning;
using Asp.Versioning.Builder;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Models;
using GymOS.Platform.Application.Auditing;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.WebApi.Endpoints;

/// <summary>
/// <c>/api/v1/audit-logs</c> — read-only, permanently (AD-7, ARCHITECTURE.md §3.9).
///
/// There is no POST, PUT, or DELETE here and there never will be. Entries come from the
/// SaveChanges interceptor and from explicit handler calls; a trail with a write API is a
/// trail someone can rewrite, which is not a trail.
/// </summary>
public static class AuditLogEndpoints
{
    public static IEndpointRouteBuilder MapAuditLogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        ApiVersionSet versionSet = endpoints.NewApiVersionSet("Audit log").Build();

        RouteGroupBuilder group = endpoints
            .MapGroup("/api/v{version:apiVersion}/audit-logs")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0))
            .WithTags("Audit log");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(PlatformPermissions.AuditRead)
            .WithName("ListAuditLogs")
            .WithSummary("Search the audit trail")
            .WithDescription(
                "Newest first. `action` matches by prefix, so `user.` returns every action on " +
                "users. Filter by acting user, entity, or time range. Sensitive values are " +
                "masked at write time and never present to be returned.")
            .Produces<PagedResult<AuditLogListItem>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{auditLogId:guid}", GetAsync)
            .RequireAuthorization(PlatformPermissions.AuditRead)
            .WithName("GetAuditLog")
            .WithSummary("One entry with its before/after values")
            .WithDescription("`oldValues` and `newValues` are JSON objects of the properties that changed.")
            .Produces<AuditLogDetail>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        IDispatcher dispatcher,
        CancellationToken cancellationToken,
        int? page = null,
        int? pageSize = null,
        string? search = null,
        Guid? userId = null,
        string? action = null,
        string? entityType = null,
        Guid? entityId = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        Result<PagedResult<AuditLogListItem>> result = await dispatcher.QueryAsync(
            new ListAuditLogsQuery(
                new PageRequest(page, pageSize, sortBy: null, sortDirection: null, search),
                userId,
                action,
                entityType,
                entityId,
                from,
                to),
            cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> GetAsync(
        Guid auditLogId,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<AuditLogDetail> result =
            await dispatcher.QueryAsync(new GetAuditLogQuery(auditLogId), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }
}
