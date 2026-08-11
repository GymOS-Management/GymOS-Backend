using GymOS.Platform.Domain.Features;
using GymOS.Platform.Domain.Features.Events;
using GymOS.Platform.Domain.Features.Rules;
using GymOS.SharedKernel.Rules;
using GymOS.TestingSupport;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

/// <summary>
/// The catalogue aggregate and the declaration behind it: what an operator may change about a
/// feature, and what the platform will not let them change at all.
/// </summary>
public sealed class FeatureCatalogueTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid GymId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AuthorId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static Feature Pos() => Feature.FromDefinition(
        new FeatureDefinition("pos", "Point of sale", "Sell at the desk.", "Operations", true, false));

    // ── The declared catalogue ───────────────────────────────────────────────────

    [Fact]
    public void TheCatalogueDeclaresExactlyTheTwelveSpecifiedKeys()
    {
        // §2.4 names these twelve. Pinned as a set, because a key is a compile-time constant that
        // code branches on: adding one is a reviewed decision, and losing one silently would strand
        // whatever reads it.
        string[] expected =
        [
            "access_control", "ai_assistant", "analytics", "crm", "gateway", "inventory",
            "marketing", "member_app", "nutrition", "pos", "reports", "workout_builder",
        ];

        Assert.Equal(expected, PlatformFeatures.All.Select(feature => feature.Key).Order().ToArray());
    }

    [Fact]
    public void EveryDeclaredFeatureIsWellFormedAndNamed() =>
        Assert.All(PlatformFeatures.All, definition =>
        {
            BusinessRuleAssert.Satisfied(new FeatureKeyMustBeAnIdentifier(definition.Key));
            BusinessRuleAssert.Satisfied(new FeatureNameIsRequired(definition.Name));
            Assert.False(string.IsNullOrWhiteSpace(definition.Description));
            Assert.False(string.IsNullOrWhiteSpace(definition.Category));
        });

    [Fact]
    public void KeysAreUnique() =>
        Assert.Equal(
            PlatformFeatures.All.Count,
            PlatformFeatures.All.Select(feature => feature.Key).Distinct(StringComparer.Ordinal).Count());

    [Theory]
    [InlineData("Pos")]          // upper case
    [InlineData("workout-builder")] // hyphen
    [InlineData("2fast")]        // leading digit
    [InlineData("")]
    [InlineData("   ")]
    public void AMalformedKeyIsRefused(string key) =>
        BusinessRuleAssert.Broken(new FeatureKeyMustBeAnIdentifier(key), "features.key_invalid");

    // ── What an operator may change ──────────────────────────────────────────────

    [Fact]
    public void Describe_ChangesTheCopyButNeverTheKey()
    {
        Feature feature = Pos();

        feature.Describe("Till", "Take payments at the counter.", "Front desk");

        Assert.Equal("pos", feature.Key); // the handle code branches on, unchanged
        Assert.Equal("Till", feature.Name);
        Assert.Equal("Take payments at the counter.", feature.Description);
        Assert.Equal("Front desk", feature.Category);
    }

    [Fact]
    public void Describe_RefusesAnEmptyName() =>
        Assert.Throws<BusinessRuleViolationException>(() => Pos().Describe(" ", null, null));

    [Fact]
    public void SetDefault_AnnouncesTheChangeBecauseItReAnswersEveryUndecidedGym()
    {
        Feature feature = Pos();
        Assert.True(feature.DefaultEnabled);

        feature.SetDefault(false);

        Assert.False(feature.DefaultEnabled);
        var changed = Assert.IsType<FeatureDefaultChanged>(Assert.Single(feature.DomainEvents));
        Assert.Equal("pos", changed.Key);
        Assert.False(changed.DefaultEnabled);
    }

    [Fact]
    public void SetDefault_ToTheSameValueIsSilent()
    {
        // A no-op write should not invent an event for consumers to react to.
        Feature feature = Pos();

        feature.SetDefault(true);

        Assert.Empty(feature.DomainEvents);
    }

    // ── Overrides ────────────────────────────────────────────────────────────────

    [Fact]
    public void Grant_RecordsTheValueTheReasonAndTheAuthor()
    {
        Feature feature = Pos();

        GymFeatureOverride over = GymFeatureOverride.Grant(
            GymId, feature.Id, feature.Key, isEnabled: false,
            reason: "Gym asked for the till to be hidden during refit.", AuthorId, Now);

        Assert.False(over.IsEnabled);
        Assert.Equal("Gym asked for the till to be hidden during refit.", over.Reason);
        Assert.Equal(AuthorId, over.SetByUserId);
        Assert.Equal(Now, over.SetAtUtc);

        var granted = Assert.IsType<GymFeatureOverridden>(Assert.Single(over.DomainEvents));
        Assert.Equal(GymId, granted.GymId);
        Assert.Equal("pos", granted.Key);
        Assert.Equal(AuthorId, granted.SetByUserId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Grant_WithoutAReason_IsRefused(string? reason)
    {
        // An override is an exception to what the gym pays for; without a reason it is
        // indistinguishable from a mistake by the time anyone asks.
        Feature feature = Pos();

        Assert.Throws<BusinessRuleViolationException>(() => GymFeatureOverride.Grant(
            GymId, feature.Id, feature.Key, true, reason!, AuthorId, Now));
    }

    [Fact]
    public void Grant_WithAnOverLongReason_IsRefused() =>
        BusinessRuleAssert.Broken(
            new OverrideReasonIsRequired(new string('x', OverrideReasonIsRequired.MaximumLength + 1)),
            "features.override_reason_required");

    [Fact]
    public void Restate_MovesTheValueReasonAuthorAndTimestamp()
    {
        Feature feature = Pos();
        GymFeatureOverride over = GymFeatureOverride.Grant(
            GymId, feature.Id, feature.Key, false, "Refit.", AuthorId, Now);
        over.DequeueDomainEvents();

        Guid secondAuthor = Guid.NewGuid();
        DateTimeOffset later = Now.AddDays(3);
        over.Restate(feature.Key, isEnabled: true, "Refit finished.", secondAuthor, later);

        // The row answers "who decided this, and why" — the audit trail keeps who decided it before.
        Assert.True(over.IsEnabled);
        Assert.Equal("Refit finished.", over.Reason);
        Assert.Equal(secondAuthor, over.SetByUserId);
        Assert.Equal(later, over.SetAtUtc);
        Assert.IsType<GymFeatureOverridden>(Assert.Single(over.DomainEvents));
    }

    [Fact]
    public void MarkCleared_AnnouncesTheWithdrawal()
    {
        Feature feature = Pos();
        GymFeatureOverride over = GymFeatureOverride.Grant(
            GymId, feature.Id, feature.Key, true, "Trial.", AuthorId, Now);
        over.DequeueDomainEvents();

        over.MarkCleared(feature.Key);

        var cleared = Assert.IsType<GymFeatureOverrideCleared>(Assert.Single(over.DomainEvents));
        Assert.Equal(GymId, cleared.GymId);
        Assert.Equal("pos", cleared.Key);
    }
}
