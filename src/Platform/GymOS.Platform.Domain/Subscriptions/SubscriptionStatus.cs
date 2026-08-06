using GymOS.SharedKernel;

namespace GymOS.Platform.Domain.Subscriptions;

/// <summary>
/// Where a gym's subscription stands (ARCHITECTURE.md §2.3). Numeric values are stored and are stable
/// API: renumbering them would reinterpret every existing subscription.
/// </summary>
public enum SubscriptionStatus
{
    /// <summary>Inside the plan's trial. Billing has not started.</summary>
    Trialing = 1,

    Active = 2,

    /// <summary>Payment is late. Service continues — withholding it is a separate decision (gym suspension).</summary>
    PastDue = 3,

    /// <summary>Ended by someone's decision. Terminal.</summary>
    Canceled = 4,

    /// <summary>Ended by time — a period finished with no renewal. Terminal.</summary>
    Expired = 5,
}

/// <summary>
/// The subscription lifecycle as one explicit transition table, the pattern
/// <c>GymStatusTransitions</c> established.
///
/// <code>
///   ┌──────────┐  trial converts  ┌────────┐   payment late   ┌─────────┐
///   │ Trialing ├─────────────────►│ Active │◄────────────────►│ PastDue │
///   └────┬─────┘                  └───┬────┘   payment made   └────┬────┘
///        │                            │                            │
///        └──────────┬─────────────────┴────────────┬───────────────┘
///                   │ cancel                       │ period ended, no renewal
///                   ▼                              ▼
///              ┌──────────┐                   ┌─────────┐
///              │ Canceled │ terminal          │ Expired │ terminal
///              └──────────┘                   └─────────┘
/// </code>
///
/// <para><b>Canceled and Expired are both terminal, and both exist</b> because they answer different
/// questions. Canceled is a decision someone made and is what a churn figure counts; Expired is time
/// running out, which is usually a failure to collect rather than a customer leaving. Collapsing them
/// would make the M5 churn metric wrong in a way nobody could later untangle.</para>
///
/// <para>A terminal subscription is never revived — a gym that comes back gets a new subscription row,
/// so the old one stays as the record of what it actually had. Same reasoning as a deleted gym.</para>
/// </summary>
public static class SubscriptionStatusTransitions
{
    private static readonly HashSet<(SubscriptionStatus From, SubscriptionStatus To)> Allowed =
    [
        // The trial ended and the gym is paying.
        (SubscriptionStatus.Trialing, SubscriptionStatus.Active),

        // A trial can go straight past due: the first charge after it can fail like any other.
        (SubscriptionStatus.Trialing, SubscriptionStatus.PastDue),

        (SubscriptionStatus.Active, SubscriptionStatus.PastDue),
        (SubscriptionStatus.PastDue, SubscriptionStatus.Active),

        // Cancellation is reachable from every live state.
        (SubscriptionStatus.Trialing, SubscriptionStatus.Canceled),
        (SubscriptionStatus.Active, SubscriptionStatus.Canceled),
        (SubscriptionStatus.PastDue, SubscriptionStatus.Canceled),

        // So is running out of time.
        (SubscriptionStatus.Trialing, SubscriptionStatus.Expired),
        (SubscriptionStatus.Active, SubscriptionStatus.Expired),
        (SubscriptionStatus.PastDue, SubscriptionStatus.Expired),
    ];

    /// <summary>Statuses that count as a live subscription — the set the one-per-gym rule applies to.</summary>
    public static readonly SubscriptionStatus[] Live =
    [
        SubscriptionStatus.Trialing,
        SubscriptionStatus.Active,
        SubscriptionStatus.PastDue,
    ];

    public static bool IsLive(SubscriptionStatus status) => Live.Contains(status);

    public static bool IsAllowed(SubscriptionStatus from, SubscriptionStatus to) =>
        Allowed.Contains((from, to));

    /// <summary>
    /// Throws when the move is not in the table. An invariant breach rather than a business failure:
    /// callers check the current state first, so reaching this is a defect (house pattern).
    /// </summary>
    public static void EnsureAllowed(SubscriptionStatus from, SubscriptionStatus to)
    {
        if (from == to)
        {
            throw new DomainException($"The subscription is already {from}.");
        }

        if (!IsAllowed(from, to))
        {
            throw new DomainException($"A subscription cannot move from {from} to {to}.");
        }
    }
}
