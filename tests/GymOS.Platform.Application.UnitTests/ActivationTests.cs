using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.Platform.Application.Identity;
using GymOS.Platform.Application.Identity.Activation;
using GymOS.Platform.Application.Identity.Users;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;
using Xunit;

namespace GymOS.Platform.Application.UnitTests;

/// <summary>
/// The activation flow (ADR-53) — the increment that made an invited administrator able to sign in
/// at all.
///
/// <para>The cases that matter are the refusals and the ordering, not the happy path: a link that
/// works twice, a link that survives a rejected password, or a reset that leaves an old session open
/// are each worth more than the flow working when nothing goes wrong.</para>
/// </summary>
public sealed class ActivationTests
{
    private const string Manage = PlatformPermissions.UsersManage;
    private const string GoodPassword = "a-perfectly-fine-password";

    [Fact]
    public async Task InvitingAnAdministrator_IssuesALinkAndSendsIt()
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);

        Result<InvitedUser> result = await context.Dispatcher.SendAsync(
            new InviteUserCommand("invited@gymos.test", "New", "Admin", []),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.EmailSent);

        // The token that was mailed is the one whose hash was stored — the whole point of the flow.
        UserActivationToken stored = Assert.Single(context.ActivationTokens.Tokens);
        var (purpose, recipient, rawToken) = Assert.Single(context.ActivationLinks.Sent);

        Assert.Equal(ActivationPurpose.Invitation, purpose);
        Assert.Equal("invited@gymos.test", recipient.Email);
        Assert.Equal(context.ActivationTokenGenerator.ComputeHash(rawToken), stored.TokenHash);

        // And the raw value is nowhere in the audit trail.
        Assert.DoesNotContain(
            context.AuditTrail.Entries,
            entry => entry.Details?.Values.Any(value => value == rawToken) == true);
    }

    [Fact]
    public async Task AnUnreachableMailServer_DoesNotUndoTheInvitation()
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        context.ActivationLinks.FailSends = true;

        Result<InvitedUser> result = await context.Dispatcher.SendAsync(
            new InviteUserCommand("invited@gymos.test", "New", "Admin", []),
            CancellationToken.None);

        // The account is created and reported as created; only the delivery flag says otherwise.
        // Rolling the invite back would tell an administrator that nothing happened when it did.
        Assert.True(result.IsSuccess);
        Assert.False(result.Value.EmailSent);
        Assert.Single(context.Users.Users);
        Assert.Single(context.ActivationTokens.Tokens);
    }

    [Fact]
    public async Task ALinkWorksOnceAndActivatesTheAccount()
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        string token = await InviteAndCaptureTokenAsync(context, "invited@gymos.test");

        Result first = await context.Dispatcher.SendAsync(
            new SetPasswordCommand(token, GoodPassword), CancellationToken.None);

        Assert.True(first.IsSuccess);

        User user = Assert.Single(context.Users.Users);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.NotNull(user.PasswordHash);

        // The second attempt is refused, and refused with the same error every other failure gives.
        Result second = await context.Dispatcher.SendAsync(
            new SetPasswordCommand(token, "another-perfectly-fine-password"), CancellationToken.None);

        Assert.True(second.IsFailure);
        Assert.Equal(ActivationErrors.LinkNotUsable.Code, second.Error!.Code);
    }

    [Fact]
    public async Task ARejectedPassword_StillSpendsTheLink()
    {
        // The ordering that stops a link being replayable for its whole lifetime by anyone willing
        // to submit a password the policy refuses: the attempt would otherwise cost them nothing.
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        string token = await InviteAndCaptureTokenAsync(context, "invited@gymos.test");

        Result rejected = await context.Dispatcher.SendAsync(
            new SetPasswordCommand(token, "short"), CancellationToken.None);

        Assert.True(rejected.IsFailure);

        // Told what was wrong with the password — but the link is gone.
        Assert.NotEqual(ActivationErrors.LinkNotUsable.Code, rejected.Error!.Code);
        Assert.True(Assert.Single(context.ActivationTokens.Tokens).IsConsumed);

        Result retry = await context.Dispatcher.SendAsync(
            new SetPasswordCommand(token, GoodPassword), CancellationToken.None);

        Assert.True(retry.IsFailure);
        Assert.Equal(ActivationErrors.LinkNotUsable.Code, retry.Error!.Code);
        Assert.Null(Assert.Single(context.Users.Users).PasswordHash);
    }

    [Fact]
    public async Task EveryFailureAnswersIdentically()
    {
        // The anti-oracle assertion, and the one most likely to rot as somebody adds a helpful
        // message: a distinct "expired" would confirm that a token had once been real.
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        string token = await InviteAndCaptureTokenAsync(context, "invited@gymos.test");

        Result neverExisted = await context.Dispatcher.SendAsync(
            new SetPasswordCommand("no-such-token", GoodPassword), CancellationToken.None);

        Result empty = await context.Dispatcher.SendAsync(
            new SetPasswordCommand(string.Empty, GoodPassword), CancellationToken.None);

        context.Clock.Advance(TimeSpan.FromDays(30));

        Result expired = await context.Dispatcher.SendAsync(
            new SetPasswordCommand(token, GoodPassword), CancellationToken.None);

        foreach (Result failure in new[] { neverExisted, empty, expired })
        {
            Assert.True(failure.IsFailure);
            Assert.Equal(ActivationErrors.LinkNotUsable.Code, failure.Error!.Code);
            Assert.Equal(ActivationErrors.LinkNotUsable.Message, failure.Error.Message);
        }
    }

    [Fact]
    public async Task ReSendingAnInvitation_InvalidatesThePreviousLink()
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        string first = await InviteAndCaptureTokenAsync(context, "invited@gymos.test");
        Guid userId = context.Users.Users[0].Id;

        Result<ActivationDispatch> resent = await context.Dispatcher.SendAsync(
            new ResendInvitationCommand(userId), CancellationToken.None);

        Assert.True(resent.IsSuccess);

        // "I re-sent it because the first might have been intercepted" is only true if the first
        // one stops working.
        Result withOldLink = await context.Dispatcher.SendAsync(
            new SetPasswordCommand(first, GoodPassword), CancellationToken.None);

        Assert.True(withOldLink.IsFailure);
        Assert.Equal(ActivationErrors.LinkNotUsable.Code, withOldLink.Error!.Code);

        string second = context.ActivationLinks.Sent[^1].RawToken;
        Result withNewLink = await context.Dispatcher.SendAsync(
            new SetPasswordCommand(second, GoodPassword), CancellationToken.None);

        Assert.True(withNewLink.IsSuccess);
    }

    [Fact]
    public async Task SettingAPassword_EndsEverySessionThatWasAlreadyOpen()
    {
        // A reset is often prompted by the belief that somebody else is in. Leaving their session
        // alive would make it ceremonial.
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        User user = context.GivenActiveUser("established@gymos.test");
        RefreshToken session = context.GivenRefreshToken(user.Id, "an-open-session");

        Result<ActivationDispatch> sent = await context.Dispatcher.SendAsync(
            new SendPasswordResetCommand(user.Id), CancellationToken.None);

        Assert.True(sent.IsSuccess);
        Assert.Equal(ActivationPurpose.PasswordReset, context.ActivationLinks.Sent[^1].Purpose);

        Result result = await context.Dispatcher.SendAsync(
            new SetPasswordCommand(context.ActivationLinks.Sent[^1].RawToken, GoodPassword),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(session.IsRevoked);
    }

    [Fact]
    public async Task ASuspendedAdministrator_CannotBeSentAResetAndCannotSetAPassword()
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        User user = context.GivenActiveUser("established@gymos.test");

        // A link issued while the account was healthy...
        Result<ActivationDispatch> sent = await context.Dispatcher.SendAsync(
            new SendPasswordResetCommand(user.Id), CancellationToken.None);
        Assert.True(sent.IsSuccess);
        string token = context.ActivationLinks.Sent[^1].RawToken;

        user.Suspend("under investigation", context.Clock.UtcNow);

        // ...stops working the moment it is suspended: a reset must not be a way back in.
        Result used = await context.Dispatcher.SendAsync(
            new SetPasswordCommand(token, GoodPassword), CancellationToken.None);

        Assert.True(used.IsFailure);
        Assert.Equal(ActivationErrors.LinkNotUsable.Code, used.Error!.Code);

        // And a new one is refused outright, naming the fix rather than failing silently.
        Result<ActivationDispatch> refused = await context.Dispatcher.SendAsync(
            new SendPasswordResetCommand(user.Id), CancellationToken.None);

        Assert.True(refused.IsFailure);
        Assert.Equal("users.suspended_cannot_reset", refused.Error!.Code);
    }

    [Fact]
    public async Task TheTwoAdministratorActionsRefuseTheAccountTypeTheyDoNotFit()
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        await InviteAndCaptureTokenAsync(context, "pending@gymos.test");
        Guid pendingId = context.Users.Users[0].Id;
        User active = context.GivenActiveUser("active@gymos.test");

        // Resetting a password somebody never had, and re-inviting somebody already established:
        // both are refused rather than quietly sending the other message.
        Result<ActivationDispatch> resetPending = await context.Dispatcher.SendAsync(
            new SendPasswordResetCommand(pendingId), CancellationToken.None);

        Result<ActivationDispatch> reinviteActive = await context.Dispatcher.SendAsync(
            new ResendInvitationCommand(active.Id), CancellationToken.None);

        Assert.Equal("users.still_pending_activation", resetPending.Error!.Code);
        Assert.Equal("users.not_pending_activation", reinviteActive.Error!.Code);
    }

    [Fact]
    public async Task CheckingALink_SaysOnlyWhetherItIsUsable()
    {
        var context = new IdentityTestContext().ActingAs(Guid.NewGuid(), Manage);
        string token = await InviteAndCaptureTokenAsync(context, "invited@gymos.test");

        Result live = await context.Dispatcher.QueryAsync(
            new CheckActivationTokenQuery(token), CancellationToken.None);

        Assert.True(live.IsSuccess);

        context.Clock.Advance(TimeSpan.FromDays(30));

        Result dead = await context.Dispatcher.QueryAsync(
            new CheckActivationTokenQuery(token), CancellationToken.None);

        Assert.True(dead.IsFailure);
        Assert.Equal(ActivationErrors.LinkNotUsable.Code, dead.Error!.Code);
    }

    private static async Task<string> InviteAndCaptureTokenAsync(
        IdentityTestContext context,
        string email)
    {
        Result<InvitedUser> invited = await context.Dispatcher.SendAsync(
            new InviteUserCommand(email, "New", "Admin", []), CancellationToken.None);

        Assert.True(invited.IsSuccess);

        return context.ActivationLinks.Sent[^1].RawToken;
    }
}
