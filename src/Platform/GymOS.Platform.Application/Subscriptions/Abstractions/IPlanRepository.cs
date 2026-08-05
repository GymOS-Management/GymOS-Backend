using GymOS.Platform.Domain.Subscriptions;

namespace GymOS.Platform.Application.Subscriptions.Abstractions;

/// <summary>
/// Write-side access to the <see cref="SubscriptionPlan"/> aggregate (AD-3). Prices are part of the
/// aggregate, so every lookup loads them — a plan saved without its prices could drop rows.
/// </summary>
public interface IPlanRepository
{
    Task<SubscriptionPlan?> FindByIdAsync(Guid planId, CancellationToken cancellationToken);

    /// <summary>Loads by code, for the uniqueness check at creation. Codes are upper-cased first.</summary>
    Task<SubscriptionPlan?> FindByCodeAsync(string code, CancellationToken cancellationToken);

    void Add(SubscriptionPlan plan);

    /// <summary>Declares the version the caller believes they are updating (ARCHITECTURE.md §3).</summary>
    void ExpectVersion(SubscriptionPlan plan, uint expectedVersion);

    /// <summary>
    /// Marks the plan itself changed, for a write that only touched a price row — without it the plan's
    /// row version never moves and two concurrent price edits both succeed (API_STANDARDS.md §3).
    /// </summary>
    void MarkAggregateChanged(SubscriptionPlan plan);
}
