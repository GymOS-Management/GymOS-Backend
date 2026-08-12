using GymOS.Platform.Domain.Features;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

public sealed class GymFeatureExceptionsTests
{
    private static readonly Guid Crm = Guid.NewGuid();
    private static readonly Guid Pos = Guid.NewGuid();

    [Fact]
    public void AnOverrideThePlanNowGrantsIsRedundant()
    {
        // The Critical finding, as one assertion: the gym was granted CRM as an exception on a plan
        // that excluded it, then upgraded to a plan that includes it.
        IReadOnlyList<Guid> redundant = GymFeatureExceptions.Redundant(
            [new GymFeatureExceptions.StoredException(Crm, IsEnabled: true)],
            planOpinions: new Dictionary<Guid, bool> { [Crm] = true },
            platformDefaults: new Dictionary<Guid, bool> { [Crm] = false });

        Assert.Equal(Crm, Assert.Single(redundant));
    }

    [Fact]
    public void AnOverrideThatStillDiffersSurvives()
    {
        Assert.Empty(GymFeatureExceptions.Redundant(
            [new GymFeatureExceptions.StoredException(Crm, IsEnabled: true)],
            planOpinions: new Dictionary<Guid, bool> { [Crm] = false },
            platformDefaults: new Dictionary<Guid, bool> { [Crm] = false }));
    }

    [Fact]
    public void WithNoPlanOpinionTheDefaultDecidesRedundancy()
    {
        // A subscription ending makes the plan layer silent for every feature, which is one of the
        // four triggers. POS ships on by default, so an override granting it is now redundant.
        IReadOnlyList<Guid> redundant = GymFeatureExceptions.Redundant(
            [new GymFeatureExceptions.StoredException(Pos, IsEnabled: true)],
            planOpinions: new Dictionary<Guid, bool>(),
            platformDefaults: new Dictionary<Guid, bool> { [Pos] = true });

        Assert.Equal(Pos, Assert.Single(redundant));
    }

    [Fact]
    public void AnOverrideOnAFeatureWithNoKnownDefaultIsLeftAlone()
    {
        // An orphaned key (declared once, since removed from code) still has rows. Withdrawing an
        // exception whose baseline we cannot compute would be deleting data on a guess.
        Assert.Empty(GymFeatureExceptions.Redundant(
            [new GymFeatureExceptions.StoredException(Crm, IsEnabled: true)],
            planOpinions: new Dictionary<Guid, bool>(),
            platformDefaults: new Dictionary<Guid, bool>()));
    }

    [Fact]
    public void NoExceptionsIsNoWork()
    {
        Assert.Empty(GymFeatureExceptions.Redundant(
            [], new Dictionary<Guid, bool>(), new Dictionary<Guid, bool> { [Crm] = false }));
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    public void ItAgreesWithRequiresOverrideOnEveryInput(bool stored, bool? plan, bool @default)
    {
        // The rule must never fork. If this ever fails, one of the two has been edited alone.
        var defaults = new Dictionary<Guid, bool> { [Crm] = @default };
        var opinions = plan is { } value
            ? new Dictionary<Guid, bool> { [Crm] = value }
            : new Dictionary<Guid, bool>();

        bool treatedAsRedundant = GymFeatureExceptions
            .Redundant([new GymFeatureExceptions.StoredException(Crm, stored)], opinions, defaults)
            .Contains(Crm);

        Assert.Equal(!FeatureResolution.RequiresOverride(stored, plan, @default), treatedAsRedundant);
    }
}
