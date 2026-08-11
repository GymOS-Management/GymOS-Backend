using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel.Currencies;

namespace GymOS.Platform.Domain.Analytics;

/// <summary>One live subscription's input to the MRR sum — its status, its cycle, and what it pays.</summary>
public sealed record MrrContribution(SubscriptionStatus Status, BillingCycle Cycle, Money Price);

/// <summary>
/// Monthly recurring revenue: the sum, <b>per currency</b>, of every billing subscription's price
/// normalised to one month.
///
/// <para>A pure, total function, for the reason <c>FeatureResolution</c> is one: this is the
/// platform's headline number, it is read by both the live dashboard and the nightly stats job, and
/// two implementations of one definition eventually disagree. Keeping it as arithmetic over values
/// means it is exhaustively testable without a database and there is only one of it.</para>
///
/// <para><b>Currencies are never summed.</b> Adding EUR to GBP needs an exchange rate, and which
/// rate — spot at capture, or month-average — is an accounting decision nobody has taken (AD-43).
/// So the result is one <see cref="Money"/> per currency, and the day AD-43 lands this collapses to
/// one number without the definition changing.</para>
/// </summary>
public static class MonthlyRecurringRevenue
{
    public static IReadOnlyList<Money> Calculate(IEnumerable<MrrContribution> contributions)
    {
        ArgumentNullException.ThrowIfNull(contributions);

        return contributions
            .Where(contribution => SubscriptionStatusTransitions.IsBilling(contribution.Status))
            .GroupBy(contribution => contribution.Price.Currency.Code, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Aggregate(
                Money.Zero(group.First().Price.Currency),
                (running, contribution) => running.Add(PerMonth(contribution))))
            .ToList();
    }

    /// <summary>
    /// The price of one month of this subscription. <see cref="BillingCycle"/>'s values are month
    /// counts, so the divisor is the enum value itself (M3.1).
    ///
    /// <para>Rounded half-even to the currency's minor units, and stated here rather than left
    /// implicit: twelve monthly figures need not re-sum to the annual price. MRR is an indicator,
    /// not an invoice — an invoice would use <see cref="Money.Allocate"/>, which conserves every
    /// cent, and there is nothing here to conserve it into.</para>
    /// </summary>
    private static Money PerMonth(MrrContribution contribution)
    {
        var months = contribution.Cycle.Months();

        return months == 1
            ? contribution.Price
            : contribution.Price.Multiply(1m / months, RoundingPolicy.HalfEven);
    }
}
