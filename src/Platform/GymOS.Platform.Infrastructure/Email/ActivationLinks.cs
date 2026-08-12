using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;
using Microsoft.Extensions.Options;

namespace GymOS.Platform.Infrastructure.Email;

/// <summary>
/// Builds the console link for an activation token and sends the message that carries it.
///
/// <para>The link points at the <b>console</b>, never at this API: the person following it needs a
/// password form, which is a frontend. <c>Email:ConsoleBaseUrl</c> supplies the address and is
/// validated at startup — wrong once, and every link in every invitation is wrong <em>and already
/// delivered</em>, which no later fix reaches.</para>
/// </summary>
internal sealed class ActivationLinks : IActivationLinks
{
    /// <summary>The console route that reads the token. Mirrored in GymOS-Platform's app router.</summary>
    private const string SetPasswordPath = "/set-password";

    private readonly EmailOptions _options;
    private readonly IEmailSender _sender;

    public ActivationLinks(IOptions<EmailOptions> options, IEmailSender sender)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _sender = sender;
    }

    public TimeSpan LifetimeFor(ActivationPurpose purpose) => purpose switch
    {
        ActivationPurpose.Invitation => TimeSpan.FromHours(_options.InvitationLifetimeHours),
        ActivationPurpose.PasswordReset => TimeSpan.FromHours(_options.PasswordResetLifetimeHours),
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown activation purpose."),
    };

    public Task<EmailSendResult> SendAsync(
        ActivationPurpose purpose,
        ActivationRecipient recipient,
        string rawToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);

        string url = BuildUrl(rawToken);
        int hours = (int)LifetimeFor(purpose).TotalHours;

        EmailMessage message = purpose switch
        {
            ActivationPurpose.Invitation =>
                EmailTemplates.Invitation(recipient.Email, recipient.FirstName, url, hours),
            ActivationPurpose.PasswordReset =>
                EmailTemplates.PasswordReset(recipient.Email, recipient.FirstName, url, hours),
            _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown activation purpose."),
        };

        return _sender.SendAsync(message, cancellationToken);
    }

    private string BuildUrl(string rawToken)
    {
        // The token is base64url — URL-safe by construction — but it is escaped anyway rather than
        // interpolated: the encoding is a property of the generator, and a link that breaks the day
        // somebody changes it would break silently, in email, after delivery.
        var builder = new UriBuilder(new Uri(new Uri(_options.ConsoleBaseUrl), SetPasswordPath))
        {
            Query = $"token={Uri.EscapeDataString(rawToken)}",
        };

        return builder.Uri.ToString();
    }
}
