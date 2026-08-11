namespace GymOS.BuildingBlocks.Application.Abstractions;

/// <summary>
/// A named, cross-instance lock that callers <b>wait</b> for — the tool for a constraint over a
/// <em>set</em> of rows, which row-level optimistic concurrency cannot express.
///
/// <para><b>Why this exists.</b> Optimistic tokens protect one row: two writers to the same row
/// collide and one loses. They say nothing about an invariant like "at least one active SuperAdmin
/// must remain", where each writer touches a <em>different</em> row and both pass a count taken
/// before either committed. Check-then-act over a set is only safe if the checks are serialized,
/// so this is a lock held from the check to the commit.</para>
///
/// <para>Distinct from <c>IJobLock</c>, which is try-acquire-or-skip: a job that loses its tick
/// simply runs later, while a request that loses this must wait and then see the truth. Held for
/// the duration of the ambient transaction, so callers must have one open.</para>
/// </summary>
public interface IAdvisoryLock
{
    /// <summary>
    /// Waits for the named lock, which is then held until the ambient transaction ends. Names are
    /// namespaced by the implementation; callers pass a stable domain name such as
    /// <c>"identity.superadmins"</c>.
    /// </summary>
    Task AcquireAsync(string name, CancellationToken cancellationToken);
}
