using GymOS.BuildingBlocks.Application.Models;

namespace GymOS.Platform.Application.Subscriptions.Abstractions;

/// <param name="Amount">
/// The figure, with <paramref name="Currency"/> beside it. Sent as a decimal plus an ISO code rather
/// than a formatted string: the reader's locale decides presentation (a Cypriot operator and a British
/// one write €1.000,50 differently), and a pre-formatted amount cannot be summed by a client.
/// </param>
/// <param name="BillingCycleMonths">
/// The cycle's length, sent alongside its name so a client can sort by period or compute a monthly
/// equivalent without hard-coding that Quarterly means three.
/// </param>
public sealed record PlanPriceDetail(
    Guid Id,
    string BillingCycle,
    int BillingCycleMonths,
    string Currency,
    decimal Amount);

public sealed record PlanListItem(
    Guid Id,
    string Code,
    string Name,
    int TrialDays,
    bool IsActive,
    bool IsPublic,
    int SortOrder,
    int PriceCount,
    int SubscribedGymCount,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// What one plan says about one feature — present only when the plan has an opinion at all.
/// </summary>
public sealed record PlanFeatureDetail(
    Guid FeatureId,
    string Key,
    string Name,
    string Category,
    bool IsEnabled,
    bool IsPremium);

/// <summary>
/// One plan in full.
///
/// <para><paramref name="SubscribedGymCount"/> counts gyms on a **live** subscription to this plan — the
/// number that decides whether withdrawing it is safe (M3.2).</para>
///
/// <para><paramref name="Features"/> arrived with M4, when the catalogue it points at came to exist.
/// It lists only the features this plan has an <b>opinion</b> about: a feature absent from the list
/// is not "excluded", it is unmentioned, and gyms on the plan fall through to the platform default.
/// That third state is why resolution has three layers rather than two.</para>
/// </summary>
/// <param name="MaxMembers">Null means unlimited, throughout.</param>
/// <param name="RowVersion">Echo in <c>If-Match</c> when updating (API_STANDARDS.md §3).</param>
public sealed record PlanDetail(
    Guid Id,
    string Code,
    string Name,
    string Description,
    int TrialDays,
    int? MaxMembers,
    int? MaxTrainers,
    int? MaxStaff,
    long? StorageLimitMb,
    bool IsActive,
    bool IsPublic,
    int SortOrder,
    IReadOnlyCollection<PlanPriceDetail> Prices,
    int SubscribedGymCount,
    DateTimeOffset CreatedAtUtc,
    string RowVersion,
    IReadOnlyCollection<PlanFeatureDetail> Features);

/// <param name="IncludeInactive">
/// Withdrawn plans are excluded by default: the catalogue an operator means is the one they can sell
/// from. They are found deliberately, the same convention as deleted gyms.
/// </param>
/// <param name="IsPublic">Optional filter — null returns both listed and unlisted plans.</param>
public sealed record PlanListFilter(PageRequest Page, bool IncludeInactive, bool? IsPublic);

/// <summary>Read side of the plan catalogue (AD-3): projections, no tracking.</summary>
public interface IPlanQueries
{
    Task<PagedResult<PlanListItem>> ListAsync(PlanListFilter filter, CancellationToken cancellationToken);

    Task<PlanDetail?> GetAsync(Guid planId, CancellationToken cancellationToken);
}
