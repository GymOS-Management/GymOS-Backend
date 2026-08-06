using GymOS.BuildingBlocks.Application.Models;
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
/// device summary, and aggregate stats. **The subscription now arrives** (M3.2). Features (M4), stats
/// (M5), and devices (M6) are still absent rather than present and empty — a field that always reads
/// zero teaches operators to distrust the screen, and adding one later is compatible within v1.</para>
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

    Task<GymDetail?> GetAsync(Guid gymId, CancellationToken cancellationToken);
}
