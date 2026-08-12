namespace GymOS.BuildingBlocks.Application.Abstractions;

/// <summary>One email, already rendered. Both bodies are required; see <see cref="IEmailSender"/>.</summary>
/// <param name="To">A single recipient address. Multiple recipients are a notification concern (ADR-53).</param>
/// <param name="Subject">Plain text, one line.</param>
/// <param name="HtmlBody">The rendered HTML body.</param>
/// <param name="TextBody">
/// The same message as plain text. <b>Not optional.</b> A security email that arrives as an unstyled
/// blob — or as nothing at all — in a client that refuses HTML is a security email nobody trusts, and
/// the two messages this interface carries are exactly the ones that must survive a strict reader.
/// </param>
public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody);

/// <summary>
/// Hands a rendered message to a transport. <b>Deliberately the smallest thing that could work</b>
/// (ADR-53).
///
/// <para>The frozen architecture (ARCHITECTURE-PLATFORM-CAPABILITIES.md §4.1) routes every
/// user-facing message through <c>GymOS.Modules.Notifications</c> — templates, recipient preferences,
/// digests, deliveries, and channel senders drawn from the Integrations capability. None of that
/// exists yet, and building it to send an invitation link would have put a milestone of work in front
/// of a defect: an invited administrator could never sign in.</para>
///
/// <para>ADR-53 permits this interface for <b>exactly two messages</b> — the invitation/activation
/// link and an administrator-initiated password reset — on the grounds that §4.1's own design
/// declares security a mandatory category that cannot be muted. Those two therefore resolve no
/// preference, honour no digest, and respect no quiet hours, which is most of what the module would
/// have contributed.</para>
///
/// <para><b>It knows nothing about identity, and nothing about why it is sending.</b> That is what
/// makes it something Notifications can absorb as its Email channel sender rather than something it
/// has to replace: a recipient, a subject, and a rendered body is exactly the shape a channel sender
/// takes. Adding a third message, or a template concept, or a recipient list here is a decision to
/// revisit ADR-53 — not a routine change.</para>
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Sends the message, or reports why it could not be handed over.
    ///
    /// <para>Returns rather than throws because <b>a delivery failure must not undo the work that
    /// caused it</b>: inviting an administrator creates the account, and an SMTP server being down is
    /// not a reason to roll that back and tell the caller nothing happened. The caller decides what a
    /// failure means to it — for the two producers here, it means reporting a partial success and
    /// leaving the re-send action as the recovery path.</para>
    ///
    /// <para>"Sent" means the transport accepted it. It never means delivered: a bounce, a spam
    /// filing, and a wrong-but-well-formed address are all indistinguishable from here.</para>
    /// </summary>
    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <param name="Sent">Whether the transport accepted the message.</param>
/// <param name="Error">
/// Why it did not, for logging and for the caller's response. Never contains the message body —
/// these two messages carry one-time credentials in their links.
/// </param>
public sealed record EmailSendResult(bool Sent, string? Error)
{
    public static EmailSendResult Success { get; } = new(true, null);

    public static EmailSendResult Failed(string error) => new(false, error);
}
