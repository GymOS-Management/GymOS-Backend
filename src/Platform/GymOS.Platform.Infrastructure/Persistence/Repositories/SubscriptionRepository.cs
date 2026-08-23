using GymOS.BuildingBlocks.Infrastructure.Persistence;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence.Repositories;

/// <summary>EF Core adapter for the <see cref="GymSubscription"/> aggregate.</summary>
internal sealed class SubscriptionRepository : ISubscriptionRepository
{
    private static readonly SubscriptionStatus[] LiveStatuses = SubscriptionStatusTransitions.Live;

    private readonly AppDbContext _dbContext;

    public SubscriptionRepository(AppDbContext dbContext) => _dbContext = dbContext;

    public Task<GymSubscription?> FindLiveByGymAsync(Guid gymId, CancellationToken cancellationToken) =>
        // SingleOrDefault, not FirstOrDefault: the partial unique index guarantees at most one live row,
        // so more than one is a broken invariant that should surface rather than be silently picked from.
        _dbContext.Set<GymSubscription>()
            .SingleOrDefaultAsync(
                subscription => subscription.GymId == gymId && LiveStatuses.Contains(subscription.Status),
                cancellationToken);

    public async Task<GymSubscription?> FindLiveByGymForUpdateAsync(
        Guid gymId,
        CancellationToken cancellationToken)
    {
        // Two statements, for the reason FindByIdForUpdateAsync gives below: EF has no pessimistic-locking
        // operator and cannot compose over raw SQL for an entity with mapped complex properties, so
        // the lock is a plain scalar statement and the aggregate then loads through the normal
        // pipeline on the same connection and transaction.
        //
        // Locks the LIVE row for this gym, whichever it is — keyed on the gym rather than on a known
        // subscription id, because the caller is asking "what does this gym have right now" and does
        // not know the answer yet. FeatureOverrideSweepJob needs this: it decides whether a gym's
        // exceptions are redundant against the live plan, and an upgrade committing between that read
        // and the delete made the verdict stale rather than merely late.
        //
        // Caller must hold an IUnitOfWork transaction, or the lock dies with the first statement.
        await _dbContext.Database.ExecuteSqlAsync(
            $"""
            SELECT 1 FROM platform.gym_subscriptions WITH (UPDLOCK, ROWLOCK)
            WHERE gym_id = {gymId} AND status IN (1, 2, 3)
            """,
            cancellationToken);

        return await FindLiveByGymAsync(gymId, cancellationToken);
    }

    public Task<GymSubscription?> FindByIdAsync(Guid subscriptionId, CancellationToken cancellationToken) =>
        _dbContext.Set<GymSubscription>()
            .SingleOrDefaultAsync(subscription => subscription.Id == subscriptionId, cancellationToken);

    public async Task<GymSubscription?> FindByIdForUpdateAsync(
        Guid subscriptionId,
        CancellationToken cancellationToken)
    {
        // Two statements on purpose. EF has no pessimistic-locking operator, and FromSql cannot carry
        // this entity: EF composes over raw SQL whenever complex properties (Money) are mapped,
        // and the composed projection loses their column names. So the lock is taken as a plain
        // scalar statement — a row lock needs no entity — and the aggregate then loads through
        // the normal pipeline on the same connection and transaction, where the lock is already
        // held. Callers hold an IUnitOfWork transaction, or the lock would die with statement one.
        await _dbContext.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM platform.gym_subscriptions WITH (UPDLOCK, ROWLOCK) WHERE id = {subscriptionId}",
            cancellationToken);

        return await FindByIdAsync(subscriptionId, cancellationToken);
    }

    public void Add(GymSubscription subscription) =>
        _dbContext.Set<GymSubscription>().Add(subscription);

    public void ExpectVersion(GymSubscription subscription, ulong expectedVersion) =>
        _dbContext.Entry(subscription).Property<ulong>(ConcurrencyConventions.RowVersionProperty).OriginalValue = expectedVersion;
}
