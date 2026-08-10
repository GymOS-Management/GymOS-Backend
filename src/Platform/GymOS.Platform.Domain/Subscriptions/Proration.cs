using GymOS.SharedKernel;
using GymOS.SharedKernel.Currencies;

namespace GymOS.Platform.Domain.Subscriptions;

/// <summary>
/// What a mid-period plan change is worth: the unused part of what the gym already paid, against the
/// part of the new plan it is about to use.
///
/// <para>The two figures come from the <b>same day counts</b>, which is what makes
/// <see cref="Net"/> sign-stable — a credit computed over one split and a charge over another could
/// net to the wrong side of zero and route an upgrade into the downgrade path.</para>
/// </summary>
/// <param name="UnusedOldPlanCredit">The old plan's price for the days remaining. Always positive.</param>
/// <param name="NewPlanRemainderCharge">The new plan's price for the same days. Always positive.</param>
/// <param name="Net">
/// Charge minus credit. Its <b>sign is the policy</b>: at or above zero the change is applied now and
/// invoiced; below zero it is scheduled for the end of the period (see <see cref="PendingPlanChange"/>).
/// </param>
public sealed record ProrationResult(
    Money UnusedOldPlanCredit,
    Money NewPlanRemainderCharge,
    Money Net,
    int DaysUsed,
    int DaysRemaining);

/// <summary>
/// Daily proration over a subscription period.
///
/// <para>Pure and static: it takes the two prices and three instants it needs and returns a value.
/// Nothing here reads a clock or a repository, so the awkward cases — a change on the last day, a
/// backdated request, a currency with three decimals — are all reachable in a unit test.</para>
/// </summary>
public static class Proration
{
    /// <summary>
    /// Splits both prices across the period by whole days and returns the difference.
    ///
    /// <para>Whole days, not seconds: a gym reading its invoice checks the arithmetic against a
    /// calendar, and "23 of 30 days" is a sentence it can verify. Days used are floored, so a day in
    /// progress counts as unused and the customer is not charged for it twice — and because the same
    /// count drives both sides, floor versus ceiling cannot bias the net either way.</para>
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when no days remain — the period has run out, so there is nothing to
    /// prorate and the caller should schedule the change for the boundary instead.
    /// </returns>
    /// <exception cref="DomainException">The two prices are in different currencies.</exception>
    public static ProrationResult? Compute(
        Money oldPrice,
        Money newPrice,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        DateTimeOffset now)
    {
        // One gym, one currency. Checked here rather than left to Money's mismatch so the message
        // names the situation: prices that disagree mean the plan catalogue and the subscription were
        // agreed in different currencies, which is a data problem, not an arithmetic one.
        if (oldPrice.Currency != newPrice.Currency)
        {
            throw new DomainException(
                $"A plan change cannot be prorated across currencies ({oldPrice.Currency.Code} to " +
                $"{newPrice.Currency.Code}). A gym is billed in one currency.");
        }

        int totalDays = (int)Math.Ceiling((periodEnd - periodStart).TotalDays);

        if (totalDays <= 0)
        {
            return null;
        }

        // Clamped, so a clock skew or a backdated request cannot produce a negative ratio — which
        // Allocate refuses, turning a plan change into a 500 for a reason nobody could read.
        int daysUsed = Math.Clamp((int)Math.Floor((now - periodStart).TotalDays), 0, totalDays);
        int daysRemaining = totalDays - daysUsed;

        if (daysRemaining == 0)
        {
            return null;
        }

        // Allocate rather than multiply-by-a-fraction: it distributes the remainder instead of
        // rounding twice, so the parts of each price still sum to that price exactly (AD-42).
        Money credit = oldPrice.Allocate(daysUsed, daysRemaining)[1];
        Money charge = newPrice.Allocate(daysUsed, daysRemaining)[1];

        return new ProrationResult(credit, charge, charge.Subtract(credit), daysUsed, daysRemaining);
    }
}
