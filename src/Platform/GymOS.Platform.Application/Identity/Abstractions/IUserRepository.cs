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
    /// Counts one failed sign-in against <paramref name="user"/> at the store, atomically, and
    /// reflects the result onto the aggregate (<see cref="User.ReflectFailedLogin"/>): the streak
    /// advances by one, and the attempt that reaches <paramref name="maxAttempts"/> locks the
    /// account until <paramref name="now"/> + <paramref name="lockoutDuration"/> and resets the
    /// streak. An account the store already shows as locked out at <paramref name="now"/> is left
    /// exactly as it is — a lockout is not extended by the guesses that arrive during it.
    ///
    /// <para>This is the one write to the aggregate that deliberately does <b>not</b> ride its row
    /// version. The counter is a tally, not an edit: five wrong guesses arriving together are five
    /// attempts on the account, and an optimistic token would let exactly one of them count and
    /// answer the other four with a conflict — on a sign-in endpoint, which must never say anything
    /// but "invalid credentials" (audit finding 7). Implementations perform the increment and the
    /// threshold test in a single statement so no two attempts can read the same streak.</para>
    ///
    /// <para>The store's row version does still advance, as it does on any update. The aggregate
    /// is left tracked and unchanged against the row as it now stands, so the caller's subsequent
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> — the audit entry and the lockout event — is not
    /// itself refused. Callers must not have pending changes on <paramref name="user"/>: a failed
    /// attempt is judged before anything else about the account is touched.</para>
    /// </summary>
    Task RecordFailedLoginAsync(
        User user,
        DateTimeOffset now,
        int maxAttempts,
        TimeSpan lockoutDuration,
        CancellationToken cancellationToken);

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
