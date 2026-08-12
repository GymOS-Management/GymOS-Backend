using System.Net;
using GymOS.BuildingBlocks.Application.Abstractions;

namespace GymOS.Platform.Infrastructure.Email;

/// <summary>
/// The two messages ADR-53 permits, rendered.
///
/// <para><b>Code, not a template engine and not a table.</b> That is the ADR's boundary rather than
/// laziness: templates with per-tenant overrides and versioning are what
/// <c>GymOS.Modules.Notifications</c> is for, and two fixed messages sent to the platform's own
/// administrators have no tenant to override them. A third message means revisiting the ADR, which is
/// also why this class has no general <c>Render(key, model)</c> shape to grow into one quietly.</para>
///
/// <para>Every interpolated value is HTML-encoded. A display name comes from whoever typed the
/// invitation, and the link carries a one-time credential — neither is trusted into markup raw.</para>
/// </summary>
internal static class EmailTemplates
{
    public static EmailMessage Invitation(
        string recipient,
        string firstName,
        string activationUrl,
        int lifetimeHours)
    {
        string name = WebUtility.HtmlEncode(firstName);
        string url = WebUtility.HtmlEncode(activationUrl);
        string window = Describe(lifetimeHours);

        string html = $"""
            <p>Hello {name},</p>
            <p>An administrator account has been created for you on the GymOS platform console.
            Choose a password to activate it:</p>
            <p><a href="{url}">Set your password</a></p>
            <p>This link works once and expires in {window}. If it has expired, ask whoever invited
            you to send another.</p>
            <p>If you were not expecting this, you can ignore it — the account cannot be used until
            somebody sets a password on it.</p>
            """;

        // The URL is repeated in full rather than hidden behind link text: a plain-text reader has no
        // link to click, and a suspicious reader wants to see where it goes before following it.
        string text = $"""
            Hello {firstName},

            An administrator account has been created for you on the GymOS platform console.
            Choose a password to activate it:

            {activationUrl}

            This link works once and expires in {window}. If it has expired, ask whoever invited you
            to send another.

            If you were not expecting this, you can ignore it — the account cannot be used until
            somebody sets a password on it.
            """;

        return new EmailMessage(recipient, "Activate your GymOS administrator account", html, text);
    }

    public static EmailMessage PasswordReset(
        string recipient,
        string firstName,
        string resetUrl,
        int lifetimeHours)
    {
        string name = WebUtility.HtmlEncode(firstName);
        string url = WebUtility.HtmlEncode(resetUrl);
        string window = Describe(lifetimeHours);

        string html = $"""
            <p>Hello {name},</p>
            <p>A password reset was requested for your GymOS administrator account:</p>
            <p><a href="{url}">Set a new password</a></p>
            <p>This link works once and expires in {window}.</p>
            <p><strong>If you did not expect this, tell your platform administrator.</strong> Your
            current password still works and nothing has changed yet — but somebody with access to
            the console asked for this link.</p>
            """;

        string text = $"""
            Hello {firstName},

            A password reset was requested for your GymOS administrator account:

            {resetUrl}

            This link works once and expires in {window}.

            If you did not expect this, tell your platform administrator. Your current password still
            works and nothing has changed yet — but somebody with access to the console asked for
            this link.
            """;

        return new EmailMessage(recipient, "Reset your GymOS administrator password", html, text);
    }

    /// <summary>"1 hour", "3 days", "72 hours" — whichever a person would say.</summary>
    private static string Describe(int hours) => hours switch
    {
        1 => "1 hour",
        < 24 => $"{hours} hours",
        24 => "1 day",
        _ when hours % 24 == 0 => $"{hours / 24} days",
        _ => $"{hours} hours",
    };
}
