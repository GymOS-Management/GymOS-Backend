using GymOS.Platform.Domain.Subscriptions;
using GymOS.Platform.Domain.Subscriptions.Events;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Currencies;
using GymOS.SharedKernel.Rules;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

public sealed class SubscriptionPlanTests
{
    private static readonly Currency Eur = Currency.Euro;
    private static readonly Currency Gbp = Currency.Of("GBP");

    private static SubscriptionPlan CreatePlan(string code = "PRO") => SubscriptionPlan.Create(
        code,
        "Pro",
        "For established gyms",
        trialDays: 14,
        new PlanLimits(MaxMembers: 500, MaxTrainers: 10, MaxStaff: 20, StorageLimitMb: 10_240));

    // ── Creation ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_StartsActiveAndPublicWithNoPrices()
    {
        SubscriptionPlan plan = CreatePlan();

        Assert.True(plan.IsActive);
        Assert.True(plan.IsPublic);
        Assert.Empty(plan.Prices);

        var created = Assert.IsType<PlanCreated>(Assert.Single(plan.DomainEvents));
        Assert.Equal("PRO", created.Code);
    }

    [Theory]
    [InlineData("pro", "PRO")]
    [InlineData("  pro_annual  ", "PRO_ANNUAL")]
    public void Create_UpperCasesTheCode(string input, string expected) =>
        // An operator typing lower case is not making a mistake; the code is upper-case by convention.
        Assert.Equal(expected, CreatePlan(input).Code);

    [Theory]
    [InlineData("P")]                 // too short
    [InlineData("PRO PLAN")]          // space
    [InlineData("PRO-PLAN")]          // hyphen is not an identifier character here
    [InlineData("PRO__PLAN")]         // doubled underscore
    [InlineData("_PRO")]              // leading underscore
    [InlineData("PRO_")]              // trailing underscore
    public void Create_RejectsACodeThatIsNotAnIdentifier(string code)
    {
        BusinessRuleViolationException violation = Assert.ThrowsAny<BusinessRuleViolationException>(
            () => CreatePlan(code));

        Assert.IsAssignableFrom<DomainException>(violation);
        Assert.Equal("plans.code_invalid", violation.Code);
    }

    [Fact]
    public void Create_RejectsAnAbsentName()
    {
        BusinessRuleViolationException violation = Assert.ThrowsAny<BusinessRuleViolationException>(
            () => SubscriptionPlan.Create("PRO", "  ", "", 0, PlanLimits.Unlimited));

        Assert.Equal("plans.name_invalid", violation.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(366)]
    public void Create_RejectsAnImplausibleTrial(int trialDays)
    {
        BusinessRuleViolationException violation = Assert.ThrowsAny<BusinessRuleViolationException>(
            () => SubscriptionPlan.Create("PRO", "Pro", "", trialDays, PlanLimits.Unlimited));

        Assert.Equal("plans.trial_days_invalid", violation.Code);
    }

    [Fact]
    public void Create_TreatsNullLimitsAsUnlimitedAndRefusesZero()
    {
        SubscriptionPlan unlimited = SubscriptionPlan.Create(
            "ENTERPRISE", "Enterprise", "", 0, PlanLimits.Unlimited);

        Assert.Null(unlimited.MaxMembers);
        Assert.Null(unlimited.StorageLimitMb);

        // Zero would mean "no members allowed", which is not a plan anyone can sell — and it is the
        // sentinel someone reaches for when they mean unlimited.
        BusinessRuleViolationException violation = Assert.ThrowsAny<BusinessRuleViolationException>(
            () => SubscriptionPlan.Create("FREE", "Free", "", 0, new PlanLimits(0, null, null, null)));

        Assert.Equal("plans.limit_invalid", violation.Code);
    }

    // ── Prices ───────────────────────────────────────────────────────────────────

    [Fact]
    public void SetPrice_AddsARowAndRaisesTheChange()
    {
        SubscriptionPlan plan = CreatePlan();
        plan.DequeueDomainEvents();

        PlanPrice price = plan.SetPrice(BillingCycle.Monthly, Money.Of(49.99m, Eur));

        Assert.Equal(49.99m, price.Price.Amount);
        Assert.Equal(Eur, price.Price.Currency);
        Assert.Equal(plan.Id, price.PlanId);

        var raised = Assert.IsType<PlanPriceSet>(Assert.Single(plan.DomainEvents));
        Assert.Equal("Monthly", raised.BillingCycle);
        Assert.Equal("EUR", raised.Currency);
        Assert.Equal(49.99m, raised.Amount);
    }

    [Fact]
    public void SetPrice_ChangesTheExistingRowRatherThanAddingASecond()
    {
        SubscriptionPlan plan = CreatePlan();
        PlanPrice first = plan.SetPrice(BillingCycle.Monthly, Money.Of(49.99m, Eur));

        PlanPrice again = plan.SetPrice(BillingCycle.Monthly, Money.Of(59.99m, Eur));

        // One intent, one row: making the caller discover whether a row exists invites the race where
        // two operators both add one.
        Assert.Same(first, again);
        Assert.Equal(59.99m, Assert.Single(plan.Prices).Price.Amount);
    }

    [Fact]
    public void SetPrice_TreatsADifferentCurrencyAsADifferentPrice()
    {
        SubscriptionPlan plan = CreatePlan();

        plan.SetPrice(BillingCycle.Monthly, Money.Of(49.99m, Eur));
        plan.SetPrice(BillingCycle.Monthly, Money.Of(44.99m, Gbp));

        // Same cycle, two currencies — the whole reason prices are rows rather than columns.
        Assert.Equal(2, plan.Prices.Count);
    }

    [Fact]
    public void SetPrice_TreatsADifferentCycleAsADifferentPrice()
    {
        SubscriptionPlan plan = CreatePlan();

        plan.SetPrice(BillingCycle.Monthly, Money.Of(49.99m, Eur));
        plan.SetPrice(BillingCycle.Yearly, Money.Of(499.99m, Eur));

        Assert.Equal(2, plan.Prices.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SetPrice_RefusesAnAmountNobodyCouldBeCharged(decimal amount)
    {
        SubscriptionPlan plan = CreatePlan();

        // Zero is refused deliberately: a €0.00 line on an invoice reads as a billing failure to
        // whoever receives it, so a free tier is expressed by having no price row.
        BusinessRuleViolationException violation = Assert.ThrowsAny<BusinessRuleViolationException>(
            () => plan.SetPrice(BillingCycle.Monthly, Money.Of(amount, Eur)));

        Assert.Equal("plans.price_invalid", violation.Code);
        Assert.Empty(plan.Prices);
    }

    [Fact]
    public void RemovePrice_TakesTheRowOutAndRaisesTheRemoval()
    {
        SubscriptionPlan plan = CreatePlan();
        PlanPrice price = plan.SetPrice(BillingCycle.Monthly, Money.Of(49.99m, Eur));
        plan.DequeueDomainEvents();

        plan.RemovePrice(price.Id);

        Assert.Empty(plan.Prices);
        var removed = Assert.IsType<PlanPriceRemoved>(Assert.Single(plan.DomainEvents));
        Assert.Equal(price.Id, removed.PlanPriceId);
    }

    [Fact]
    public void RemovePrice_RefusesAPriceFromAnotherPlan()
    {
        SubscriptionPlan plan = CreatePlan();

        Assert.ThrowsAny<DomainException>(() => plan.RemovePrice(Guid.NewGuid()));
    }

    // ── Withdrawal ───────────────────────────────────────────────────────────────

    [Fact]
    public void Deactivate_WithdrawsThePlanWithoutTouchingItsPrices()
    {
        SubscriptionPlan plan = CreatePlan();
        plan.SetPrice(BillingCycle.Monthly, Money.Of(49.99m, Eur));
        plan.DequeueDomainEvents();

        plan.Deactivate();

        Assert.False(plan.IsActive);

        // Prices survive: gyms already on the plan keep billing at them (§2.3).
        Assert.Single(plan.Prices);
        Assert.IsType<PlanDeactivated>(Assert.Single(plan.DomainEvents));
    }

    [Fact]
    public void Deactivate_IsRefusedTwice() =>
        Assert.ThrowsAny<DomainException>(() =>
        {
            SubscriptionPlan plan = CreatePlan();
            plan.Deactivate();
            plan.Deactivate();
        });

    [Fact]
    public void Activate_ReturnsAWithdrawnPlanToTheCatalogue()
    {
        SubscriptionPlan plan = CreatePlan();
        plan.Deactivate();
        plan.DequeueDomainEvents();

        plan.Activate();

        Assert.True(plan.IsActive);
        Assert.IsType<PlanActivated>(Assert.Single(plan.DomainEvents));
    }

    [Fact]
    public void Activate_IsRefusedWhenAlreadyActive() =>
        Assert.ThrowsAny<DomainException>(() => CreatePlan().Activate());

    // ── Details ──────────────────────────────────────────────────────────────────

    [Fact]
    public void UpdateDetails_ChangesEverythingExceptTheCode()
    {
        SubscriptionPlan plan = CreatePlan();
        string codeBefore = plan.Code;
        plan.DequeueDomainEvents();

        plan.UpdateDetails(
            "Pro Plus",
            "Now with more",
            trialDays: 30,
            PlanLimits.Unlimited,
            sortOrder: 5,
            isPublic: false);

        Assert.Equal(codeBefore, plan.Code);
        Assert.Equal("Pro Plus", plan.Name);
        Assert.Equal(30, plan.TrialDays);
        Assert.Null(plan.MaxMembers);
        Assert.Equal(5, plan.SortOrder);
        Assert.False(plan.IsPublic);
        Assert.IsType<PlanUpdated>(Assert.Single(plan.DomainEvents));
    }

    [Fact]
    public void UpdateDetails_DoesNotChangeWhetherThePlanIsActive()
    {
        SubscriptionPlan plan = CreatePlan();
        plan.Deactivate();

        plan.UpdateDetails("Pro", "", 0, PlanLimits.Unlimited, 0, isPublic: true);

        // Editing a withdrawn plan's wording must not put it back on sale — that is `activate`, a
        // separate decision.
        Assert.False(plan.IsActive);
    }

    // ── Billing cycles ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(BillingCycle.Monthly, 1)]
    [InlineData(BillingCycle.Quarterly, 3)]
    [InlineData(BillingCycle.Yearly, 12)]
    public void ABillingCyclesValueIsItsLengthInMonths(BillingCycle cycle, int months)
    {
        // The values are month counts on purpose, so advancing a period is arithmetic on the enum
        // rather than a switch someone will extend inconsistently. They are also stored values:
        // renumbering them would reprice every existing subscription.
        Assert.Equal(months, (int)cycle);
        Assert.Equal(months, cycle.Months());
    }
}
