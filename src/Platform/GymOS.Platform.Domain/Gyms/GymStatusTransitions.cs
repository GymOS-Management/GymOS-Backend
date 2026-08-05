using GymOS.SharedKernel;

namespace GymOS.Platform.Domain.Gyms;

/// <summary>
/// The gym lifecycle as one explicit transition table (ARCHITECTURE.md §2.2: "status transitions are
/// enforced in the domain entity"), following the pattern <c>UserStatusTransitions</c> established.
///
/// <code>
///   ┌──────────────┐   go live    ┌────────┐   suspend    ┌───────────┐
///   │ Provisioning ├─────────────►│ Active │◄────────────►│ Suspended │
///   └──────┬───────┘              └───┬────┘  reactivate  └─────┬─────┘
///          │                          │                          │
///          └──────────────────────────┴──────────────────────────┘
///                                     │ delete (soft)
///                                     ▼
///                                 ┌─────────┐
///                                 │ Deleted │  terminal
///                                 └─────────┘
/// </code>
///
/// <para><b>Deleted is terminal, deliberately.</b> The frozen design names
/// <c>Deleted → Active</c> as the example of a transition that must not exist. Restoring a deleted gym
/// by flipping a status would silently resurrect a tenant whose data may already have been partly
/// purged, and whose subscription and invoices were closed on the assumption it was gone. Reinstating a
/// gym is an explicit operator decision with its own workflow, not a status change.</para>
/// </summary>
public static class GymStatusTransitions
{
    private static readonly HashSet<(GymStatus From, GymStatus To)> Allowed =
    [
        // Provisioning finished: the gym opens for business.
        (GymStatus.Provisioning, GymStatus.Active),

        // A gym can be suspended before it ever went live — a deal that fell through during setup.
        (GymStatus.Provisioning, GymStatus.Suspended),

        (GymStatus.Active, GymStatus.Suspended),
        (GymStatus.Suspended, GymStatus.Active),

        // Soft delete is reachable from every non-deleted state.
        (GymStatus.Provisioning, GymStatus.Deleted),
        (GymStatus.Active, GymStatus.Deleted),
        (GymStatus.Suspended, GymStatus.Deleted),
    ];

    public static bool IsAllowed(GymStatus from, GymStatus to) => Allowed.Contains((from, to));

    /// <summary>
    /// Throws when the move is not in the table. An invariant breach rather than a business failure:
    /// callers check the current state first, so reaching this is a defect (house pattern).
    /// </summary>
    public static void EnsureAllowed(GymStatus from, GymStatus to)
    {
        if (from == to)
        {
            throw new DomainException($"The gym is already {from}.");
        }

        if (!IsAllowed(from, to))
        {
            throw new DomainException($"A gym cannot move from {from} to {to}.");
        }
    }
}
