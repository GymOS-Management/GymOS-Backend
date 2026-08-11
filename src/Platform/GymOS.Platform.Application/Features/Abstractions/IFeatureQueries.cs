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
/// </param>
public sealed record FeatureListItem(
    Guid Id,
    string Key,
    string Name,
    string Description,
    string Category,
    bool DefaultEnabled,
    bool IsPremium,
    int AdoptionCount,
    int OverrideCount,
    string RowVersion);

/// <summary>One feature's answer for one gym, with the layer that decided it.</summary>
/// <param name="Source">
/// <c>Override</c>, <c>Plan</c>, or <c>Default</c> — returned because "is POS on?" and "why is POS
/// on?" are the same question in a support conversation.
/// </param>
public sealed record ResolvedFeatureDetail(
    Guid FeatureId,
    string Key,
    string Name,
    string Category,
    bool Enabled,
    string Source,
    bool IsPremium,
    string? OverrideReason);

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
