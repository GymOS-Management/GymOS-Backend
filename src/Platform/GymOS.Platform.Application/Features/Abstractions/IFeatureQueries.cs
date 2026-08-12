namespace GymOS.Platform.Application.Features.Abstractions;

/// <summary>
/// One catalogue row as the API reports it.
/// </summary>
/// <param name="AdoptionCount">
/// How many gyms currently resolve this feature <b>on</b> — counted through the full three-layer
/// rule, not by counting plan rows. That distinction is the point: a feature can be live for a gym
/// because of an override, because of its plan, or because of the platform default, and an adoption
/// figure that only counted one of those would understate reality and mislead the decision it exists
/// to inform ("can we retire this?").
///
/// <para><b>Null on <c>PUT /features/{id}</c>'s read-back.</b> That endpoint returns this same type
/// for one edited row, and computing the count costs a scan over every gym for every feature —
/// paid for by <c>GET /features</c>, whose whole purpose is that number, and not by a caller editing
/// a feature's copy. Null means "not computed here", not "zero": a feature with real adopters does
/// not lose them because its description changed, and reporting <c>0</c> would say otherwise with no
/// way for the caller to tell the difference from a genuine zero.</para>
/// </param>
/// <param name="OverrideCount">Same population and the same null-on-write-response rule as <see cref="AdoptionCount"/>.</param>
/// <param name="IsDeclared">
/// Whether a module still declares this key in <c>PlatformFeatures.All</c>. False for a row a rename
/// left behind: the seeder never deletes an orphan (dropping it would cascade-delete plan contents and
/// gym overrides that reference it), so without this flag the row is indistinguishable from a live
/// feature — editable, counted in adoption, flagged only in a startup log nobody reads at 2am.
/// </param>
public sealed record FeatureListItem(
    Guid Id,
    string Key,
    string Name,
    string Description,
    string Category,
    bool DefaultEnabled,
    bool IsPremium,
    int? AdoptionCount,
    int? OverrideCount,
    string RowVersion,
    bool IsDeclared);

/// <summary>One feature's answer for one gym, with the layer that decided it.</summary>
/// <param name="Source">
/// <c>Override</c>, <c>Plan</c>, or <c>Default</c> — returned because "is POS on?" and "why is POS
/// on?" are the same question in a support conversation.
/// </param>
/// <param name="OverrideSetAtUtc">
/// When the override was set, or restated. Null unless the override decided the answer — the same rule
/// <paramref name="OverrideReason"/> follows. Overrides never expire, so "how old is this grant" is an
/// operator's only compensating control short of the audit log.
/// </param>
/// <param name="OverrideSetByEmail">
/// Who set the override, by email. Null unless the override decided the answer, and also null when the
/// author's account has since been deleted (<c>GymFeatureOverride.SetByUserId</c> is cleared, not the
/// override itself — the exception survives losing its author).
/// </param>
public sealed record ResolvedFeatureDetail(
    Guid FeatureId,
    string Key,
    string Name,
    string Category,
    bool Enabled,
    string Source,
    bool IsPremium,
    string? OverrideReason,
    DateTimeOffset? OverrideSetAtUtc,
    string? OverrideSetByEmail);

/// <summary>Read-side access to features (AD-3): projections only, never aggregates.</summary>
public interface IFeatureQueries
{
    /// <summary>The catalogue, ordered for display, with adoption counts.</summary>
    Task<IReadOnlyList<FeatureListItem>> ListCatalogueAsync(CancellationToken cancellationToken);

    Task<FeatureListItem?> GetAsync(Guid featureId, CancellationToken cancellationToken);

    /// <summary>
    /// Every feature resolved for one gym — the three-layer answer for the whole catalogue, which is
    /// what both <c>GET /gyms/{id}/features</c> and the gym detail block report.
    /// </summary>
    Task<IReadOnlyList<ResolvedFeatureDetail>> ResolveForGymAsync(
        Guid gymId,
        CancellationToken cancellationToken);
}
