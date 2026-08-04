using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.Platform.Application.Identity;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Application.Identity.Users;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;
using Xunit;

namespace GymOS.Platform.Application.UnitTests;

/// <summary>
/// Administering administrators. The interesting cases are the refusals: the guards that stop
/// an ordinary-looking edit from locking everyone out of the platform.
/// </summary>
public sealed class UserManagementTests
{
    private const string Manage = PlatformPermissions.UsersManage;

    [Fact]
    public async Task InvitedUser_IsCreatedWithoutAPasswordAndCannotSignIn()
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        Role admin = context.GivenPlatformRole(SystemRoles.PlatformAdmin);

        Result<UserDetail> result = await context.Dispatcher.SendAsync(
            new InviteUserCommand("new.admin@gymos.test", "New", "Admin", [admin.Id]),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        User created = Assert.Single(context.Users.Users);
        Assert.Equal(UserStatus.PendingActivation, created.Status);
        Assert.Null(created.PasswordHash);
        Assert.Equal(admin.Id, Assert.Single(created.Roles).RoleId);
    }

    [Fact]
    public async Task InvitingAnExistingEmail_IsAConflict()
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        User existing = context.GivenActiveUser("taken@gymos.test");

        Result<UserDetail> result = await context.Dispatcher.SendAsync(
            new InviteUserCommand(existing.Email.ToUpperInvariant(), "Someone", "Else", []),
            CancellationToken.None);

        // Matched on the normalized email, so a different casing is still the same account.
        Assert.Equal(ErrorKind.Conflict, result.Error!.Kind);
        Assert.Equal("users.email_taken", result.Error.Code);
    }

    [Theory]
    [InlineData("no-at-sign")]
    [InlineData("")]
    public async Task InvitingAnInvalidEmail_IsAValidationFailure_NotAnException(string email)
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);

        Result<UserDetail> result = await context.Dispatcher.SendAsync(
            new InviteUserCommand(email, "New", "Admin", []),
            CancellationToken.None);

        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Empty(context.Users.Users);
    }

    [Fact]
    public async Task InvitingWithAnUnknownRole_FailsAndCreatesNothing()
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);

        Result<UserDetail> result = await context.Dispatcher.SendAsync(
            new InviteUserCommand("new.admin@gymos.test", "New", "Admin", [Guid.NewGuid()]),
            CancellationToken.None);

        Assert.Equal("users.unknown_role", result.Error!.Code);
        Assert.Empty(context.Users.Users);
    }

    [Fact]
    public async Task AGymScopedRole_CannotBeGrantedToAPlatformAdministrator()
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        Role tenantRole = Role.Create("Reception", "Front desk", RoleScope.Tenant, tenantId: Guid.NewGuid());
        context.Roles.Seed(tenantRole);

        Result<UserDetail> result = await context.Dispatcher.SendAsync(
            new InviteUserCommand("new.admin@gymos.test", "New", "Admin", [tenantRole.Id]),
            CancellationToken.None);

        // Silently ignoring it would grant access inside a gym that nobody chose.
        Assert.Equal("users.role_not_platform_scoped", result.Error!.Code);
    }

    [Fact]
    public async Task Suspension_EndsEverySessionImmediately()
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        User target = context.GivenActiveUser("target@gymos.test");
        RefreshToken session = context.GivenRefreshToken(target.Id, "raw-target");
        Guid stampBefore = target.SecurityStamp;

        Result<UserDetail> result = await context.Dispatcher.SendAsync(
            new SuspendUserCommand(target.Id, "Left the company", "1"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(UserStatus.Suspended, target.Status);

        // Both halves are needed: the rotated stamp invalidates access tokens, the revoked
        // refresh token stops them getting a new one.
        Assert.NotEqual(stampBefore, target.SecurityStamp);
        Assert.True(session.IsRevoked);
        Assert.Equal(RevocationReasons.AccountSuspended, session.RevocationReason);
    }

    [Fact]
    public async Task SuspensionRequiresAReason()
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        User target = context.GivenActiveUser("target@gymos.test");

        Result<UserDetail> result = await context.Dispatcher.SendAsync(
            new SuspendUserCommand(target.Id, "   ", "1"), CancellationToken.None);

        Assert.Equal("users.reason_required", result.Error!.Code);
        Assert.Equal(UserStatus.Active, target.Status);
    }

    [Fact]
    public async Task AnAdministratorCannotSuspendThemselves()
    {
        var context = new IdentityTestContext();
        User self = context.GivenActiveUser("self@gymos.test");
        context.ActingAs(self.Id, Manage);

        Result<UserDetail> result = await context.Dispatcher.SendAsync(
            new SuspendUserCommand(self.Id, "Oops", "1"), CancellationToken.None);

        // Recoverable only by another administrator — and possibly by nobody.
        Assert.Equal(UserErrors.CannotSuspendSelf, result.Error);
        Assert.Equal(UserStatus.Active, self.Status);
    }

    [Fact]
    public async Task TheLastActiveSuperAdmin_CannotBeSuspended()
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        Role superAdmin = context.GivenPlatformRole(SystemRoles.SuperAdmin);
        User target = context.GivenActiveUser("only.superadmin@gymos.test");
        target.AssignRole(superAdmin.Id);
        context.UserQueries.ActiveUsersPerRole[SystemRoles.SuperAdmin] = 1;

        Result<UserDetail> result = await context.Dispatcher.SendAsync(
            new SuspendUserCommand(target.Id, "Cleaning up", "1"), CancellationToken.None);

        Assert.Equal(UserErrors.LastSuperAdmin, result.Error);
        Assert.Equal(UserStatus.Active, target.Status);
    }

    [Fact]
    public async Task ASuperAdminAmongOthers_CanBeSuspended()
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        Role superAdmin = context.GivenPlatformRole(SystemRoles.SuperAdmin);
        User target = context.GivenActiveUser("one.of.several@gymos.test");
        target.AssignRole(superAdmin.Id);
        context.UserQueries.ActiveUsersPerRole[SystemRoles.SuperAdmin] = 2;

        Result<UserDetail> result = await context.Dispatcher.SendAsync(
            new SuspendUserCommand(target.Id, "Leave of absence", "1"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(UserStatus.Suspended, target.Status);
    }

    [Fact]
    public async Task Reactivation_RestoresPendingActivation_ForAnAccountThatNeverSetAPassword()
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        var invited = User.Create("invited@gymos.test", "Never", "Activated");
        invited.Suspend("Invited then suspended", IdentityTestContext.Start);
        context.Users.Seed(invited);

        Result<UserDetail> result = await context.Dispatcher.SendAsync(
            new ReactivateUserCommand(invited.Id, "1"), CancellationToken.None);

        // Reactivating must not hand out a working login the owner never had.
        Assert.True(result.IsSuccess);
        Assert.Equal(UserStatus.PendingActivation, invited.Status);
    }

    [Fact]
    public async Task ReactivatingAnActiveAccount_IsAConflict()
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        User target = context.GivenActiveUser("active@gymos.test");

        Result<UserDetail> result = await context.Dispatcher.SendAsync(
            new ReactivateUserCommand(target.Id, "1"), CancellationToken.None);

        Assert.Equal(UserErrors.NotSuspended, result.Error);
    }

    [Fact]
    public async Task SettingRoles_ReconcilesToTheRequestedSet()
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        Role support = context.GivenPlatformRole(SystemRoles.SupportAgent);
        Role billing = context.GivenPlatformRole(SystemRoles.BillingAdmin);
        User target = context.GivenActiveUser("target@gymos.test");
        target.AssignRole(support.Id);

        Result<UserDetail> result = await context.Dispatcher.SendAsync(
            new SetUserRolesCommand(target.Id, [billing.Id], "1"), CancellationToken.None);

        // Declarative: the sent set is the resulting set, so two clients converge instead of
        // composing into a state neither chose.
        Assert.True(result.IsSuccess);
        Assert.Equal(billing.Id, Assert.Single(target.Roles).RoleId);
    }

    [Fact]
    public async Task SettingAnEmptyRoleSet_RemovesEveryRole()
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        Role support = context.GivenPlatformRole(SystemRoles.SupportAgent);
        User target = context.GivenActiveUser("target@gymos.test");
        target.AssignRole(support.Id);

        Result<UserDetail> result = await context.Dispatcher.SendAsync(
            new SetUserRolesCommand(target.Id, [], "1"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(target.Roles);
    }

    [Fact]
    public async Task AnAdministratorCannotChangeTheirOwnRoles()
    {
        var context = new IdentityTestContext();
        Role superAdmin = context.GivenPlatformRole(SystemRoles.SuperAdmin);
        User self = context.GivenActiveUser("self@gymos.test");
        context.ActingAs(self.Id, Manage);

        Result<UserDetail> result = await context.Dispatcher.SendAsync(
            new SetUserRolesCommand(self.Id, [superAdmin.Id], "1"), CancellationToken.None);

        // Blocks self-elevation and self-demotion in one rule.
        Assert.Equal(UserErrors.CannotChangeOwnRoles, result.Error);
        Assert.Empty(self.Roles);
    }

    [Fact]
    public async Task RemovingSuperAdminFromItsLastHolder_IsRefused()
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        Role superAdmin = context.GivenPlatformRole(SystemRoles.SuperAdmin);
        Role support = context.GivenPlatformRole(SystemRoles.SupportAgent);
        User target = context.GivenActiveUser("only.superadmin@gymos.test");
        target.AssignRole(superAdmin.Id);
        context.UserQueries.ActiveUsersPerRole[SystemRoles.SuperAdmin] = 1;

        Result<UserDetail> result = await context.Dispatcher.SendAsync(
            new SetUserRolesCommand(target.Id, [support.Id], "1"), CancellationToken.None);

        Assert.Equal(UserErrors.LastSuperAdmin, result.Error);
        Assert.Contains(target.Roles, assignment => assignment.RoleId == superAdmin.Id);
    }

    [Fact]
    public async Task KeepingSuperAdminWhileAddingAnotherRole_IsAllowedForTheLastHolder()
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        Role superAdmin = context.GivenPlatformRole(SystemRoles.SuperAdmin);
        Role billing = context.GivenPlatformRole(SystemRoles.BillingAdmin);
        User target = context.GivenActiveUser("only.superadmin@gymos.test");
        target.AssignRole(superAdmin.Id);
        context.UserQueries.ActiveUsersPerRole[SystemRoles.SuperAdmin] = 1;

        Result<UserDetail> result = await context.Dispatcher.SendAsync(
            new SetUserRolesCommand(target.Id, [superAdmin.Id, billing.Id], "1"), CancellationToken.None);

        // The guard protects the role, not the row: an edit that keeps SuperAdmin is fine.
        Assert.True(result.IsSuccess);
        Assert.Equal(2, target.Roles.Count);
    }

    [Fact]
    public async Task ActingOnAMissingUser_ReportsNotFound()
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        var absent = Guid.NewGuid();

        Result update = await context.Dispatcher.SendAsync(
            new UpdateUserCommand(absent, "A", "B", "1"), CancellationToken.None);
        Result suspend = await context.Dispatcher.SendAsync(
            new SuspendUserCommand(absent, "reason", "1"), CancellationToken.None);
        Result roles = await context.Dispatcher.SendAsync(
            new SetUserRolesCommand(absent, [], "1"), CancellationToken.None);

        Assert.All(
            new[] { update, suspend, roles },
            result => Assert.Equal(ErrorKind.NotFound, result.Error!.Kind));
    }
}
