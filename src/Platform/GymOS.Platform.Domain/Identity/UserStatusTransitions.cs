using GymOS.SharedKernel;

namespace GymOS.Platform.Domain.Identity;

/// <summary>
/// The account lifecycle, as one explicit transition table (ARCHITECTURE.md §2.1).
///
/// <para>The rules were already enforced before this type existed, but they were spread across
/// <see cref="User.SetPasswordHash"/>, <see cref="User.Suspend"/>, and
/// <see cref="User.Reactivate"/> — each guarding its own case. Collected here they can be read,
/// reviewed, and exhaustively tested as a matrix, and a future transition cannot be added by
/// editing one method and forgetting the others.</para>
///
/// <code>
///   ┌──────────────────┐  password set    ┌────────┐
///   │ PendingActivation├─────────────────►│ Active │
///   └────────┬─────────┘                  └───┬────┘
///            │                                │
///            │ suspend (revoke invitation)    │ suspend
///            ▼                                ▼
///        ┌───────────────────────────────────────┐
///        │              Suspended                │
///        └───┬───────────────────────────────┬───┘
///            │ reactivate (password set)     │ reactivate (no password)
///            ▼                               ▼
///         Active                       PendingActivation
/// </code>
///
/// <para><b>Why there is no <c>Locked</c> state.</b> Lockout after repeated failed sign-ins is
/// a <i>time window</i>, not a lifecycle state: it is represented by
/// <see cref="User.LockedOutUntilUtc"/> and evaluated with <see cref="User.IsLockedOut"/>.
/// Modelling it as a status would mean (a) a stored value that becomes a lie the moment the
/// clock passes the expiry, unless a job exists solely to unlock accounts, (b) remembering the
/// state to return to, since a lockout can interrupt either Active or PendingActivation, and
/// (c) writing to the users table on the sign-in read path. A self-expiring condition derived
/// from a timestamp has none of those problems, which is why the approved schema models it that
/// way and why ASP.NET Core Identity does too.</para>
///
/// <para><b>Why there is no <c>Deleted</c> state.</b> The approved design gives soft delete to
/// <i>gyms</i> (<c>deleted_at</c> plus a purge job for the GDPR erasure path) and deliberately
/// does not give it to platform administrators: §3.6 offers suspend, not delete, and the audit
/// trail denormalizes <c>user_email</c> precisely so history survives an account that no longer
/// exists. Suspension already removes all access permanently. Adding a Deleted status would be
/// a change to a frozen decision and needs a superseding ADR — with the erasure semantics, the
/// audit-retention interaction, and the effect on <c>users.manage</c> worked out first, not a
/// new enum member.</para>
/// </summary>
public static class UserStatusTransitions
{
    /// <summary>
    /// Every legal move. Absence is the rule: a pair not listed here is refused, so adding a
    /// state without deciding its transitions makes that state unreachable rather than silently
    /// permissive.
    /// </summary>
    private static readonly HashSet<(UserStatus From, UserStatus To)> Allowed =
    [
        // Activation: the invitee sets their first password.
        (UserStatus.PendingActivation, UserStatus.Active),

        // An invitation can be revoked before it is ever used.
        (UserStatus.PendingActivation, UserStatus.Suspended),

        // Administrator action, or a refresh that finds the account no longer entitled.
        (UserStatus.Active, UserStatus.Suspended),

        // Reactivation returns the account to where it was: Active if a password exists,
        // PendingActivation if it never did — so lifting a suspension never hands out a
        // working login the owner never had.
        (UserStatus.Suspended, UserStatus.Active),
        (UserStatus.Suspended, UserStatus.PendingActivation),
    ];

    public static bool IsAllowed(UserStatus from, UserStatus to) => Allowed.Contains((from, to));

    /// <summary>
    /// Throws when the move is not in the table. A rejected transition is an invariant breach,
    /// not a business outcome — callers are expected to have checked the current state first,
    /// so reaching this is a defect (house pattern: <see cref="DomainException"/> for
    /// invariants, <c>Result</c> for business failures).
    /// </summary>
    public static void EnsureAllowed(UserStatus from, UserStatus to)
    {
        if (from == to)
        {
            throw new DomainException($"The user is already {from}.");
        }

        if (!IsAllowed(from, to))
        {
            throw new DomainException($"A user cannot move from {from} to {to}.");
        }
    }
}
