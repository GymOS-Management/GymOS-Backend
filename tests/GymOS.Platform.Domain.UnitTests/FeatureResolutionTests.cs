using GymOS.Platform.Domain.Features;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

/// <summary>
/// The three-layer rule (§2.4) as a complete truth table. Exhaustive on purpose: this is the most-read
/// sentence in the module — every gym detail view and every future enforcement check asks it — and a
/// rule that is only spot-checked is a rule that drifts.
/// </summary>
public sealed class FeatureResolutionTests
{
    private const string Key = "pos";

    /// <summary>Every combination of (override, plan, default) and the answer each must give.</summary>
    public static TheoryData<bool?, bool?, bool, bool, FeatureSource> AllCombinations() => new()
    {
        // An override always wins, whatever the layers beneath say.
        { true, true, true, true, FeatureSource.Override },
        { true, true, false, true, FeatureSource.Override },
        { true, false, true, true, FeatureSource.Override },
        { true, false, false, true, FeatureSource.Override },
        { true, null, true, true, FeatureSource.Override },
        { true, null, false, true, FeatureSource.Override },
        { false, true, true, false, FeatureSource.Override },
        { false, true, false, false, FeatureSource.Override },
        { false, false, true, false, FeatureSource.Override },
        { false, false, false, false, FeatureSource.Override },
        { false, null, true, false, FeatureSource.Override },
        { false, null, false, false, FeatureSource.Override },

        // No override: the plan decides, whatever the default says.
        { null, true, true, true, FeatureSource.Plan },
        { null, true, false, true, FeatureSource.Plan },
        { null, false, true, false, FeatureSource.Plan },
        { null, false, false, false, FeatureSource.Plan },

        // Neither: the platform default. `plan: null` is also the no-live-subscription case.
        { null, null, true, true, FeatureSource.Default },
        { null, null, false, false, FeatureSource.Default },
    };

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void EveryCombinationResolvesToTheDocumentedLayer(
        bool? overrideEnabled,
        bool? planEnabled,
        bool defaultEnabled,
        bool expectedEnabled,
        FeatureSource expectedSource)
    {
        ResolvedFeature resolved = FeatureResolution.Resolve(
            Key, overrideEnabled, planEnabled, defaultEnabled);

        Assert.Equal(Key, resolved.Key);
        Assert.Equal(expectedEnabled, resolved.Enabled);
        Assert.Equal(expectedSource, resolved.Source);
    }

    [Fact]
    public void TheTruthTableCoversEveryPossibleInput()
    {
        // 3 override states × 3 plan states × 2 defaults = 18. Asserted so a future layer or a new
        // tri-state cannot be added while leaving this table looking complete.
        Assert.Equal(18, AllCombinations().Count);
    }

    [Fact]
    public void AGymWithNoSubscriptionGetsThePlatformDefault()
    {
        // The approved decision, spelled out as its own case because the reasoning is a product one:
        // a gym is provisioned before it is sold anything, so it must stay configurable, and a
        // cancelled gym degrades to the baseline rather than going dark.
        Assert.True(FeatureResolution.Resolve(Key, null, planEnabled: null, defaultEnabled: true).Enabled);
        Assert.Equal(
            FeatureSource.Default,
            FeatureResolution.Resolve(Key, null, planEnabled: null, defaultEnabled: true).Source);
    }

    // ── When an override is warranted at all ─────────────────────────────────────

    [Theory]
    // Desired agrees with the plan → no override needed.
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)]
    // Desired differs from the plan → an override is the only way to express it.
    [InlineData(true, false, false, true)]
    [InlineData(false, true, true, true)]
    // No plan opinion: the default is what it is compared against.
    [InlineData(true, null, true, false)]
    [InlineData(true, null, false, true)]
    [InlineData(false, null, false, false)]
    [InlineData(false, null, true, true)]
    public void RequiresOverride_IsTrueOnlyWhenTheDesiredValueDiffersFromTheLayersBeneath(
        bool desired,
        bool? planEnabled,
        bool defaultEnabled,
        bool expected) =>
        Assert.Equal(expected, FeatureResolution.RequiresOverride(desired, planEnabled, defaultEnabled));

    [Fact]
    public void SettingWhatThePlanAlreadyGives_NeedsNoOverride()
    {
        // §2.4: "an override exists only when it differs from the plan". The approved consequence is
        // that such a request CLEARS any existing override rather than storing a redundant one — so
        // a gym that later changes plan follows the new plan instead of staying frozen.
        Assert.False(FeatureResolution.RequiresOverride(
            desiredEnabled: true, planEnabled: true, defaultEnabled: false));
    }
}
