using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

/// <summary>
/// The subscription lifecycle as a matrix, exhaustively — every ordered pair is either allowed or
/// refused, so a state added later cannot be quietly reachable.
/// </summary>
public sealed class SubscriptionStatusTransitionTests
{
    public static TheoryData<SubscriptionStatus, SubscriptionStatus> AllowedTransitions() => new()
    {
        { SubscriptionStatus.Trialing, SubscriptionStatus.Active },
        { SubscriptionStatus.Trialing, SubscriptionStatus.PastDue },
        { SubscriptionStatus.Trialing, SubscriptionStatus.Canceled },
        { SubscriptionStatus.Trialing, SubscriptionStatus.Expired },
        { SubscriptionStatus.Active, SubscriptionStatus.PastDue },
        { SubscriptionStatus.Active, SubscriptionStatus.Canceled },
        { SubscriptionStatus.Active, SubscriptionStatus.Expired },
        { SubscriptionStatus.PastDue, SubscriptionStatus.Active },
        { SubscriptionStatus.PastDue, SubscriptionStatus.Canceled },
        { SubscriptionStatus.PastDue, SubscriptionStatus.Expired },
    };

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public void AllowedTransitionsAreAccepted(SubscriptionStatus from, SubscriptionStatus to)
    {
        Assert.True(SubscriptionStatusTransitions.IsAllowed(from, to));
        SubscriptionStatusTransitions.EnsureAllowed(from, to); // must not throw
    }

    [Fact]
    public void EveryPairOutsideTheAllowListIsRefused()
    {
        var allowed = AllowedTransitions()
            .Select(row => ((SubscriptionStatus)row[0], (SubscriptionStatus)row[1]))
            .ToHashSet();

        var statuses = Enum.GetValues<SubscriptionStatus>();

        foreach (SubscriptionStatus from in statuses)
        {
            foreach (SubscriptionStatus to in statuses)
            {
                if (allowed.Contains((from, to)))
                {
                    continue;
                }

                Assert.False(SubscriptionStatusTransitions.IsAllowed(from, to));
                Assert.Throws<DomainException>(
                    () => SubscriptionStatusTransitions.EnsureAllowed(from, to));
            }
        }
    }

    [Theory]
    [InlineData(SubscriptionStatus.Canceled)]
    [InlineData(SubscriptionStatus.Expired)]
    public void EndedSubscriptionsAreTerminal(SubscriptionStatus terminal)
    {
        // A gym that comes back gets a new subscription, so the old row stays as the record of what it
        // actually had — the same reasoning as a deleted gym.
        foreach (SubscriptionStatus target in Enum.GetValues<SubscriptionStatus>())
        {
            Assert.False(SubscriptionStatusTransitions.IsAllowed(terminal, target));
        }
    }

    [Fact]
    public void CanceledAndExpiredAreBothTerminalButDistinct()
    {
        // Cancellation is a decision, which is what churn counts. Expiry is time running out, usually a
        // failure to collect. Collapsing them would make the M5 churn figure wrong in a way nobody
        // could untangle later.
        Assert.NotEqual(SubscriptionStatus.Canceled, SubscriptionStatus.Expired);
        Assert.False(SubscriptionStatusTransitions.IsLive(SubscriptionStatus.Canceled));
        Assert.False(SubscriptionStatusTransitions.IsLive(SubscriptionStatus.Expired));
    }

    [Theory]
    [InlineData(SubscriptionStatus.Trialing)]
    [InlineData(SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.PastDue)]
    public void LiveStatusesAreTheOnesTheOnePerGymRuleCovers(SubscriptionStatus status)
    {
        Assert.True(SubscriptionStatusTransitions.IsLive(status));
        Assert.Contains(status, SubscriptionStatusTransitions.Live);
    }

    [Theory]
    [InlineData(SubscriptionStatus.Trialing)]
    [InlineData(SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.PastDue)]
    [InlineData(SubscriptionStatus.Canceled)]
    [InlineData(SubscriptionStatus.Expired)]
    public void ATransitionToTheCurrentStateIsRefused(SubscriptionStatus status) =>
        Assert.Throws<DomainException>(
            () => SubscriptionStatusTransitions.EnsureAllowed(status, status));

    [Fact]
    public void StatusNumbersAreTheOnesTheDatabaseStores()
    {
        // The partial unique index filters on `status IN (1, 2, 3)`, and stored rows carry these values.
        // Renumbering would reinterpret every existing subscription and silently break that index.
        Assert.Equal(1, (int)SubscriptionStatus.Trialing);
        Assert.Equal(2, (int)SubscriptionStatus.Active);
        Assert.Equal(3, (int)SubscriptionStatus.PastDue);
        Assert.Equal(4, (int)SubscriptionStatus.Canceled);
        Assert.Equal(5, (int)SubscriptionStatus.Expired);
    }
}
