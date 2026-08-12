using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Domain.Identity;

namespace GymOS.Platform.Application.Identity.Abstractions;

/// <summary>Who the link is for, and how to greet them.</summary>
public sealed record ActivationRecipient(string Email, string FirstName);

/// <summary>
/// Everything about an activation link that is not the token itself: how long it lives, where it
/// points, and how it reaches the person.
///
/// <para>One owner, because those three have to agree. The email states the window in its copy, the
/// token row enforces it, and the link's address is the console rather than this API — split across
/// three places, they drift, and the failure is an email that promises 72 hours for a token that dies
/// in one.</para>
///
/// <para>Implemented in Infrastructure (ADR-53's <c>IEmailSender</c>, the SMTP transport, the message
/// bodies, and the console's base URL all live there); the handlers depend on this and never learn
/// how mail leaves or what the link looks like.</para>
/// </summary>
public interface IActivationLinks
{
    /// <summary>How long a link of this purpose stays usable — the value the token's expiry is set from.</summary>
    TimeSpan LifetimeFor(ActivationPurpose purpose);

    /// <summary>
    /// Builds the link for <paramref name="rawToken"/> and sends the matching message.
    ///
    /// <para>Reports failure rather than throwing: an unreachable mail server must not undo the
    /// account work that triggered it (<see cref="IEmailSender"/>).</para>
    /// </summary>
    Task<EmailSendResult> SendAsync(
        ActivationPurpose purpose,
        ActivationRecipient recipient,
        string rawToken,
        CancellationToken cancellationToken);
}
