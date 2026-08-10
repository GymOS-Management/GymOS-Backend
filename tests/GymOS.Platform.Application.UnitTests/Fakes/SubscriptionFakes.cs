using System.Globalization;
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Models;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Subscriptions;

namespace GymOS.Platform.Application.UnitTests.Fakes;

/// <summary>In-memory stand-ins for the plan seams, behavioural rather than mocks.</summary>
internal sealed class FakePlanRepository : IPlanRepository
{
    private readonly List<SubscriptionPlan> _plans = [];

    public IReadOnlyList<SubscriptionPlan> Plans => _plans;

    public Dictionary<Guid, uint> ExpectedVersions { get; } = [];

    public HashSet<Guid> AggregateChanges { get; } = [];

    /// <summary>Set to make <see cref="ExpectVersion"/> behave as a stale-version write.</summary>
    public uint? ConflictingVersion { get; set; }

    public void Seed(SubscriptionPlan plan) => _plans.Add(plan);

    public Task<SubscriptionPlan?> FindByIdAsync(Guid planId, CancellationToken cancellationToken) =>
        Task.FromResult(_plans.SingleOrDefault(plan => plan.Id == planId));

    public Task<SubscriptionPlan?> FindByCodeAsync(string code, CancellationToken cancellationToken) =>
        Task.FromResult(_plans.SingleOrDefault(plan =>
            string.Equals(plan.Code, code, StringComparison.OrdinalIgnoreCase)));

    public void Add(SubscriptionPlan plan) => _plans.Add(plan);

    public void ExpectVersion(SubscriptionPlan plan, uint expectedVersion)
    {
        ExpectedVersions[plan.Id] = expectedVersion;

        if (ConflictingVersion == expectedVersion)
        {
            throw new ConcurrencyConflictException();
        }
    }

    public void MarkAggregateChanged(SubscriptionPlan plan) => AggregateChanges.Add(plan.Id);
}

/// <summary>
/// Read-side fake, projecting from the same aggregates the write side mutates — so a handler that
/// returns the resource it just changed is checked against real state.
/// </summary>
internal sealed class FakePlanQueries : IPlanQueries
{
    private readonly FakePlanRepository _plans;

    public FakePlanQueries(FakePlanRepository plans) => _plans = plans;

    public uint RowVersion { get; set; } = 1;

    /// <summary>
    /// Gyms the test has declared as subscribed to a plan. Settable rather than derived from a
    /// subscription fake, so a plan test can set up "three gyms are on this" without building a
    /// subscription graph.
    /// </summary>
    public Dictionary<Guid, int> SubscribedGymCounts { get; } = [];

    private int SubscribedGymCount(Guid planId) =>
        SubscribedGymCounts.TryGetValue(planId, out int count) ? count : 0;

    public Task<PagedResult<PlanListItem>> ListAsync(
        PlanListFilter filter,
        CancellationToken cancellationToken)
    {
        IEnumerable<SubscriptionPlan> plans = filter.IncludeInactive
            ? _plans.Plans
            : _plans.Plans.Where(plan => plan.IsActive);

        if (filter.IsPublic is { } isPublic)
        {
            plans = plans.Where(plan => plan.IsPublic == isPublic);
        }

        var items = plans
            .Select(plan => new PlanListItem(
                plan.Id,
                plan.Code,
                plan.Name,
                plan.TrialDays,
                plan.IsActive,
                plan.IsPublic,
                plan.SortOrder,
                plan.Prices.Count,
                SubscribedGymCount(plan.Id),
                plan.CreatedAtUtc))
            .ToList();

        return Task.FromResult(
            new PagedResult<PlanListItem>(items, filter.Page.Page, filter.Page.PageSize, items.Count));
    }

    public Task<PlanDetail?> GetAsync(Guid planId, CancellationToken cancellationToken)
    {
        SubscriptionPlan? plan = _plans.Plans.SingleOrDefault(candidate => candidate.Id == planId);

        if (plan is null)
        {
            return Task.FromResult<PlanDetail?>(null);
        }

        var prices = plan.Prices
            .OrderBy(price => (int)price.BillingCycle)
            .Select(price => new PlanPriceDetail(
                price.Id,
                price.BillingCycle.ToString(),
                price.BillingCycle.Months(),
                price.Price.Currency.Code,
                price.Price.Amount))
            .ToList();

        return Task.FromResult<PlanDetail?>(new PlanDetail(
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
            prices,
            SubscribedGymCount(plan.Id),
            plan.CreatedAtUtc,
            RowVersion.ToString(CultureInfo.InvariantCulture)));
    }
}

/// <summary>In-memory stand-in for the subscription write side.</summary>
internal sealed class FakeSubscriptionRepository : ISubscriptionRepository
{
    private readonly List<GymSubscription> _subscriptions = [];

    public IReadOnlyList<GymSubscription> Subscriptions => _subscriptions;

    public Dictionary<Guid, uint> ExpectedVersions { get; } = [];

    /// <summary>Set to make <see cref="ExpectVersion"/> behave as a stale-version write.</summary>
    public uint? ConflictingVersion { get; set; }

    public void Seed(GymSubscription subscription) => _subscriptions.Add(subscription);

    /// <summary>
    /// Mirrors the partial unique index: at most one live row per gym, so SingleOrDefault here would
    /// throw on the same broken invariant the real repository would.
    /// </summary>
    public Task<GymSubscription?> FindLiveByGymAsync(Guid gymId, CancellationToken cancellationToken) =>
        Task.FromResult(_subscriptions.SingleOrDefault(subscription =>
            subscription.GymId == gymId && subscription.IsLive));

    public Task<GymSubscription?> FindByIdAsync(Guid subscriptionId, CancellationToken cancellationToken) =>
        Task.FromResult(_subscriptions.SingleOrDefault(subscription => subscription.Id == subscriptionId));

    public void Add(GymSubscription subscription) => _subscriptions.Add(subscription);

    public void ExpectVersion(GymSubscription subscription, uint expectedVersion)
    {
        ExpectedVersions[subscription.Id] = expectedVersion;

        if (ConflictingVersion == expectedVersion)
        {
            throw new ConcurrencyConflictException();
        }
    }
}

/// <summary>
/// Read side, projecting from the same aggregates the write side mutates — so a handler that returns the
/// resource it just changed is checked against real state.
/// </summary>
internal sealed class FakeSubscriptionQueries : ISubscriptionQueries
{
    private readonly FakeSubscriptionRepository _subscriptions;
    private readonly FakePlanRepository _plans;

    public FakeSubscriptionQueries(FakeSubscriptionRepository subscriptions, FakePlanRepository plans)
    {
        _subscriptions = subscriptions;
        _plans = plans;
    }

    public uint RowVersion { get; set; } = 1;

    public Task<SubscriptionDetail?> GetLiveForGymAsync(Guid gymId, CancellationToken cancellationToken)
    {
        GymSubscription? subscription = _subscriptions.Subscriptions
            .SingleOrDefault(candidate => candidate.GymId == gymId && candidate.IsLive);

        return Task.FromResult(subscription is null ? null : Project(subscription));
    }

    public Task<IReadOnlyList<SubscriptionDetail>> ListHistoryForGymAsync(
        Guid gymId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SubscriptionDetail>>(
            _subscriptions.Subscriptions
                .Where(subscription => subscription.GymId == gymId)
                .Select(Project)
                .Select(detail => detail!)
                .ToList());

    private SubscriptionDetail? Project(GymSubscription subscription)
    {
        SubscriptionPlan? plan = _plans.Plans.SingleOrDefault(candidate => candidate.Id == subscription.PlanId);

        return new SubscriptionDetail(
            subscription.Id,
            subscription.GymId,
            subscription.PlanId,
            plan?.Code ?? string.Empty,
            plan?.Name ?? string.Empty,
            subscription.Status.ToString(),
            subscription.BillingCycle.ToString(),
            subscription.BillingCycle.Months(),
            subscription.Price.Currency.Code,
            subscription.Price.Amount,
            subscription.CurrentPeriodStartUtc,
            subscription.CurrentPeriodEndUtc,
            subscription.TrialEndsAtUtc,
            subscription.CanceledAtUtc,
            subscription.CancelAtPeriodEnd,
            subscription.AutoRenew,
            subscription.CancellationReason,
            subscription.CreatedAtUtc,
            RowVersion.ToString(CultureInfo.InvariantCulture),
            subscription.PendingChange is not { } pending
                ? null
                : new PendingPlanChangeDetail(
                    pending.PlanId,
                    pending.BillingCycle.ToString(),
                    pending.Amount,
                    pending.CurrencyCode,
                    pending.ChangeAtUtc));
    }
}
