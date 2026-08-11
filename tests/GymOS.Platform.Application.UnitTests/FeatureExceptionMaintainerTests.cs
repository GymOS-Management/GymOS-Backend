using GymOS.Platform.Application.Features;
using GymOS.Platform.Application.UnitTests.Fakes;
using GymOS.Platform.Domain.Features;
using Xunit;

namespace GymOS.Platform.Application.UnitTests;

/// <summary>
/// The one contract an integration test cannot isolate: that the maintainer only stages the
/// withdrawal and leaves the save to its caller. Every other behaviour of
/// <see cref="GymFeatureExceptions.Redundant"/> is pinned in the Domain suite (Task 1); this class
/// exists only for the seam integration tests share across both halves of a subscription-changing
/// transaction, where both halves commit either way and this defect would be invisible.
/// </summary>
public sealed class FeatureExceptionMaintainerTests
{
    private sealed class Context
    {
        public FakeFeatureRepository Features { get; } = new();

        public FakePlanRepository Plans { get; } = new();

        public FakeSubscriptionRepository Subscriptions { get; } = new();

        public FakeUnitOfWork UnitOfWork { get; } = new();

        public Guid GymId { get; } = Guid.NewGuid();

        public FeatureExceptionMaintainer Maintainer =>
            new(Features, Plans, Subscriptions);

        /// <summary>
        /// A gym with no live subscription and an override that already says exactly what the
        /// platform default says — redundant from the moment it is checked, regardless of what
        /// caused it (a goodwill trial the gym has since started paying for, in the scenario this
        /// whole increment exists to fix).
        /// </summary>
        public void GiveGymARedundantOverride()
        {
            Feature feature = Features.Seed(new FeatureDefinition(
                "crm", "CRM", "", "Members", DefaultEnabled: true, IsPremium: true));

            Features.SeedOverride(GymFeatureOverride.Grant(
                GymId,
                feature.Id,
                feature.Key,
                isEnabled: true,
                reason: "3-month goodwill trial.",
                setByUserId: null,
                now: new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero)));
        }
    }

    [Fact]
    public async Task ItMarksWithdrawalsButLeavesTheSaveToItsCaller()
    {
        // If it saved, the withdrawal would commit even when the subscription change that caused it
        // fails — leaving a gym stripped of an exception for a plan change that never happened.
        // Integration tests cannot see this: there, both halves commit either way.
        var context = new Context();
        context.GiveGymARedundantOverride();

        int withdrawn = await context.Maintainer.WithdrawRedundantAsync(context.GymId, default);

        Assert.Equal(1, withdrawn);
        Assert.Equal(0, context.UnitOfWork.SaveCount);
        Assert.Empty(context.Features.Overrides);
    }
}
