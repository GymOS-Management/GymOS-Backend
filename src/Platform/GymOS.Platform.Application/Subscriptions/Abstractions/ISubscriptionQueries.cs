namespace GymOS.Platform.Application.Subscriptions.Abstractions;

/// <summary>
/// A gym's subscription as the API reports it.
/// </summary>
/// <param name="PlanCode">
/// Included beside the id so a client can render "PRO_ANNUAL" without a second request — the plan's
/// code is the handle a human recognises.
/// </param>
/// <param name="TrialEndsAtUtc">
/// Null when the plan had no trial. **A past value with status still `Trialing` is expected** until the
/// renewal job lands (M3.4): conversion is an explicit act, and the console shows this date so the state
/// is legible rather than misleading.
/// </param>
/// <param name="CancelAtPeriodEnd">
/// Cancellation is scheduled; service continues to <paramref name="CurrentPeriodEndUtc"/>. Distinct from
/// a cancelled subscription, which has ended.
/// </param>
public sealed record SubscriptionDetail(
    Guid Id,
    Guid GymId,
    Guid PlanId,
    string PlanCode,
    string PlanName,
    string Status,
    string BillingCycle,
    int BillingCycleMonths,
    string Currency,
    decimal Amount,
    DateTimeOffset CurrentPeriodStartUtc,
    DateTimeOffset CurrentPeriodEndUtc,
    DateTimeOffset? TrialEndsAtUtc,
    DateTimeOffset? CanceledAtUtc,
    bool CancelAtPeriodEnd,
    bool AutoRenew,
    string? CancellationReason,
    DateTimeOffset CreatedAtUtc,
    string RowVersion);

/// <summary>
/// A gym's subscription as it appears *inside* the gym detail response — the same facts without the
/// gym id it is already nested under, and without a row version, because the gym's own ETag governs
/// that resource.
/// </summary>
public sealed record GymSubscriptionSummary(
    Guid Id,
    Guid PlanId,
    string PlanCode,
    string PlanName,
    string Status,
    string BillingCycle,
    string Currency,
    decimal Amount,
    DateTimeOffset CurrentPeriodEndUtc,
    DateTimeOffset? TrialEndsAtUtc,
    bool CancelAtPeriodEnd);

public interface ISubscriptionQueries
{
    /// <summary>The gym's live subscription, or null.</summary>
    Task<SubscriptionDetail?> GetLiveForGymAsync(Guid gymId, CancellationToken cancellationToken);

    /// <summary>
    /// Every subscription a gym has ever had, newest first — the record a billing dispute is settled
    /// from, which is why ended subscriptions are never deleted.
    /// </summary>
    Task<IReadOnlyList<SubscriptionDetail>> ListHistoryForGymAsync(
        Guid gymId,
        CancellationToken cancellationToken);
}
