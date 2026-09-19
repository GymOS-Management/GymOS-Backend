using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Settings.Abstractions;
using GymOS.Platform.Infrastructure.Email;

namespace GymOS.Platform.Infrastructure.Settings;

/// <summary>
/// Picks the sender per call from the live Email settings (M7 spec §4): SMTP when enabled, the logging
/// sender otherwise. Replaces the composition-time choice that read Email:Enabled once at start-up.
/// </summary>
internal sealed class SwitchingEmailSender : IEmailSender
{
    private readonly ISettings<EmailOptions> _settings;
    private readonly SmtpEmailSender _smtp;
    private readonly LoggingEmailSender _logging;

    public SwitchingEmailSender(ISettings<EmailOptions> settings, SmtpEmailSender smtp, LoggingEmailSender logging)
    {
        _settings = settings; _smtp = smtp; _logging = logging;
    }

    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken) =>
        _settings.Current.Enabled ? _smtp.SendAsync(message, cancellationToken) : _logging.SendAsync(message, cancellationToken);
}
