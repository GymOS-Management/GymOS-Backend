using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Identity.Events;
using GymOS.SharedKernel;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

public sealed class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 16, 0, 0, TimeSpan.Zero);

    private static User CreateUser() => User.Create("admin@gymos.test", "Ada", "Admin");

    [Fact]
    public void Create_StartsPendingActivation_AndRaisesUserCreated()
    {
        User user = CreateUser();

        Assert.Equal(UserStatus.PendingActivation, user.Status);
        Assert.Null(user.PasswordHash);
        Assert.Equal("ADMIN@GYMOS.TEST", user.NormalizedEmail);
        var created = Assert.IsType<UserCreated>(Assert.Single(user.DomainEvents));
        Assert.Equal(user.Id, created.UserId);
    }

    [Fact]
    public void Create_RejectsAnObviouslyInvalidEmail()
    {
        Assert.Throws<DomainException>(() => User.Create("not-an-email", "Ada", "Admin"));
    }

    [Fact]
    public void SetPasswordHash_ActivatesPendingUser_RotatesStamp_AndRaisesEvent()
    {
        User user = CreateUser();
        Guid originalStamp = user.SecurityStamp;
        user.DequeueDomainEvents();

        user.SetPasswordHash("argon2id-hash", Now);

        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Equal(Now, user.PasswordChangedAtUtc);
        Assert.NotEqual(originalStamp, user.SecurityStamp);
        Assert.IsType<UserPasswordChanged>(Assert.Single(user.DomainEvents));
    }

    [Fact]
    public void RecordFailedLogin_LocksOut_AtTheThreshold_AndRaisesEvent()
    {
        User user = CreateUser();
        user.SetPasswordHash("hash", Now);
        user.DequeueDomainEvents();

        user.RecordFailedLogin(Now, maxAttempts: 3, lockoutDuration: TimeSpan.FromMinutes(15));
        user.RecordFailedLogin(Now, maxAttempts: 3, lockoutDuration: TimeSpan.FromMinutes(15));
        Assert.False(user.IsLockedOut(Now));

        user.RecordFailedLogin(Now, maxAttempts: 3, lockoutDuration: TimeSpan.FromMinutes(15));

        Assert.True(user.IsLockedOut(Now));
        Assert.False(user.IsLockedOut(Now.AddMinutes(16))); // lockout expires
        var lockedOut = Assert.IsType<UserLockedOut>(Assert.Single(user.DomainEvents));
        Assert.Equal(Now.AddMinutes(15), lockedOut.LockedOutUntilUtc);
    }

    [Fact]
    public void RecordSuccessfulLogin_ClearsFailuresAndLockout()
    {
        User user = CreateUser();
        user.SetPasswordHash("hash", Now);
        user.RecordFailedLogin(Now, maxAttempts: 2, lockoutDuration: TimeSpan.FromMinutes(15));
        user.RecordFailedLogin(Now, maxAttempts: 2, lockoutDuration: TimeSpan.FromMinutes(15));
        Assert.True(user.IsLockedOut(Now));

        user.RecordSuccessfulLogin(Now.AddMinutes(20));

        Assert.False(user.IsLockedOut(Now.AddMinutes(20)));
        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.Equal(Now.AddMinutes(20), user.LastLoginAtUtc);
    }

    [Fact]
    public void Suspend_RotatesStamp_AndReactivate_RestoresTheRightStatus()
    {
        User user = CreateUser();
        user.SetPasswordHash("hash", Now);
        Guid stampBeforeSuspension = user.SecurityStamp;
        user.DequeueDomainEvents();

        user.Suspend("policy violation", Now);

        Assert.Equal(UserStatus.Suspended, user.Status);
        Assert.Equal("policy violation", user.SuspensionReason);
        Assert.NotEqual(stampBeforeSuspension, user.SecurityStamp);
        Assert.Throws<DomainException>(() => user.Suspend("again", Now));
        Assert.Throws<DomainException>(() => user.SetPasswordHash("new", Now));

        user.Reactivate(Now);

        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Null(user.SuspensionReason);
    }

    [Fact]
    public void Reactivate_WithoutPassword_ReturnsToPendingActivation()
    {
        User user = CreateUser();
        user.Suspend("cleanup", Now);

        user.Reactivate(Now);

        Assert.Equal(UserStatus.PendingActivation, user.Status);
        Assert.Throws<DomainException>(() => user.Reactivate(Now)); // not suspended anymore
    }

    [Fact]
    public void AssignRole_IsIdempotent_AndTenantScopesAreDistinct()
    {
        User user = CreateUser();
        var roleId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        user.DequeueDomainEvents();

        user.AssignRole(roleId);
        user.AssignRole(roleId); // no-op
        user.AssignRole(roleId, tenantId); // different scope — distinct assignment

        Assert.Equal(2, user.Roles.Count);
        Assert.Equal(2, user.DequeueDomainEvents().Count); // exactly one event per real assignment
    }

    [Fact]
    public void RevokeRole_RemovesTheAssignment_AndIsIdempotent()
    {
        User user = CreateUser();
        var roleId = Guid.NewGuid();
        user.AssignRole(roleId);
        user.DequeueDomainEvents();

        user.RevokeRole(roleId);
        user.RevokeRole(roleId); // no-op

        Assert.Empty(user.Roles);
        Assert.IsType<UserRoleRevoked>(Assert.Single(user.DequeueDomainEvents()));
    }
}
