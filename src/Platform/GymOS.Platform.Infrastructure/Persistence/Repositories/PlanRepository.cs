using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core adapter for the <see cref="SubscriptionPlan"/> aggregate. Both lookups include the prices
/// <b>and the features</b>: they are part of the aggregate, and loading a plan without them is not
/// merely incomplete but actively wrong. An unloaded feature list makes <c>SetFeature</c> insert a
/// second row for a key the plan already has — caught by <c>ux_plan_features_plan_feature</c> as a
/// 409 — and makes <c>ClearFeature</c> find nothing to remove. It also made the plan layer invisible
/// to the gym-override write, which then stored exceptions the plan already granted.
/// </summary>
internal sealed class PlanRepository : IPlanRepository
{
    private readonly AppDbContext _dbContext;

    public PlanRepository(AppDbContext dbContext) => _dbContext = dbContext;

    public Task<SubscriptionPlan?> FindByIdAsync(Guid planId, CancellationToken cancellationToken) =>
        _dbContext.Set<SubscriptionPlan>()
            .Include(plan => plan.Prices)
            .Include(plan => plan.Features)
            .SingleOrDefaultAsync(plan => plan.Id == planId, cancellationToken);

    public async Task<SubscriptionPlan?> FindByIdForUpdateAsync(
        Guid planId,
        CancellationToken cancellationToken)
    {
        // Same two-statement shape as ISubscriptionRepository.FindByIdForUpdateAsync, for the same
        // reason: PlanPrice maps a Money complex property, so EF cannot FromSql this entity — the
        // composed projection loses the complex property's column names. The lock is taken as a
        // bare scalar statement instead, and the aggregate then loads through the normal Include
        // pipeline on the same connection and transaction, where the lock is already held. Callers
        // hold an IUnitOfWork transaction, or the lock would die with statement one.
        await _dbContext.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM platform.subscription_plans WHERE id = {planId} FOR UPDATE",
            cancellationToken);

        return await FindByIdAsync(planId, cancellationToken);
    }

    public Task<SubscriptionPlan?> FindByCodeAsync(string code, CancellationToken cancellationToken)
    {
        string normalized = (code ?? string.Empty).Trim().ToUpperInvariant();

        return _dbContext.Set<SubscriptionPlan>()
            .Include(plan => plan.Prices)
            .Include(plan => plan.Features)
            .SingleOrDefaultAsync(plan => plan.Code == normalized, cancellationToken);
    }

    public void Add(SubscriptionPlan plan) => _dbContext.Set<SubscriptionPlan>().Add(plan);

    public void ExpectVersion(SubscriptionPlan plan, uint expectedVersion) =>
        _dbContext.Entry(plan).Property<uint>("xmin").OriginalValue = expectedVersion;

    public void MarkAggregateChanged(SubscriptionPlan plan) =>
        _dbContext.Entry(plan).Property(entity => entity.UpdatedAtUtc).IsModified = true;
}
