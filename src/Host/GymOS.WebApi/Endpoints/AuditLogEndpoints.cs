using System.Text;
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

        group.MapGet("/export", ExportAsync)
            .RequireAuthorization(PlatformPermissions.AuditExport)
            .WithName("ExportAuditLogs")
            .WithSummary("Stream the audit trail as CSV")
            .WithDescription(
                "`from` and `to` are required, inclusive, and at most 366 days apart. Same " +
                "filters as the list, no paging; rows stream oldest first. Nine columns; " +
                "`oldValues`/`newValues` are not exported — read them per entry. The export " +
                "itself is recorded in the audit log with the filter and the row count.")
            .Produces<string>(StatusCodes.Status200OK, contentType: "text/csv; charset=utf-8")
            .ProducesProblem(StatusCodes.Status400BadRequest)
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

    /// <summary>
    /// The scoped <see cref="IDispatcher"/>, the DbContext behind it, and the <c>IAuditTrail</c> it
    /// eventually reaches all live for the lifetime of the request scope — which <c>Results.Stream</c>
    /// keeps open for as long as the callback runs. That is what makes dispatching
    /// <see cref="RecordAuditExportCommand"/> from inside the callback, after the rows have been
    /// enumerated, both possible and correct: the row count it records is the number of lines that
    /// actually reached the response body, not one read before the body was sent.
    /// </summary>
    private static async Task<IResult> ExportAsync(
        IDispatcher dispatcher,
        CancellationToken cancellationToken,
        Guid? userId = null,
        string? action = null,
        string? entityType = null,
        Guid? entityId = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        Result<AuditLogExport> result = await dispatcher.QueryAsync(
            new ExportAuditLogsQuery(userId, action, entityType, entityId, from, to),
            cancellationToken);

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        AuditLogExport export = result.Value;
        string fileName = $"audit-logs-{export.Filter.FromUtc:yyyyMMdd}-{export.Filter.ToUtc:yyyyMMdd}.csv";

        return Results.Stream(
            async stream =>
            {
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.NewLine = "\r\n";
                await writer.WriteLineAsync(AuditCsv.Header);

                long count = 0;
                await foreach (AuditLogListItem item in export.Rows.WithCancellation(cancellationToken))
                {
                    await writer.WriteLineAsync(AuditCsv.FormatRow(item));
                    count++;
                }

                await writer.FlushAsync(cancellationToken);
                await dispatcher.SendAsync(new RecordAuditExportCommand(export.Filter, count), cancellationToken);
            },
            "text/csv; charset=utf-8",
            fileName);
    }
}
