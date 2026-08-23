using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Domain.Identity;

namespace GymOS.Platform.Application.Identity.Abstractions;

/// <summary>
/// Write-side access to the <see cref="User"/> aggregate. A repository exists here
/// because User carries real invariants (lockout, activation, stamp rotation) that
/// must be loaded and saved as a whole (AD-3); read-only projections do not go
/// through it.
/// </summary>
public interface IUserRepository
{
    /// <summary>
    /// Loads the aggregate by normalized email (<see cref="User.Normalize"/>),
    /// tracked for mutation. <see langword="null"/> when no such user exists.
    /// </summary>
    Task<User?> FindByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken);

    /// <summary>Loads the aggregate by id, tracked for mutation.</summary>
    Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken);

    void Add(User user);

    /// <summary>
    /// Declares the version the caller believes they are updating, so the write fails if the row
    /// changed since they read it (ARCHITECTURE.md §3).
    ///
    /// <para>The check happens at the store, not here: this records the expectation, and
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> raises
    /// <see cref="ConcurrencyConflictException"/> when the row no longer matches. Comparing in
    /// application code would leave a window between the comparison and the update — which is the
    /// bug optimistic concurrency exists to close.</para>
    /// </summary>
    /// <param name="expectedVersion">A token previously issued by the API for this resource.</param>
    void ExpectVersion(User user, ulong expectedVersion);

    /// <summary>
    /// Marks the aggregate itself as changed, for a write that only touched its children.
    ///
    /// <para>Necessary because a row version protects <i>a row</i>. Granting or revoking a role
    /// inserts and deletes <c>user_roles</c> rows and never touches <c>users</c>, so without this
    /// the user's version does not move and two administrators editing the same role set both
    /// succeed — last-write-wins on the exact operation most likely to be edited concurrently.</para>
    ///
    /// <para>Conceptually it is not a workaround: the aggregate is the consistency boundary, so a
    /// change to any part of it is a change to the aggregate, and its version should say so.</para>
    /// </summary>
    void MarkAggregateChanged(User user);
}
