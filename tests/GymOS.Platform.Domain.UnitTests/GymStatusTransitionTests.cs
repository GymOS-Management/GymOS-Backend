using GymOS.Platform.Domain.Gyms;
using GymOS.SharedKernel;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

/// <summary>
/// The gym lifecycle as a matrix, exhaustively — every ordered pair is either in the allow-list or
/// refused, so a state added later cannot be quietly reachable.
/// </summary>
public sealed class GymStatusTransitionTests
{
    public static TheoryData<GymStatus, GymStatus> AllowedTransitions() => new()
    {
        { GymStatus.Provisioning, GymStatus.Active },
        { GymStatus.Provisioning, GymStatus.Suspended },
        { GymStatus.Active, GymStatus.Suspended },
        { GymStatus.Suspended, GymStatus.Active },
        { GymStatus.Provisioning, GymStatus.Deleted },
        { GymStatus.Active, GymStatus.Deleted },
        { GymStatus.Suspended, GymStatus.Deleted },
    };

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public void AllowedTransitionsAreAccepted(GymStatus from, GymStatus to)
    {
        Assert.True(GymStatusTransitions.IsAllowed(from, to));
        GymStatusTransitions.EnsureAllowed(from, to); // must not throw
    }

    [Fact]
    public void EveryPairOutsideTheAllowListIsRefused()
    {
        var allowed = AllowedTransitions()
            .Select(row => ((GymStatus)row[0], (GymStatus)row[1]))
            .ToHashSet();

        var statuses = Enum.GetValues<GymStatus>();

        foreach (GymStatus from in statuses)
        {
            foreach (GymStatus to in statuses)
            {
                if (allowed.Contains((from, to)))
                {
                    continue;
                }

                Assert.False(GymStatusTransitions.IsAllowed(from, to));
                Assert.Throws<DomainException>(() => GymStatusTransitions.EnsureAllowed(from, to));
            }
        }
    }

    [Theory]
    [InlineData(GymStatus.Deleted, GymStatus.Active)]
    [InlineData(GymStatus.Deleted, GymStatus.Provisioning)]
    [InlineData(GymStatus.Deleted, GymStatus.Suspended)]
    public void DeletedIsTerminal(GymStatus from, GymStatus to)
    {
        // The frozen design names Deleted → Active as the transition that must not exist: a status flip
        // would resurrect a tenant whose data may already be partly purged and whose billing was closed.
        Assert.False(GymStatusTransitions.IsAllowed(from, to));
    }

    [Theory]
    [InlineData(GymStatus.Provisioning)]
    [InlineData(GymStatus.Active)]
    [InlineData(GymStatus.Suspended)]
    [InlineData(GymStatus.Deleted)]
    public void ATransitionToTheCurrentStateIsRefused(GymStatus status) =>
        // Self-transitions are not no-ops on purpose: "suspend an already-suspended gym" is a caller
        // that has lost track of state, and reporting it beats silently succeeding.
        Assert.Throws<DomainException>(() => GymStatusTransitions.EnsureAllowed(status, status));

    [Fact]
    public void StatusNumbersAreTheOnesTheApiPublishes()
    {
        // These values travel in stored rows and, as names, in API responses. Renumbering them would
        // reinterpret every existing gym.
        Assert.Equal(1, (int)GymStatus.Provisioning);
        Assert.Equal(2, (int)GymStatus.Active);
        Assert.Equal(3, (int)GymStatus.Suspended);
        Assert.Equal(4, (int)GymStatus.Deleted);
    }
}
