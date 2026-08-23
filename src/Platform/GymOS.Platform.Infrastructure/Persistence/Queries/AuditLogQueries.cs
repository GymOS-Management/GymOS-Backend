using GymOS.BuildingBlocks.Application.Models;
using GymOS.BuildingBlocks.Infrastructure.Auditing;
using GymOS.BuildingBlocks.Infrastructure.Persistence;
using GymOS.Platform.Application.Auditing;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence.Queries;

internal sealed class AuditLogQueries : IAuditLogQueries
{
    private readonly AppDbContext _dbContext;

    public AuditLogQueries(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<PagedResult<AuditLogListItem>> ListAsync(
        AuditLogFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        IQueryable<AuditLog> query = _dbContext.Set<AuditLog>().AsNoTracking();

        if (filter.UserId is { } userId)
        {
            query = query.Where(log => log.UserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            // Prefix match, so `user.` finds every action on users without the caller
            // enumerating them (ARCHITECTURE.md Â§2.6 action prefix search).
            //
            // ILIKE rather than `string.StartsWith`, which EF translates to a case-SENSITIVE LIKE
            // with the caller's metacharacters left live. Two defects came out of that, and this
            // file already avoided both a few lines below for its free-text search:
            //
            //   * `Gym.` found nothing at all. Action codes are lower-case by convention, so the
            //     filter demanded the reader know that and type it exactly — a filter that answers
            //     "no entries match" when entries plainly match reads as a broken trail.
            //   * A `%` typed into the filter acted as a wildcard, so it matched rows that do not
            //     start with what was typed. The audit's LIKE-escaping finding, in the one query
            //     that escapes its other parameter and not this one.
            string pattern = SearchPatterns.StartsWith(filter.Action.Trim());
            query = query.Where(log =>
                EF.Functions.Like(log.Action, pattern));
        }

        if (!string.IsNullOrWhiteSpace(filter.EntityType))
        {
            string entityType = filter.EntityType.Trim();
            query = query.Where(log => log.EntityType == entityType);
        }

        if (filter.EntityId is { } entityId)
        {
            query = query.Where(log => log.EntityId == entityId);
        }

        if (filter.FromUtc is { } from)
        {
            query = query.Where(log => log.CreatedAtUtc >= from);
        }

        if (filter.ToUtc is { } to)
        {
            query = query.Where(log => log.CreatedAtUtc <= to);
        }

        if (filter.Page.Search is { } search)
        {
            string pattern = SearchPatterns.Contains(search);
            query = query.Where(log =>
                EF.Functions.Like(log.Action, pattern) ||
                (log.UserEmail != null
                    && EF.Functions.Like(log.UserEmail, pattern)));
        }

        int totalCount = await query.CountAsync(cancellationToken);

        // Newest first, always: the audit log is read to answer "what just happened".
        // Sorting is not caller-configurable, so the descending created_at index always applies.
        var items = await query
            .OrderByDescending(log => log.CreatedAtUtc)
            .ThenBy(log => log.Id)
            .Skip(filter.Page.Skip)
            .Take(filter.Page.PageSize)
            .Select(log => new AuditLogListItem(
                log.Id,
                log.UserId,
                log.UserEmail,
                log.IpAddress,
                log.Action,
                log.EntityType,
                log.EntityId,
                log.CorrelationId,
                log.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return new PagedResult<AuditLogListItem>(items, filter.Page.Page, filter.Page.PageSize, totalCount);
    }

    public Task<AuditLogDetail?> GetAsync(Guid auditLogId, CancellationToken cancellationToken) =>
        _dbContext.Set<AuditLog>()
            .AsNoTracking()
            .Where(log => log.Id == auditLogId)
            .Select(log => new AuditLogDetail(
                new AuditLogListItem(
                    log.Id,
                    log.UserId,
                    log.UserEmail,
                    log.IpAddress,
                    log.Action,
                    log.EntityType,
                    log.EntityId,
                    log.CorrelationId,
                    log.CreatedAtUtc),
                log.UserAgent,
                log.OldValues,
                log.NewValues))
            .SingleOrDefaultAsync(cancellationToken);
}
