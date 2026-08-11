using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.Platform.Application.Identity;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Application.Identity.ChangePassword;
using GymOS.Platform.Application.Identity.GetCurrentUser;
using GymOS.Platform.Application.Identity.SignOut;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;
using Xunit;

namespace GymOS.Platform.Application.UnitTests;

/// <summary>Sign-out, change-password, and <c>/me</c>.</summary>
public sealed class SessionLifecycleTests
{
    private const string Password = "correct-horse-battery";
    private const string Ip = "198.51.100.40";

    [Fact]
    public async Task SignOut_RevokesTheCallersOwnToken()
    {
        var context = new IdentityTestContext();
        User user = context.GivenActiveUser(password: Password);
        RefreshToken token = context.GivenRefreshToken(user.Id, "raw-1");

        Result result = await context.Dispatcher.SendAsync(
            new SignOutCommand(user.Id, "raw-1", Ip), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(token.IsRevoked);
        Assert.Equal(RevocationReasons.SignedOut, token.RevocationReason);
    }

    [Fact]
    public async Task SignOut_WillNotRevokeAnotherUsersToken_ButStillReportsSuccess()
    {
        var context = new IdentityTestContext();
        User owner = context.GivenActiveUser("owner@gymos.test", Password);
        User attacker = context.GivenActiveUser("attacker@gymos.test", Password);
        RefreshToken ownersToken = context.GivenRefreshToken(owner.Id, "raw-owner");

        Result result = await context.Dispatcher.SendAsync(
            new SignOutCommand(attacker.Id, "raw-owner", Ip), CancellationToken.None);

        // Success without effect: reporting the difference would let an authenticated
        // user probe whether a given token string is live.
        Assert.True(result.IsSuccess);
        Assert.False(ownersToken.IsRevoked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("already-gone")]
    public async Task SignOut_IsIdempotent(string? presented)
    {
        var context = new IdentityTestContext();
        User user = context.GivenActiveUser(password: Password);

        Result result = await context.Dispatcher.SendAsync(
            new SignOutCommand(user.Id, presented, Ip), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ChangePassword_RotatesTheStampAndEndsEverySession()
    {
        var context = new IdentityTestContext();
        User user = context.GivenActiveUser(password: Password);
        RefreshToken laptop = context.GivenRefreshToken(user.Id, "raw-laptop");
        RefreshToken phone = context.GivenRefreshToken(user.Id, "raw-phone");
        Guid stampBefore = user.SecurityStamp;

        Result result = await ChangePasswordAsync(context, user.Id, Password, "a-brand-new-password");

        Assert.True(result.IsSuccess);
        Assert.NotEqual(stampBefore, user.SecurityStamp);
        Assert.Equal(context.PasswordHasher.Hash("a-brand-new-password"), user.PasswordHash);

        // The stamp rotation expires access tokens; revoking the refresh tokens is what
        // stops a thief from simply refreshing back in. Half the job would be no job.
        Assert.True(laptop.IsRevoked);
        Assert.True(phone.IsRevoked);
        Assert.Equal(RevocationReasons.PasswordChanged, laptop.RevocationReason);
    }

    [Fact]
    public async Task ChangePassword_WithTheWrongCurrentPassword_ChangesNoPasswordButIsCountedAndAudited()
    {
        var context = new IdentityTestContext();
        User user = context.GivenActiveUser(password: Password);
        RefreshToken token = context.GivenRefreshToken(user.Id, "raw-1");
        string hashBefore = user.PasswordHash!;

        Result result = await ChangePasswordAsync(context, user.Id, "not-my-password", "a-brand-new-password");

        Assert.Equal(AuthenticationErrors.InvalidCurrentPassword, result.Error);
        Assert.Equal(hashBefore, user.PasswordHash);
        Assert.False(token.IsRevoked);

        // The attempt IS recorded — the endpoint verifies a password, so leaving failures
        // uncounted and untraced made it an unlimited oracle for anyone holding a stolen access
        // token. One save: the failed-attempt counter and its audit row commit together.
        Assert.Equal(1, user.FailedLoginAttempts);
        Assert.Equal(1, context.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task ChangePassword_RepeatedWrongCurrentPasswords_LockTheAccount()
    {
        var context = new IdentityTestContext(policy => policy.MaxFailedAttempts = 3);
        User user = context.GivenActiveUser(password: Password);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            await ChangePasswordAsync(context, user.Id, "not-my-password", "a-brand-new-password");
        }

        // Lockout applies to this door as well as the login form: they are attempts on one
        // account, and a guess that lands here ends every session the real owner holds.
        Assert.True(user.IsLockedOut(context.Clock.UtcNow));

        Result afterLockout = await ChangePasswordAsync(
            context, user.Id, Password, "a-brand-new-password");

        // Even the CORRECT password is refused while locked out, and the hash is untouched.
        Assert.Equal(AuthenticationErrors.AccountLocked, afterLockout.Error);
    }

    [Fact]
    public async Task ChangePassword_RejectsANewPasswordBelowThePolicy()
    {
        var context = new IdentityTestContext(policy => policy.MinimumPasswordLength = 12);
        User user = context.GivenActiveUser(password: Password);
        string hashBefore = user.PasswordHash!;

        Result result = await ChangePasswordAsync(context, user.Id, Password, "short");

        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Equal("auth.weak_password", result.Error.Code);
        Assert.Equal(hashBefore, user.PasswordHash);
    }

    [Fact]
    public async Task ChangePassword_ForAVanishedUser_ReportsNotFound()
    {
        var context = new IdentityTestContext();

        Result result = await ChangePasswordAsync(context, Guid.NewGuid(), Password, "a-brand-new-password");

        Assert.Equal(AuthenticationErrors.UserNotFound, result.Error);
    }

    [Fact]
    public async Task GetCurrentUser_ReturnsTheProfileWithItsEffectivePermissions()
    {
        var context = new IdentityTestContext();
        User user = context.GivenActiveUser(password: Password);
        context.IdentityQueries.Permissions[user.Id] = ["users.read", "audit.read"];

        Result<CurrentUser> result = await context.Dispatcher.QueryAsync(
            new GetCurrentUserQuery(user.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(user.Email, result.Value.Profile.Email);
        Assert.Equal(nameof(UserStatus.Active), result.Value.Profile.Status);
        Assert.Equal(["SuperAdmin"], result.Value.Profile.Roles);
        Assert.Equal(["users.read", "audit.read"], result.Value.Permissions);
    }

    [Fact]
    public async Task GetCurrentUser_ForATokenWhoseSubjectIsGone_ReportsNotFound()
    {
        var context = new IdentityTestContext();

        Result<CurrentUser> result = await context.Dispatcher.QueryAsync(
            new GetCurrentUserQuery(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(AuthenticationErrors.UserNotFound, result.Error);
    }

    [Fact]
    public async Task GetCurrentUser_WithNoRolesYet_ReturnsAnEmptyPermissionSet()
    {
        var context = new IdentityTestContext();
        User user = context.GivenActiveUser(password: Password);

        Result<CurrentUser> result = await context.Dispatcher.QueryAsync(
            new GetCurrentUserQuery(user.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Permissions);
    }

    private static Task<Result> ChangePasswordAsync(
        IdentityTestContext context,
        Guid userId,
        string current,
        string next) =>
        context.Dispatcher.SendAsync(
            new ChangePasswordCommand(userId, current, next, Ip), CancellationToken.None);
}
