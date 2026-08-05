namespace GymOS.Platform.Domain.Gyms;

/// <summary>
/// A gym's lifecycle state (ARCHITECTURE.md §2.2). Numeric values are persisted and are therefore
/// stable API — never reorder or reuse them.
/// </summary>
public enum GymStatus
{
    /// <summary>
    /// Created, not yet operating. The gym exists as a tenant so its data can be prepared, but it is
    /// not live. Distinct from Suspended: nothing has been taken away, nothing has started yet.
    /// </summary>
    Provisioning = 1,

    Active = 2,

    /// <summary>
    /// Deliberately withheld by the platform operator — non-payment, breach, or investigation.
    /// Reversible, and the reason is recorded.
    /// </summary>
    Suspended = 3,

    /// <summary>
    /// Soft-deleted. The row and its history remain for the retention window; a purge job performs the
    /// hard delete afterwards (the GDPR erasure path). Terminal from the platform's point of view.
    /// </summary>
    Deleted = 4,
}
