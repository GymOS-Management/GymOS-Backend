using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Domain.Gyms;

namespace GymOS.Platform.Application.Gyms.Abstractions;

/// <summary>
/// Write-side access to the <see cref="Gym"/> aggregate. A repository exists because a gym carries real
/// invariants — status transitions, slug immutability, soft delete — that must be loaded and saved whole
/// (AD-3). Read-only projections do not go through it.
/// </summary>
public interface IGymRepository
{
    /// <summary>
    /// Loads the aggregate by id, tracked for mutation.
    ///
    /// Returns a soft-deleted gym too, deliberately: refusing to load it would make "this gym was
    /// deleted" indistinguishable from "no such gym", and the operator needs to be told which.
    /// </summary>
    Task<Gym?> FindByIdAsync(Guid gymId, CancellationToken cancellationToken);

    /// <summary>Loads by slug, for the uniqueness check at provisioning.</summary>
    Task<Gym?> FindBySlugAsync(string slug, CancellationToken cancellationToken);

    /// <summary>The gym's branding, tracked for mutation.</summary>
    Task<GymBranding?> FindBrandingAsync(Guid gymId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether another gym already claims this custom domain. A domain must resolve to exactly one gym,
    /// so this is checked before the write rather than left to a unique index to reject with a database
    /// error nobody can act on.
    /// </summary>
    Task<bool> CustomDomainTakenAsync(string customDomain, Guid exceptGymId, CancellationToken cancellationToken);

    void Add(Gym gym);

    void AddBranding(GymBranding branding);

    /// <summary>
    /// Declares the version the caller believes they are updating, so the write fails if the row changed
    /// since they read it (ARCHITECTURE.md §3).
    ///
    /// The comparison happens at the store, not here: doing it in application code would leave a window
    /// between the check and the update, which is the bug optimistic concurrency exists to close.
    /// <see cref="ConcurrencyConflictException"/> is raised by <see cref="IUnitOfWork.SaveChangesAsync"/>.
    /// </summary>
    void ExpectVersion(Gym gym, ulong expectedVersion);

    /// <summary>
    /// Marks the gym itself as changed, for a write that only touched its branding. Without this the
    /// gym's row version does not move, and two operators editing branding concurrently both succeed —
    /// the child-row gap documented in API_STANDARDS.md §3.
    /// </summary>
    void MarkAggregateChanged(Gym gym);
}
