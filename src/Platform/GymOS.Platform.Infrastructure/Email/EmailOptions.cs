using System.ComponentModel.DataAnnotations;

namespace GymOS.Platform.Infrastructure.Email;

/// <summary>
/// How the two ADR-53 messages reach an SMTP server.
///
/// <para><b><see cref="Enabled"/> defaults to false</b>, and that is not timidity: the integration
/// suite, and any developer who has not started the <c>tools</c> compose profile, must not depend on
/// an SMTP server existing. Disabled, <c>SmtpEmailSender</c>'s counterpart logs what it would have
/// sent and reports success, so every path that sends mail stays exercised.</para>
///
/// <para>Enabled, the settings are validated <b>at startup</b> rather than at first send. A
/// misconfigured mail server that only reveals itself when somebody invites an administrator is a
/// defect discovered by a person waiting for an email — the same reasoning that makes a missing JWT
/// signing key a boot failure.</para>
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>
    /// When false, nothing is sent and the send is logged instead. The default, deliberately.
    /// </summary>
    public bool Enabled { get; init; }

    public SmtpOptions Smtp { get; init; } = new();

    public SenderOptions From { get; init; } = new();

    /// <summary>
    /// Where the links in those messages point — the console, not this API.
    ///
    /// <para>The backend does not guess its own frontend's address in any deployment that sends
    /// mail: wrong once and every activation link is wrong <em>and already delivered</em>, which no
    /// later fix reaches. Hence the startup validation, which requires an absolute URL whenever
    /// <see cref="Enabled"/> is true.</para>
    ///
    /// <para>The development default exists so link building never throws when email is
    /// <b>disabled</b> — the invitation still has to succeed, and the link it logs still has to be
    /// followable by a developer reading it out of the console. An earlier version defaulted this to
    /// empty and turned every invitation into a 500 the moment the URL was assembled.</para>
    /// </summary>
    public string ConsoleBaseUrl { get; init; } = "http://localhost:3000";

    /// <summary>How long an invitation link stays usable.</summary>
    /// <remarks>
    /// Long, because it is sent to somebody who is not expecting it and may be away for a few days;
    /// too short and every invitation becomes a support request. The account it protects has no
    /// password yet.
    /// </remarks>
    [Range(1, 336)]
    public int InvitationLifetimeHours { get; init; } = 72;

    /// <summary>How long a password-reset link stays usable.</summary>
    /// <remarks>
    /// Short, because the account already has a credential, so a live reset link is a second way in
    /// and should not outlive the reason it was created.
    /// </remarks>
    [Range(1, 72)]
    public int PasswordResetLifetimeHours { get; init; } = 1;
}

public sealed class SmtpOptions
{
    [Required]
    public string Host { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; init; } = 1025;

    /// <summary>
    /// STARTTLS when the server offers it. False only for a local capture like Mailpit, which speaks
    /// plain SMTP on purpose.
    /// </summary>
    public bool UseStartTls { get; init; }

    /// <summary>Blank for a local capture; required by every real provider.</summary>
    public string? Username { get; init; }

    public string? Password { get; init; }
}

public sealed class SenderOptions
{
    [Required]
    [EmailAddress]
    public string Address { get; init; } = string.Empty;

    [Required]
    public string DisplayName { get; init; } = string.Empty;
}
