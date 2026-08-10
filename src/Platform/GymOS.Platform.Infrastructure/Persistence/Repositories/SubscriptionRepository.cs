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

    public Task<GymSubscription?> FindByIdAsync(Guid subscriptionId, CancellationToken cancellationToken) =>
        _dbContext.Set<GymSubscription>()
            .SingleOrDefaultAsync(subscription => subscription.Id == subscriptionId, cancellationToken);

    public async Task<GymSubscription?> FindByIdForUpdateAsync(
        Guid subscriptionId,
        CancellationToken cancellationToken)
    {
        // Two statements on purpose. EF has no FOR UPDATE operator, and FromSql cannot carry
        // this entity: EF composes over raw SQL whenever complex properties (Money) are mapped,
        // and the composed projection loses their column names. So the lock is taken as a plain
        // scalar statement — a row lock needs no entity — and the aggregate then loads through
        // the normal pipeline on the same connection and transaction, where the lock is already
        // held. Callers hold an IUnitOfWork transaction, or the lock would die with statement one.
        await _dbContext.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM platform.gym_subscriptions WHERE id = {subscriptionId} FOR UPDATE",
            cancellationToken);

        return await FindByIdAsync(subscriptionId, cancellationToken);
    }

    public void Add(GymSubscription subscription) =>
        _dbContext.Set<GymSubscription>().Add(subscription);

    public void ExpectVersion(GymSubscription subscription, uint expectedVersion) =>
        _dbContext.Entry(subscription).Property<uint>("xmin").OriginalValue = expectedVersion;
}
