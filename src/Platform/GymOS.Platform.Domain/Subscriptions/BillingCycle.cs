namespace GymOS.Platform.Domain.Subscriptions;

/// <summary>
/// How often a subscription bills (ARCHITECTURE.md §2.3).
///
/// <para><b>The numeric values are month counts, and that is deliberate</b> — Monthly=1,
/// Quarterly=3, Yearly=12. A period's length is therefore the enum's own value, so advancing a
/// billing period reads as <c>start.AddMonths((int)cycle)</c> rather than needing a switch that
/// someone will one day extend inconsistently. They are also stable API and stored values: renumbering
/// them would reprice every existing subscription.</para>
/// </summary>
public enum BillingCycle
{
    Monthly = 1,
    Quarterly = 3,
    Yearly = 12,
}

public static class BillingCycles
{
    /// <summary>The period length in months, which is the enum value itself.</summary>
    public static int Months(this BillingCycle cycle) => (int)cycle;
}
