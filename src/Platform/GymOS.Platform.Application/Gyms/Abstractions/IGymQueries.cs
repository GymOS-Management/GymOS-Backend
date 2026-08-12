using GymOS.BuildingBlocks.Application.Models;
using GymOS.Platform.Application.Features.Abstractions;
using GymOS.Platform.Application.Subscriptions.Abstractions;

namespace GymOS.Platform.Application.Gyms.Abstractions;

/// <summary>One row of the gyms list.</summary>
public sealed record GymListItem(
    Guid Id,
    string Slug,
    string Name,
    string Status,
    string CountryCode,
    string Currency,
    string OwnerName,
    string OwnerEmail,
    DateTimeOffset CreatedAtUtc);

public sealed record GymBrandingDetail(
    string? LogoUrl,
    string? FaviconUrl,
    string? PrimaryColour,
    string? SecondaryColour,
    string? CustomDomain,
    string? EmailSenderName);

/// <summary>
/// One gym in full.
///
/// <para>§3.3 describes the detail view as carrying the active subscription, resolved feature flags, a
/// device summary, and aggregate stats. **The subscription (M3.2) and the resolved features (M4) now
/// arrive.** Stats (M5) and devices (M6) are still absent rather than present and empty — a field that
/// always reads zero teaches operators to distrust the screen, and adding one later is compatible
/// within v1.</para>
/// </summary>
/// <param name="RowVersion">
/// Opaque version token. Echo it in <c>If-Match</c> when updating (API_STANDARDS.md §3).
/// </param>
public sealed record GymDetail(
    Guid Id,
    string Slug,
    string Name,
    string Status,
    string? SuspensionReason,
    string OwnerName,
    string OwnerEmail,
    string? Phone,
    string CountryCode,
    string TimeZoneId,
    string Currency,
    DateTimeOffset? DeletedAtUtc,
    DateTimeOffset CreatedAtUtc,
    GymBrandingDetail Branding,
    /// <summary>The gym's live subscription, or null when it has none — a gym can exist unsubscribed.</summary>
    GymSubscriptionSummary? Subscription,

    /// <summary>
    /// Every feature resolved for this gym, each carrying the layer that decided it. Always the whole
    /// catalogue, never only the exceptions: "what can this gym actually do" is the question an
    /// operator opens this screen with, and a list of overrides answers a different one.
    ///
    /// <para><b>Null when the caller does not hold <c>features.read</c></b> (M4.1 task 4). One entry
    /// carries <c>overrideReason</c> — the free text an operator writes when granting a paid
    /// capability for free, commercially sensitive on its own — so a caller who only holds
    /// <c>gyms.read</c> gets the rest of the gym's detail without it, exactly as the dedicated
    /// <c>GET /gyms/{id}/features</c> route already requires at both its endpoint policy and its
    /// request type. The permission is checked in Application, by <c>GetGymQueryHandler</c> and by
    /// <c>GymWrites.ReadBackAsync</c> — the two production callers of <see cref="IGymQueries.GetAsync"/>
    /// — never inside this read side itself (AD-3): a query object asking
    /// <c>IPermissionEvaluator</c> would put an authorization decision one layer further from
    /// <c>AuthorizationBehavior</c> than every other permission check in the codebase.</para>
    /// </summary>
    IReadOnlyList<ResolvedFeatureDetail>? Features,
    string RowVersion);

/// <param name="Status">Optional exact-match filter on <c>GymStatus</c>, by name.</param>
/// <param name="CountryCode">Optional exact-match filter, case-insensitive.</param>
/// <param name="IncludeDeleted">
/// Soft-deleted gyms are excluded by default. An operator looking at "the gyms" means the live ones; the
/// deleted are found deliberately, which is also what stops a deleted tenant being counted in a figure
/// someone reports upward.
/// </param>
public sealed record GymListFilter(
    PageRequest Page,
    string? Status,
    string? CountryCode,
    bool IncludeDeleted);

/// <summary>Read side of the gyms surface (AD-3): projections, no tracking, no aggregate loading.</summary>
public interface IGymQueries
{
    Task<PagedResult<GymListItem>> ListAsync(GymListFilter filter, CancellationToken cancellationToken);

    /// <param name="includeFeatures">
    /// Whether to resolve and attach the feature block. A plain flag, decided by the caller rather
    /// than by this method: <c>features.read</c> is an authorization concept, and the read side
    /// (AD-3) has never known about those — every permission decision in this codebase sits at the
    /// endpoint policy, on the request type, and in <c>AuthorizationBehavior</c>, all in Application.
    /// <c>GetGymQueryHandler</c> and <c>GymWrites.ReadBackAsync</c> — the two production callers —
    /// each ask the same <c>IPermissionEvaluator</c> that pipeline uses, so the two routes cannot
    /// disagree about who holds it even though neither asks this method to decide.
    /// </param>
    Task<GymDetail?> GetAsync(Guid gymId, bool includeFeatures, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the gym exists at all, deleted or not. For handlers that need to tell "no such gym"
    /// from an empty answer without paying for the whole detail projection — which now resolves the
    /// feature catalogue, and would do it twice on a request that goes on to resolve it itself.
    /// </summary>
    Task<bool> ExistsAsync(Guid gymId, CancellationToken cancellationToken);
}
