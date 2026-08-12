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
    /// <summary>
    /// The console route that reads the token. Mirrored in GymOS-Platform's app router.
    ///
    /// <para><b>Relative, with no leading slash</b>, and that is load-bearing: a root-relative path
    /// resolves against the <em>authority</em> of the base URL and discards its path, so a console
    /// deployed under a sub-path — <c>https://apps.example.com/gymos-console</c> — silently produced
    /// links to <c>https://apps.example.com/set-password</c>. Startup validation could not catch it,
    /// because that URL is absolute and well-formed; the first symptom was an invited administrator
    /// on a 404, holding a link already delivered and beyond fixing.</para>
    /// </summary>
    private const string SetPasswordPath = "set-password";

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
        // The base is forced to end in a slash before the route is resolved against it. Without
        // that, .NET treats the last segment as a file name and replaces it, so
        // `https://host/console` + `set-password` gives `https://host/set-password` — the same
        // sub-path loss the leading slash used to cause, arriving by a different route.
        string root = _options.ConsoleBaseUrl.EndsWith('/')
            ? _options.ConsoleBaseUrl
            : _options.ConsoleBaseUrl + "/";

        // The token is base64url — URL-safe by construction — but it is escaped anyway rather than
        // interpolated: the encoding is a property of the generator, and a link that breaks the day
        // somebody changes it would break silently, in email, after delivery.
        var builder = new UriBuilder(new Uri(new Uri(root), SetPasswordPath))
        {
            Query = $"token={Uri.EscapeDataString(rawToken)}",
        };

        return builder.Uri.ToString();
    }
}
