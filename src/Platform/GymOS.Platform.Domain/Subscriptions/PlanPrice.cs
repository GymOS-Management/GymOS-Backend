using GymOS.SharedKernel.Currencies;
using GymOS.SharedKernel.Entities;

namespace GymOS.Platform.Domain.Subscriptions;

/// <summary>
/// What one billing cycle of a plan costs, in one currency (ARCHITECTURE.md §2.3).
///
/// <para><b>Prices are rows, not columns.</b> A plan has as many of these as it has (cycle, currency)
/// combinations, so offering annual billing or adding GBP is data entry rather than a migration. The
/// alternative — <c>monthly_price</c>, <c>yearly_price</c>, <c>monthly_price_gbp</c> — grows a column
/// every time commercial policy changes.</para>
///
/// <para>Part of the <see cref="SubscriptionPlan"/> aggregate: prices are only ever created, changed, or
/// removed through the plan, so a plan can enforce "one price per cycle and currency" as an invariant it
/// actually owns.</para>
/// </summary>
public sealed class PlanPrice : Entity
{
    internal PlanPrice(Guid id, Guid planId, BillingCycle billingCycle, Money price)
        : base(id)
    {
        PlanId = planId;
        BillingCycle = billingCycle;
        Price = price;
    }

    private PlanPrice()
    {
    }

    public Guid PlanId { get; private set; }

    public BillingCycle BillingCycle { get; private set; }

    /// <summary>
    /// The amount and its currency, as one value (AD-42). Never a naked decimal: a price without its
    /// currency is a number nobody can interpret, and rule R4 fails the build for the alternative.
    /// </summary>
    public Money Price { get; private set; }

    /// <summary>Convenience for the uniqueness check the plan enforces.</summary>
    public bool Matches(BillingCycle cycle, Currency currency) =>
        BillingCycle == cycle && Price.Currency == currency;

    internal void ChangeTo(Money price) => Price = price;
}
