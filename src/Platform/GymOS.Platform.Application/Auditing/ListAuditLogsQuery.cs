using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Models;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Auditing;

[RequiresPermission(PlatformPermissions.AuditRead)]
public sealed record ListAuditLogsQuery(
    PageRequest Page,
    Guid? UserId,
    string? Action,
    string? EntityType,
    Guid? EntityId,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc) : IQuery<Result<PagedResult<AuditLogListItem>>>;

internal sealed class ListAuditLogsQueryHandler
    : IQueryHandler<ListAuditLogsQuery, Result<PagedResult<AuditLogListItem>>>
{
    private readonly IAuditLogQueries _auditLogQueries;

    public ListAuditLogsQueryHandler(IAuditLogQueries auditLogQueries) => _auditLogQueries = auditLogQueries;

    public async Task<Result<PagedResult<AuditLogListItem>>> HandleAsync(
        ListAuditLogsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        PagedResult<AuditLogListItem> page = await _auditLogQueries.ListAsync(
            new AuditLogFilter(
                query.Page,
                query.UserId,
                query.Action,
                query.EntityType,
                query.EntityId,
                query.FromUtc,
                query.ToUtc),
            cancellationToken);

        return Result.Success(page);
    }
}

[RequiresPermission(PlatformPermissions.AuditRead)]
public sealed record GetAuditLogQuery(Guid AuditLogId) : IQuery<Result<AuditLogDetail>>;

internal sealed class GetAuditLogQueryHandler : IQueryHandler<GetAuditLogQuery, Result<AuditLogDetail>>
{
    private readonly IAuditLogQueries _auditLogQueries;

    public GetAuditLogQueryHandler(IAuditLogQueries auditLogQueries) => _auditLogQueries = auditLogQueries;

    public async Task<Result<AuditLogDetail>> HandleAsync(
        GetAuditLogQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        AuditLogDetail? entry = await _auditLogQueries.GetAsync(query.AuditLogId, cancellationToken);

        return entry is null
            ? Result.Failure<AuditLogDetail>(
                Error.NotFound("audit.not_found", $"No audit entry with id '{query.AuditLogId}' exists."))
            : Result.Success(entry);
    }
}
