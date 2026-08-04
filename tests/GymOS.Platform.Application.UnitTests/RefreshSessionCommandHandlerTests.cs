using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.Platform.Application.Identity;
using GymOS.Platform.Application.Identity.RefreshSession;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;
using Xunit;

namespace GymOS.Platform.Application.UnitTests;

/// <summary>
/// Rotation and reuse detection (AD-4) — the mechanism that limits the damage of a
/// stolen refresh token to a single use.
/// </summary>
public sealed class RefreshSessionCommandHandlerTests
{
    private const string Ip = "198.51.100.30";

    [Fact]
    public async Task ActiveToken_IsRotated_AndTheOldOneLinksToItsSuccessor()
    {
        var context = new IdentityTestContext();
        User user = context.GivenActiveUser();
        RefreshToken original = context.GivenRefreshToken(user.Id, "raw-1");

        Result<AuthenticatedSession> result = await RefreshAsync(context, "raw-1");

        Assert.True(result.IsSuccess);
        Assert.True(original.IsRevoked);
        Assert.Equal(RevocationReasons.Rotated, original.RevocationReason);

        // The forward link is what makes the chain walkable — without it, reuse
        // detection has nothing to revoke.
        string successorHash = context.RefreshTokenGenerator.ComputeHash(result.Value.RefreshToken);
        Assert.Equal(successorHash, original.ReplacedByTokenHash);

        RefreshToken successor = Assert.Single(
            context.RefreshTokens.Tokens,
            token => token.TokenHash == successorHash);
        Assert.True(successor.IsActive(context.Clock.UtcNow));
    }

    [Fact]
    public async Task RotationExtendsTheSessionWindow()
    {
        var context = new IdentityTestContext(policy => policy.RefreshTokenLifetime = TimeSpan.FromDays(7));
        User user = context.GivenActiveUser();
        context.GivenRefreshToken(user.Id, "raw-1");

        context.Clock.Advance(TimeSpan.FromDays(3));
        Result<AuthenticatedSession> result = await RefreshAsync(context, "raw-1");

        Assert.Equal(context.Clock.UtcNow.AddDays(7), result.Value.RefreshTokenExpiresAtUtc);
    }

    [Fact]
    public async Task ReplayingARotatedToken_IsRefused_AndRevokesTheRestOfTheChain()
    {
        var context = new IdentityTestContext();
        User user = context.GivenActiveUser();
        context.GivenRefreshToken(user.Id, "raw-1");

        // A legitimate client rotates twice: raw-1 → generated#1 → generated#2.
        Result<AuthenticatedSession> first = await RefreshAsync(context, "raw-1");
        Result<AuthenticatedSession> second = await RefreshAsync(context, first.Value.RefreshToken);
        Assert.True(second.IsSuccess);

        // Now the stolen copy of raw-1 turns up.
        Result<AuthenticatedSession> replay = await RefreshAsync(context, "raw-1");

        Assert.Equal(AuthenticationErrors.InvalidRefreshToken, replay.Error);

        // Everything descended from the leaked token is dead, so whoever holds the
        // live session — attacker or victim — is signed out and must authenticate.
        Assert.All(context.RefreshTokens.Tokens, token => Assert.True(token.IsRevoked));
        Assert.Contains(
            context.RefreshTokens.Tokens,
            token => token.RevocationReason == RevocationReasons.ReuseDetected);
    }

    [Fact]
    public async Task ReuseDetection_LeavesOtherDevicesSignedIn()
    {
        var context = new IdentityTestContext();
        User user = context.GivenActiveUser();
        context.GivenRefreshToken(user.Id, "laptop-1");
        RefreshToken phone = context.GivenRefreshToken(user.Id, "phone-1");

        await RefreshAsync(context, "laptop-1");   // laptop rotates
        await RefreshAsync(context, "laptop-1");   // the leaked laptop token is replayed

        // The phone holds a chain of its own; a leak on the laptop is no reason to
        // sign the user out everywhere.
        Assert.False(phone.IsRevoked);
    }

    [Fact]
    public async Task ExpiredToken_IsRefused()
    {
        var context = new IdentityTestContext();
        User user = context.GivenActiveUser();
        context.GivenRefreshToken(user.Id, "raw-1", lifetime: TimeSpan.FromHours(1));

        context.Clock.Advance(TimeSpan.FromHours(2));
        Result<AuthenticatedSession> result = await RefreshAsync(context, "raw-1");

        Assert.Equal(AuthenticationErrors.InvalidRefreshToken, result.Error);
    }

    [Theory]
    [InlineData("never-issued")]
    [InlineData("")]
    public async Task UnknownOrMissingToken_IsRefused(string presented)
    {
        var context = new IdentityTestContext();
        context.GivenActiveUser();

        Result<AuthenticatedSession> result = await RefreshAsync(context, presented);

        Assert.Equal(AuthenticationErrors.InvalidRefreshToken, result.Error);
    }

    [Fact]
    public async Task SuspendedOwner_CannotRefresh_AndTheTokenIsRevoked()
    {
        var context = new IdentityTestContext();
        User user = context.GivenActiveUser();
        RefreshToken token = context.GivenRefreshToken(user.Id, "raw-1");

        // Suspension after the session was established: refresh is the boundary where
        // account state is re-read, so the session dies within one access-token lifetime.
        user.Suspend("Compromised", context.Clock.UtcNow);

        Result<AuthenticatedSession> result = await RefreshAsync(context, "raw-1");

        Assert.Equal(AuthenticationErrors.AccountSuspended, result.Error);
        Assert.True(token.IsRevoked);
        Assert.Equal(RevocationReasons.AccountSuspended, token.RevocationReason);
    }

    [Fact]
    public async Task RefreshedTokenReflectsPermissionsAsTheyAreNow_NotAsTheyWere()
    {
        var context = new IdentityTestContext();
        User user = context.GivenActiveUser();
        context.GivenRefreshToken(user.Id, "raw-1");
        context.IdentityQueries.Permissions[user.Id] = ["users.read"];

        await RefreshAsync(context, "raw-1");
        context.IdentityQueries.Permissions[user.Id] = ["users.read", "users.manage"];
        Result<AuthenticatedSession> second = await RefreshAsync(context, "raw-refresh-1");

        Assert.True(second.IsSuccess);
        Assert.Equal(["users.read", "users.manage"], context.AccessTokens.Requests[^1].Permissions);
    }

    private static Task<Result<AuthenticatedSession>> RefreshAsync(
        IdentityTestContext context,
        string presentedToken) =>
        context.Dispatcher.SendAsync(new RefreshSessionCommand(presentedToken, Ip), CancellationToken.None);
}
