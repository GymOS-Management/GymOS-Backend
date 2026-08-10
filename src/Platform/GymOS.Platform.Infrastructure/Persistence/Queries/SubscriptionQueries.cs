using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;
using RowVersion = GymOS.BuildingBlocks.Application.Abstractions.RowVersion;

namespace GymOS.Platform.Infrastructure.Persistence.Queries;

internal sealed class SubscriptionQueries : ISubscriptionQueries
{
    private static readonly SubscriptionStatus[] LiveStatuses = SubscriptionStatusTransitions.Live;

    private readonly AppDbContext _dbContext;

    public SubscriptionQueries(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<SubscriptionDetail?> GetLiveForGymAsync(
        Guid gymId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SubscriptionDetail> matches = await ProjectAsync(
            query => query.Where(subscription =>
                subscription.GymId == gymId && LiveStatuses.Contains(subscription.Status)),
            cancellationToken);

        return matches.Count == 0 ? null : matches[0];
    }

    public Task<IReadOnlyList<SubscriptionDetail>> ListHistoryForGymAsync(
        Guid gymId,
        CancellationToken cancellationToken) =>
        ProjectAsync(
            query => query
                .Where(subscription => subscription.GymId == gymId)
                .OrderByDescending(subscription => subscription.CreatedAtUtc),
            cancellationToken);

    /// <summary>
    /// One projection for both reads. The plan is joined for its code and name, because the question a
    /// reader asks is "which plan", and an id is not an answer a human can use.
    /// </summary>
    private async Task<IReadOnlyList<SubscriptionDetail>> ProjectAsync(
        Func<IQueryable<GymSubscription>, IQueryable<GymSubscription>> filter,
        CancellationToken cancellationToken)
    {
        IQueryable<GymSubscription> query = filter(_dbContext.Set<GymSubscription>().AsNoTracking());

        var rows = await query
            .Select(subscription => new
            {
                subscription.Id,
                subscription.GymId,
                subscription.PlanId,
                subscription.Status,
                subscription.BillingCycle,
                Currency = subscription.Price.Currency,
                Amount = subscription.Price.Amount,
                subscription.CurrentPeriodStartUtc,
                subscription.CurrentPeriodEndUtc,
                subscription.TrialEndsAtUtc,
                subscription.CanceledAtUtc,
                subscription.CancelAtPeriodEnd,
                subscription.AutoRenew,
                subscription.CancellationReason,
                subscription.CreatedAtUtc,
                subscription.PendingChange,
                RowVersion = EF.Property<uint>(subscription, "xmin"),
                PlanCode = _dbContext.Set<SubscriptionPlan>()
                    .Where(plan => plan.Id == subscription.PlanId)
                    .Select(plan => plan.Code)
                    .FirstOrDefault(),
                PlanName = _dbContext.Set<SubscriptionPlan>()
                    .Where(plan => plan.Id == subscription.PlanId)
                    .Select(plan => plan.Name)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new SubscriptionDetail(
                row.Id,
                row.GymId,
                row.PlanId,
                row.PlanCode ?? string.Empty,
                row.PlanName ?? string.Empty,
                row.Status.ToString(),
                row.BillingCycle.ToString(),
                row.BillingCycle.Months(),
                row.Currency.Code,
                row.Amount,
                row.CurrentPeriodStartUtc,
                row.CurrentPeriodEndUtc,
                row.TrialEndsAtUtc,
                row.CanceledAtUtc,
                row.CancelAtPeriodEnd,
                row.AutoRenew,
                row.CancellationReason,
                row.CreatedAtUtc,
                RowVersion.ToToken(row.RowVersion),
                row.PendingChange is not { } pending
                    ? null
                    : new PendingPlanChangeDetail(
                        pending.PlanId,
                        pending.BillingCycle.ToString(),
                        pending.Amount,
                        pending.CurrencyCode,
                        pending.ChangeAtUtc)))
            .ToList();
    }
}
