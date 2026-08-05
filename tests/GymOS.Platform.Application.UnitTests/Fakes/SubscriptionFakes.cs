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
            plan.CreatedAtUtc,
            RowVersion.ToString(CultureInfo.InvariantCulture)));
    }
}
