using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core adapter for the <see cref="SubscriptionPlan"/> aggregate. Both lookups include the prices:
/// they are part of the aggregate, and loading a plan without them would let a caller save one back with
/// its price list emptied.
/// </summary>
internal sealed class PlanRepository : IPlanRepository
{
    private readonly AppDbContext _dbContext;

    public PlanRepository(AppDbContext dbContext) => _dbContext = dbContext;

    public Task<SubscriptionPlan?> FindByIdAsync(Guid planId, CancellationToken cancellationToken) =>
        _dbContext.Set<SubscriptionPlan>()
            .Include(plan => plan.Prices)
            .SingleOrDefaultAsync(plan => plan.Id == planId, cancellationToken);

    public Task<SubscriptionPlan?> FindByCodeAsync(string code, CancellationToken cancellationToken)
    {
        string normalized = (code ?? string.Empty).Trim().ToUpperInvariant();

        return _dbContext.Set<SubscriptionPlan>()
            .Include(plan => plan.Prices)
            .SingleOrDefaultAsync(plan => plan.Code == normalized, cancellationToken);
    }

    public void Add(SubscriptionPlan plan) => _dbContext.Set<SubscriptionPlan>().Add(plan);

    public void ExpectVersion(SubscriptionPlan plan, uint expectedVersion) =>
        _dbContext.Entry(plan).Property<uint>("xmin").OriginalValue = expectedVersion;

    public void MarkAggregateChanged(SubscriptionPlan plan) =>
        _dbContext.Entry(plan).Property(entity => entity.UpdatedAtUtc).IsModified = true;
}
