using GymOS.BuildingBlocks.Application.Models;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;
using RowVersion = GymOS.BuildingBlocks.Application.Abstractions.RowVersion;

namespace GymOS.Platform.Infrastructure.Persistence.Queries;

internal sealed class PlanQueries : IPlanQueries
{
    /// <summary>Sortable columns, by request value — an allowlist, for the reason in <c>GymQueries</c>.</summary>
    private static readonly string[] SortableFields = ["name", "code", "sortOrder", "createdAtUtc"];

    private readonly AppDbContext _dbContext;

    public PlanQueries(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<PagedResult<PlanListItem>> ListAsync(
        PlanListFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        IQueryable<SubscriptionPlan> query = _dbContext.Set<SubscriptionPlan>().AsNoTracking();

        if (!filter.IncludeInactive)
        {
            query = query.Where(plan => plan.IsActive);
        }

        if (filter.IsPublic is { } isPublic)
        {
            query = query.Where(plan => plan.IsPublic == isPublic);
        }

        if (filter.Page.Search is { } search)
        {
            string pattern = $"%{search}%";
            query = query.Where(plan =>
                EF.Functions.ILike(plan.Name, pattern) || EF.Functions.ILike(plan.Code, pattern));
        }

        int totalCount = await query.CountAsync(cancellationToken);

        // Anonymous projection then in-memory mapping: positional record constructors and a
        // correlated count do not both translate cleanly.
        var page = await Sort(query, filter.Page)
            .Skip(filter.Page.Skip)
            .Take(filter.Page.PageSize)
            .Select(plan => new
            {
                plan.Id,
                plan.Code,
                plan.Name,
                plan.TrialDays,
                plan.IsActive,
                plan.IsPublic,
                plan.SortOrder,
                PriceCount = plan.Prices.Count,
                plan.CreatedAtUtc,
            })
            .ToListAsync(cancellationToken);

        var items = page
            .Select(plan => new PlanListItem(
                plan.Id,
                plan.Code,
                plan.Name,
                plan.TrialDays,
                plan.IsActive,
                plan.IsPublic,
                plan.SortOrder,
                plan.PriceCount,
                plan.CreatedAtUtc))
            .ToList();

        return new PagedResult<PlanListItem>(items, filter.Page.Page, filter.Page.PageSize, totalCount);
    }

    public async Task<PlanDetail?> GetAsync(Guid planId, CancellationToken cancellationToken)
    {
        var projection = await _dbContext.Set<SubscriptionPlan>()
            .AsNoTracking()
            .Where(plan => plan.Id == planId)
            .Select(plan => new
            {
                plan.Id,
                plan.Code,
                plan.Name,
                plan.Description,
                plan.TrialDays,
                plan.MaxMembers,
                plan.MaxTrainers,
                plan.MaxStaff,
                plan.StorageLimitMb,
                plan.IsActive,
                plan.IsPublic,
                plan.SortOrder,
                plan.CreatedAtUtc,
                RowVersion = EF.Property<uint>(plan, "xmin"),
                Prices = plan.Prices
                    .Select(price => new
                    {
                        price.Id,
                        price.BillingCycle,
                        Currency = price.Price.Currency,
                        Amount = price.Price.Amount,
                    })
                    .ToList(),
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (projection is null)
        {
            return null;
        }

        var prices = projection.Prices
            // Ordered by period length, so a price list reads monthly → quarterly → yearly rather than
            // in whatever order the rows were inserted.
            .OrderBy(price => (int)price.BillingCycle)
            .ThenBy(price => price.Currency.Code, StringComparer.Ordinal)
            .Select(price => new PlanPriceDetail(
                price.Id,
                price.BillingCycle.ToString(),
                price.BillingCycle.Months(),
                price.Currency.Code,
                price.Amount))
            .ToList();

        return new PlanDetail(
            projection.Id,
            projection.Code,
            projection.Name,
            projection.Description,
            projection.TrialDays,
            projection.MaxMembers,
            projection.MaxTrainers,
            projection.MaxStaff,
            projection.StorageLimitMb,
            projection.IsActive,
            projection.IsPublic,
            projection.SortOrder,
            prices,
            projection.CreatedAtUtc,
            RowVersion.ToToken(projection.RowVersion));
    }

    private static IQueryable<SubscriptionPlan> Sort(IQueryable<SubscriptionPlan> query, PageRequest page) =>
        (page.SortBy is not null && SortableFields.Contains(page.SortBy, StringComparer.OrdinalIgnoreCase)
            ? page.SortBy.ToLowerInvariant() switch
            {
                "name" => page.Descending
                    ? query.OrderByDescending(plan => plan.Name)
                    : query.OrderBy(plan => plan.Name),
                "code" => page.Descending
                    ? query.OrderByDescending(plan => plan.Code)
                    : query.OrderBy(plan => plan.Code),
                "createdatutc" => page.Descending
                    ? query.OrderByDescending(plan => plan.CreatedAtUtc)
                    : query.OrderBy(plan => plan.CreatedAtUtc),
                _ => page.Descending
                    ? query.OrderByDescending(plan => plan.SortOrder)
                    : query.OrderBy(plan => plan.SortOrder),
            }
            // A catalogue's natural order is the one the operator chose, not creation time.
            : query.OrderBy(plan => plan.SortOrder).ThenBy(plan => plan.Name))
        .ThenBy(plan => plan.Id);
}
