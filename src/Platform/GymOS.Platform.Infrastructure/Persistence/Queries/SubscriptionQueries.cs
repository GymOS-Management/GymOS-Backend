using GymOS.BuildingBlocks.Infrastructure.Persistence;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Gyms;
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

    public async Task<IReadOnlyList<Guid>> ListDueForRenewalAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await _dbContext.Set<GymSubscription>()
            .AsNoTracking()
            .Where(subscription => LiveStatuses.Contains(subscription.Status)
                && (subscription.CurrentPeriodEndUtc <= now
                    || (subscription.Status == SubscriptionStatus.Trialing
                        && subscription.TrialEndsAtUtc <= now))
                // Never bill a deleted tenant. Deletion cancels the live subscription in the same
                // transaction, so this should find nothing to exclude — it is the belt to that
                // braces, because the cost of the sweep being wrong here is money charged to a
                // business that no longer exists.
                && !_dbContext.Set<Gym>()
                    .Any(gym => gym.Id == subscription.GymId && gym.DeletedAtUtc != null))
            .OrderBy(subscription => subscription.CurrentPeriodEndUtc)
            .Select(subscription => subscription.Id)
            .ToListAsync(cancellationToken);

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
                RowVersion = EF.Property<ulong>(subscription, ConcurrencyConventions.RowVersionProperty),
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
