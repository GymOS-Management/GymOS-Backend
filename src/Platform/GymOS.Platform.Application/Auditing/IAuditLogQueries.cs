using GymOS.BuildingBlocks.Application.Models;

namespace GymOS.Platform.Application.Auditing;

public sealed record AuditLogListItem(
    Guid Id,
    Guid? UserId,
    string? UserEmail,
    string? IpAddress,
    string Action,
    string? EntityType,
    Guid? EntityId,
    Guid? CorrelationId,
    DateTimeOffset CreatedAtUtc);

/// <param name="OldValues">Raw jsonb, passed through to the console's diff viewer.</param>
public sealed record AuditLogDetail(
    AuditLogListItem Summary,
    string? UserAgent,
    string? OldValues,
    string? NewValues);

public sealed record AuditLogFilter(
    PageRequest Page,
    Guid? UserId,
    string? Action,
    string? EntityType,
    Guid? EntityId,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc);

/// <summary>
/// The bounds of a CSV export (M7 spec §8). Unlike <see cref="AuditLogFilter"/>, <see cref="FromUtc"/>
/// and <see cref="ToUtc"/> are required and there is no paging — an export streams every row the
/// filter matches, oldest first.
/// </summary>
public sealed record AuditLogExportFilter(
    Guid? UserId,
    string? Action,
    string? EntityType,
    Guid? EntityId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc);

/// <summary>
/// Read side of the audit log (AD-7). Read-only by design: there is no write method here
/// and no write endpoint anywhere. Entries are produced by the interceptor and by explicit
/// handler calls, and nothing in the system can amend or remove one — a trail that can be
/// edited is not a trail.
/// </summary>
public interface IAuditLogQueries
{
    Task<PagedResult<AuditLogListItem>> ListAsync(AuditLogFilter filter, CancellationToken cancellationToken);

    Task<AuditLogDetail?> GetAsync(Guid auditLogId, CancellationToken cancellationToken);

    /// <summary>
    /// Every row the filter matches, oldest first (ascending <c>created_at_utc</c> then <c>id</c>) —
    /// the order a CSV export reads naturally and the tie-break that makes it deterministic. Deferred:
    /// the caller controls when rows are pulled from the database, so nothing is materialized here.
    /// </summary>
    IAsyncEnumerable<AuditLogListItem> StreamAsync(AuditLogExportFilter filter, CancellationToken cancellationToken);
}
