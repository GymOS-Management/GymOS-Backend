using GymOS.Platform.Domain.Features;

namespace GymOS.Platform.Application.Features.Abstractions;

/// <summary>
/// Write-side access to the feature catalogue and per-gym overrides (AD-3).
///
/// <para>No <c>Add</c> and no <c>Remove</c> for <see cref="Feature"/>: keys are seeded, never
/// created or deleted through a use case (§3.5). Overrides are the opposite — they are created and
/// deleted constantly, because an override's absence is exactly "follows the plan".</para>
/// </summary>
public interface IFeatureRepository
{
    Task<Feature?> FindByIdAsync(Guid featureId, CancellationToken cancellationToken);

    Task<Feature?> FindByKeyAsync(string key, CancellationToken cancellationToken);

    /// <summary>The whole catalogue, tracked — twelve rows, and every use case needs all of them.</summary>
    Task<IReadOnlyList<Feature>> ListAllAsync(CancellationToken cancellationToken);

    /// <summary>Declares the version the caller believes they are updating (ARCHITECTURE.md §3).</summary>
    void ExpectVersion(Feature feature, uint expectedVersion);

    /// <summary>Every override of one gym, tracked — the working set for a resolution write.</summary>
    Task<IReadOnlyList<GymFeatureOverride>> ListOverridesForGymAsync(
        Guid gymId,
        CancellationToken cancellationToken);

    void AddOverride(GymFeatureOverride gymFeatureOverride);

    /// <summary>
    /// Removes an exception, so the feature follows the gym's plan again. A hard delete on purpose:
    /// there is no "revoked override" state — absence *is* the meaning (§2.4).
    /// </summary>
    void RemoveOverride(GymFeatureOverride gymFeatureOverride);
}
