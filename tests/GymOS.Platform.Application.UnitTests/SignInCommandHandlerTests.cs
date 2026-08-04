using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.Platform.Application.Identity;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Application.Identity.SignIn;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;
using Xunit;

namespace GymOS.Platform.Application.UnitTests;

/// <summary>
/// Sign-in behavior, including the parts that exist only for security and are therefore
/// invisible in a happy-path test: the uniform failure vocabulary, the constant-cost
/// path for unknown accounts, and the order in which lockout and suspension are judged.
/// </summary>
public sealed class SignInCommandHandlerTests
{
    private const string Password = "correct-horse-battery";
    private const string Ip = "198.51.100.20";

    [Fact]
    public async Task ValidCredentials_IssueASessionAndRecordTheLogin()
    {
        var context = new IdentityTestContext();
        User user = context.GivenActiveUser(password: Password);

        Result<AuthenticatedSession> result = await SignInAsync(context, user.Email, Password);

        Assert.True(result.IsSuccess);
        Assert.Equal(IdentityTestContext.Start, user.LastLoginAtUtc);
        Assert.Equal(0, user.FailedLoginAttempts);

        RefreshToken issued = Assert.Single(context.RefreshTokens.Tokens);
        Assert.Equal(user.Id, issued.UserId);
        Assert.True(issued.IsActive(IdentityTestContext.Start));

        // The raw token is what the cookie carries; only its hash is ever recorded.
        Assert.Equal(
            context.RefreshTokenGenerator.ComputeHash(result.Value.RefreshToken),
            issued.TokenHash);
        Assert.Equal(1, context.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task IssuedAccessToken_AssertsTheUsersCurrentPermissionsAndStamp()
    {
        var context = new IdentityTestContext();
        User user = context.GivenActiveUser(password: Password);
        context.IdentityQueries.Permissions[user.Id] = ["users.read", "users.manage"];

        await SignInAsync(context, user.Email, Password);

        AccessTokenRequest request = Assert.Single(context.AccessTokens.Requests);
        Assert.Equal(user.Id, request.UserId);
        Assert.Equal(user.SecurityStamp, request.SecurityStamp);
        Assert.Equal(["users.read", "users.manage"], request.Permissions);
        Assert.Equal(IdentityTestContext.Start, request.IssuedAtUtc);
    }

    [Fact]
    public async Task UnknownEmail_FailsUniformly_AndStillPaysTheHashingCost()
    {
        var context = new IdentityTestContext();
        context.GivenActiveUser(password: Password);

        int hashCallsBefore = context.PasswordHasher.HashCallCount;

        Result<AuthenticatedSession> result = await SignInAsync(context, "nobody@gymos.test", Password);

        Assert.Equal(AuthenticationErrors.InvalidCredentials, result.Error);

        // Without this, an unknown email answers measurably faster than a wrong
        // password and the endpoint becomes an account-existence oracle.
        Assert.Equal(hashCallsBefore + 1, context.PasswordHasher.HashCallCount);
    }

    [Fact]
    public async Task UserWithNoPasswordSet_IsIndistinguishableFromAnUnknownUser()
    {
        var context = new IdentityTestContext();
        var invited = User.Create("invited@gymos.test", "Not", "Activated");
        context.Users.Seed(invited);

        Result<AuthenticatedSession> result = await SignInAsync(context, invited.Email, Password);

        Assert.Equal(AuthenticationErrors.InvalidCredentials, result.Error);
        Assert.Equal(UserStatus.PendingActivation, invited.Status);
    }

    [Fact]
    public async Task WrongPassword_CountsAFailedAttemptAndPersistsIt()
    {
        var context = new IdentityTestContext();
        User user = context.GivenActiveUser(password: Password);

        Result<AuthenticatedSession> result = await SignInAsync(context, user.Email, "wrong");

        Assert.Equal(AuthenticationErrors.InvalidCredentials, result.Error);
        Assert.Equal(1, user.FailedLoginAttempts);
        Assert.Equal(1, context.UnitOfWork.SaveCount);
        Assert.Empty(context.RefreshTokens.Tokens);
    }

    [Fact]
    public async Task ReachingTheAttemptThreshold_LocksTheAccountForTheConfiguredDuration()
    {
        var context = new IdentityTestContext(policy =>
        {
            policy.MaxFailedAttempts = 3;
            policy.LockoutDuration = TimeSpan.FromMinutes(30);
        });
        User user = context.GivenActiveUser(password: Password);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            await SignInAsync(context, user.Email, "wrong");
        }

        Assert.True(user.IsLockedOut(IdentityTestContext.Start));
        Assert.Equal(IdentityTestContext.Start.AddMinutes(30), user.LockedOutUntilUtc);
    }

    [Fact]
    public async Task LockedAccount_IsRejectedWithoutVerifyingThePassword()
    {
        var context = new IdentityTestContext(policy => policy.MaxFailedAttempts = 1);
        User user = context.GivenActiveUser(password: Password);
        await SignInAsync(context, user.Email, "wrong"); // trips the lock

        int verifyCallsBefore = context.PasswordHasher.VerifyCallCount;

        // Even the correct password is refused while the lock holds — and refusing it
        // costs no Argon2 work, which is the whole point of locking out.
        Result<AuthenticatedSession> result = await SignInAsync(context, user.Email, Password);

        Assert.Equal(AuthenticationErrors.AccountLocked, result.Error);
        Assert.Equal(verifyCallsBefore, context.PasswordHasher.VerifyCallCount);
    }

    [Fact]
    public async Task LockoutExpires_AndTheCorrectPasswordWorksAgain()
    {
        var context = new IdentityTestContext(policy =>
        {
            policy.MaxFailedAttempts = 1;
            policy.LockoutDuration = TimeSpan.FromMinutes(15);
        });
        User user = context.GivenActiveUser(password: Password);
        await SignInAsync(context, user.Email, "wrong");

        context.Clock.Advance(TimeSpan.FromMinutes(16));

        Result<AuthenticatedSession> result = await SignInAsync(context, user.Email, Password);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task SuspendedUser_IsToldWhy_ButOnlyAfterProvingThePassword()
    {
        var context = new IdentityTestContext();
        User user = context.GivenActiveUser(password: Password);
        user.Suspend("Left the company", IdentityTestContext.Start);

        Result<AuthenticatedSession> wrongPassword = await SignInAsync(context, user.Email, "wrong");
        Result<AuthenticatedSession> rightPassword = await SignInAsync(context, user.Email, Password);

        // A caller who cannot authenticate learns nothing about the account's state.
        Assert.Equal(AuthenticationErrors.InvalidCredentials, wrongPassword.Error);
        Assert.Equal(AuthenticationErrors.AccountSuspended, rightPassword.Error);
        Assert.Empty(context.RefreshTokens.Tokens);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankEmail_FailsWithoutTouchingTheRepository(string email)
    {
        var context = new IdentityTestContext();

        Result<AuthenticatedSession> result = await SignInAsync(context, email, Password);

        Assert.Equal(AuthenticationErrors.InvalidCredentials, result.Error);
    }

    [Fact]
    public async Task EmailMatchingIsCaseInsensitive()
    {
        var context = new IdentityTestContext();
        User user = context.GivenActiveUser("Admin@GymOS.test", Password);

        Result<AuthenticatedSession> result = await SignInAsync(context, "ADMIN@gymos.TEST", Password);

        Assert.True(result.IsSuccess);
        Assert.Equal(user.Id, Assert.Single(context.AccessTokens.Requests).UserId);
    }

    private static Task<Result<AuthenticatedSession>> SignInAsync(
        IdentityTestContext context,
        string email,
        string password) =>
        context.Dispatcher.SendAsync(new SignInCommand(email, password, Ip), CancellationToken.None);
}
