using GymOS.Platform.Domain.Subscriptions;
using GymOS.Platform.Domain.Subscriptions.Events;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

/// <summary>
/// What a plan says about a feature — the second resolution layer, and the distinction that makes it
/// three layers rather than two: a plan can include a feature, exclude it, or say nothing at all.
/// </summary>
public sealed class PlanFeatureTests
{
    private static readonly Guid Crm = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static SubscriptionPlan Plan() =>
        SubscriptionPlan.Create("PRO", "Pro", "", 0, PlanLimits.Unlimited);

    [Fact]
    public void ANewPlanSaysNothingAboutAnyFeature() => Assert.Empty(Plan().Features);

    [Fact]
    public void SetFeature_AddsThePlansOpinionAndAnnouncesIt()
    {
        SubscriptionPlan plan = Plan();
        plan.DequeueDomainEvents();

        plan.SetFeature(Crm, isEnabled: true);

        PlanFeature feature = Assert.Single(plan.Features);
        Assert.Equal(Crm, feature.FeatureId);
        Assert.True(feature.IsEnabled);

        var set = Assert.IsType<PlanFeatureSet>(Assert.Single(plan.DomainEvents));
        Assert.Equal(Crm, set.FeatureId);
        Assert.True(set.IsEnabled);
    }

    [Fact]
    public void SetFeature_Twice_ChangesTheOpinionRatherThanAddingASecondRow()
    {
        SubscriptionPlan plan = Plan();

        plan.SetFeature(Crm, true);
        plan.SetFeature(Crm, false);

        // An upsert, like SetPrice: "the PRO plan includes CRM" is one intent, and two rows for one
        // feature would leave resolution deciding which one the plan meant.
        PlanFeature feature = Assert.Single(plan.Features);
        Assert.False(feature.IsEnabled);
    }

    [Fact]
    public void SetFeature_WithTheValueItAlreadyHas_AnnouncesNothing()
    {
        SubscriptionPlan plan = Plan();
        plan.SetFeature(Crm, isEnabled: true);
        plan.DequeueDomainEvents();

        plan.SetFeature(Crm, isEnabled: true);

        // Feature.SetDefault already works this way, and the asymmetry was the problem: a retried
        // PUT announced a plan change that did not happen, and every subscriber would have had to
        // learn to ignore it. Nothing changed, so nothing is announced.
        Assert.Empty(plan.DomainEvents);
        Assert.True(Assert.Single(plan.Features).IsEnabled);
    }

    [Fact]
    public void SetFeature_ReportsWhetherItChangedAnything()
    {
        SubscriptionPlan plan = Plan();

        // The caller needs this to decide whether to move the aggregate's version: a write that
        // changed nothing must not invalidate every other reader's ETag.
        Assert.True(plan.SetFeature(Crm, isEnabled: true));
        Assert.False(plan.SetFeature(Crm, isEnabled: true));
        Assert.True(plan.SetFeature(Crm, isEnabled: false));
    }

    [Fact]
    public void ClearFeature_OfAFeatureItNeverHeld_AnnouncesNothing()
    {
        SubscriptionPlan plan = Plan();
        plan.DequeueDomainEvents();

        Assert.False(plan.ClearFeature(Crm));
        Assert.Empty(plan.DomainEvents);
    }

    [Fact]
    public void ClearFeature_RemovesTheOpinion_WhichIsNotTheSameAsDisablingIt()
    {
        SubscriptionPlan plan = Plan();
        plan.SetFeature(Crm, true);
        plan.DequeueDomainEvents();

        plan.ClearFeature(Crm);

        // Silence, not "no": a cleared feature falls through to the platform default, so the two
        // answers differ the moment that default changes. This is the whole reason the plan layer is
        // nullable rather than a boolean.
        Assert.Empty(plan.Features);
        Assert.IsType<PlanFeatureCleared>(Assert.Single(plan.DomainEvents));
    }

    [Fact]
    public void ClearFeature_WhenThePlanNeverHadAnOpinion_IsASilentNoOp()
    {
        SubscriptionPlan plan = Plan();
        plan.DequeueDomainEvents();

        plan.ClearFeature(Crm);

        // The caller's intent — "this plan should not have an opinion" — is already true.
        Assert.Empty(plan.Features);
        Assert.Empty(plan.DomainEvents);
    }

    [Fact]
    public void FeaturesAndPricesAreIndependentPartsOfTheSamePlan()
    {
        SubscriptionPlan plan = Plan();

        plan.SetFeature(Crm, true);

        // Guards against a future refactor conflating the two child collections: they are both
        // reached through the plan, and neither may disturb the other.
        Assert.Single(plan.Features);
        Assert.Empty(plan.Prices);
    }
}
