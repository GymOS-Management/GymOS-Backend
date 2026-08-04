using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

/// <summary>
/// The account lifecycle as a matrix, exhaustively. Every ordered pair of states is either in
/// the allow-list or refused, and this asserts the whole grid rather than the handful of paths
/// the use cases happen to exercise — so a state added later cannot be quietly reachable.
/// </summary>
public sealed class UserStatusTransitionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<UserStatus, UserStatus> AllowedTransitions() => new()
    {
        { UserStatus.PendingActivation, UserStatus.Active },
        { UserStatus.PendingActivation, UserStatus.Suspended },
        { UserStatus.Active, UserStatus.Suspended },
        { UserStatus.Suspended, UserStatus.Active },
        { UserStatus.Suspended, UserStatus.PendingActivation },
    };

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public void AllowedTransitionsAreAccepted(UserStatus from, UserStatus to)
    {
        Assert.True(UserStatusTransitions.IsAllowed(from, to));
        UserStatusTransitions.EnsureAllowed(from, to); // must not throw
    }

    [Fact]
    public void EveryPairOutsideTheAllowListIsRefused()
    {
        var allowed = AllowedTransitions()
            .Select(row => ((UserStatus)row[0], (UserStatus)row[1]))
            .ToHashSet();

        var statuses = Enum.GetValues<UserStatus>();

        foreach (UserStatus from in statuses)
        {
            foreach (UserStatus to in statuses)
            {
                if (allowed.Contains((from, to)))
                {
                    continue;
                }

                Assert.False(UserStatusTransitions.IsAllowed(from, to));
                Assert.Throws<DomainException>(() => UserStatusTransitions.EnsureAllowed(from, to));
            }
        }
    }

    [Theory]
    [InlineData(UserStatus.PendingActivation)]
    [InlineData(UserStatus.Active)]
    [InlineData(UserStatus.Suspended)]
    public void ATransitionToTheCurrentStateIsRefused(UserStatus status)
    {
        // Self-transitions are not no-ops here on purpose: "suspend an already-suspended user"
        // is a caller that has lost track of state, and reporting it beats silently succeeding.
        Assert.Throws<DomainException>(() => UserStatusTransitions.EnsureAllowed(status, status));
    }

    [Fact]
    public void ActiveCannotReturnToPendingActivation()
    {
        // There is no un-activating: a password exists, so PendingActivation would misdescribe
        // the account. Suspension is the way to withdraw access.
        Assert.False(UserStatusTransitions.IsAllowed(UserStatus.Active, UserStatus.PendingActivation));
    }

    // ── The aggregate honors the same table ──────────────────────────────────────

    [Fact]
    public void SettingTheFirstPasswordActivatesAnInvitedAccount()
    {
        // This is the activation extension point the email flow will call into
        // (docs/Engineering/ACCOUNT-ACTIVATION.md); it exists and works today.
        var user = User.Create("invited@gymos.test", "Ada", "Admin");
        Assert.Equal(UserStatus.PendingActivation, user.Status);

        user.SetPasswordHash("hash", Now);

        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Equal(Now, user.PasswordChangedAtUtc);
    }

    [Fact]
    public void AnInvitationCanBeSuspendedBeforeItIsEverUsed()
    {
        var user = User.Create("invited@gymos.test", "Ada", "Admin");

        user.Suspend("Hire fell through", Now);

        Assert.Equal(UserStatus.Suspended, user.Status);
    }

    [Fact]
    public void ReactivatingAnUnusedInvitationReturnsItToPendingActivation()
    {
        var user = User.Create("invited@gymos.test", "Ada", "Admin");
        user.Suspend("Hire fell through", Now);

        user.Reactivate(Now);

        // Not Active: the owner never set a password, so there is no login to restore.
        Assert.Equal(UserStatus.PendingActivation, user.Status);
    }

    [Fact]
    public void ReactivatingAnActivatedAccountReturnsItToActive()
    {
        var user = User.Create("admin@gymos.test", "Ada", "Admin");
        user.SetPasswordHash("hash", Now);
        user.Suspend("Leave of absence", Now);

        user.Reactivate(Now);

        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Null(user.SuspensionReason);
    }

    [Fact]
    public void ASuspendedAccountCannotHaveItsPasswordChanged()
    {
        var user = User.Create("admin@gymos.test", "Ada", "Admin");
        user.SetPasswordHash("hash", Now);
        user.Suspend("Compromised", Now);

        // Otherwise setting a password would be a back door out of suspension, since on an
        // invited account that same call is what activates it.
        Assert.Throws<DomainException>(() => user.SetPasswordHash("new-hash", Now));
        Assert.Equal(UserStatus.Suspended, user.Status);
    }

    [Fact]
    public void SuspensionRotatesTheSecurityStampSoSessionsDieImmediately()
    {
        var user = User.Create("admin@gymos.test", "Ada", "Admin");
        user.SetPasswordHash("hash", Now);
        Guid stampBefore = user.SecurityStamp;

        user.Suspend("Compromised", Now);

        Assert.NotEqual(stampBefore, user.SecurityStamp);
    }

    // ── Lockout is a window, not a state ─────────────────────────────────────────

    [Fact]
    public void LockoutDoesNotChangeTheAccountStatus()
    {
        var user = User.Create("admin@gymos.test", "Ada", "Admin");
        user.SetPasswordHash("hash", Now);

        user.RecordFailedLogin(Now, maxAttempts: 1, lockoutDuration: TimeSpan.FromMinutes(15));

        // The account is locked out *and* Active. Conflating the two would need a job to unlock
        // accounts and a memory of what state to return to.
        Assert.True(user.IsLockedOut(Now));
        Assert.Equal(UserStatus.Active, user.Status);
    }

    [Fact]
    public void LockoutExpiresOnItsOwnWithoutAnyTransition()
    {
        var user = User.Create("admin@gymos.test", "Ada", "Admin");
        user.SetPasswordHash("hash", Now);
        user.RecordFailedLogin(Now, maxAttempts: 1, lockoutDuration: TimeSpan.FromMinutes(15));

        Assert.False(user.IsLockedOut(Now.AddMinutes(16)));
        Assert.Equal(UserStatus.Active, user.Status);
    }

    [Fact]
    public void AnInvitedAccountCanBeLockedOutToo()
    {
        // The reason a Locked status would need to remember where it came from: lockout can
        // interrupt either lifecycle state.
        var user = User.Create("invited@gymos.test", "Ada", "Admin");

        user.RecordFailedLogin(Now, maxAttempts: 1, lockoutDuration: TimeSpan.FromMinutes(15));

        Assert.True(user.IsLockedOut(Now));
        Assert.Equal(UserStatus.PendingActivation, user.Status);
    }

    [Fact]
    public void ChangingThePasswordClearsAnActiveLockout()
    {
        var user = User.Create("admin@gymos.test", "Ada", "Admin");
        user.SetPasswordHash("hash", Now);
        user.RecordFailedLogin(Now, maxAttempts: 1, lockoutDuration: TimeSpan.FromMinutes(15));

        user.SetPasswordHash("new-hash", Now.AddMinutes(1));

        // Whoever set the password proved ownership, so holding the lock serves nobody.
        Assert.False(user.IsLockedOut(Now.AddMinutes(1)));
        Assert.Equal(0, user.FailedLoginAttempts);
    }
}
