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
/// One plan in full.
///
/// <para><paramref name="SubscribedGymCount"/> counts gyms on a **live** subscription to this plan — the
/// number that decides whether withdrawing it is safe. It arrived with M3.2; the plan's feature set is
/// still absent because the feature catalogue is M4, and a field that always reads empty teaches
/// operators to distrust the screen.</para>
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
    string RowVersion);

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
