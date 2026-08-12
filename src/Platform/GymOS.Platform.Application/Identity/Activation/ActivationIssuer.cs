using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;

namespace GymOS.Platform.Application.Identity.Activation;

/// <summary>
/// Issues an activation token for a user and sends the link that carries it.
///
/// <para>Public and shared by the three producers — invite, re-send, and administrator-initiated
/// reset — because the ordering is the part that has to be right every time: invalidate whatever is
/// outstanding, issue, save, <em>then</em> send. Three copies would be three chances to send before
/// saving, which mails a link the database does not know about.</para>
///
/// <para><b>It stages and sends; it never saves.</b> Same rule as <c>InvoiceIssuer</c>, and
/// structurally guaranteed the same way — this class has no <c>IUnitOfWork</c> to call. The caller
/// commits the token alongside whatever else its transaction is doing, and then asks for the send.
/// </para>
/// </summary>
public sealed class ActivationIssuer
{
    private readonly IUserActivationTokenRepository _tokens;
    private readonly IActivationTokenGenerator _generator;
    private readonly IActivationLinks _links;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public ActivationIssuer(
        IUserActivationTokenRepository tokens,
        IActivationTokenGenerator generator,
        IActivationLinks links,
        ICurrentUser currentUser,
        IClock clock)
    {
        _tokens = tokens;
        _generator = generator;
        _links = links;
        _currentUser = currentUser;
        _clock = clock;
    }

    /// <summary>
    /// Stages a new token, invalidating any the user already holds, and returns the raw value.
    ///
    /// <para>The raw token is returned rather than stored so the caller can hand it to
    /// <see cref="SendAsync"/> after its save. It must not be logged, echoed in a response, or put in
    /// an audit row — it is the credential itself.</para>
    /// </summary>
    public async Task<string> IssueAsync(
        Guid userId,
        ActivationPurpose purpose,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;

        // Before issuing, never after: a user holds at most one live link, so "I re-sent it because
        // the first might have been intercepted" is true rather than wishful.
        await _tokens.InvalidateOutstandingAsync(userId, now, cancellationToken);

        string rawToken = _generator.Generate();

        _tokens.Add(UserActivationToken.Issue(
            userId,
            _generator.ComputeHash(rawToken),
            purpose,
            now.Add(_links.LifetimeFor(purpose)),
            now,
            _currentUser.UserId));

        return rawToken;
    }

    /// <summary>
    /// Sends the link. <b>Call after the save has committed</b>, so a mail that goes out always
    /// refers to a token that exists.
    ///
    /// <para>The outbox would be the right home for this and is not available — its processor does
    /// not exist yet (capture side only). So the send is inline, and the two consequences are stated
    /// rather than hidden: a crash between the commit and this call loses one email, and the re-send
    /// action is the recovery path. This moves onto the outbox when the processor lands.</para>
    /// </summary>
    public Task<EmailSendResult> SendAsync(
        ActivationPurpose purpose,
        ActivationRecipient recipient,
        string rawToken,
        CancellationToken cancellationToken) =>
        _links.SendAsync(purpose, recipient, rawToken, cancellationToken);
}
