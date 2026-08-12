using GymOS.BuildingBlocks.Application.Abstractions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GymOS.Platform.Infrastructure.Email;

/// <summary>
/// The SMTP transport for ADR-53's two messages.
///
/// <para>MailKit rather than <c>System.Net.Mail.SmtpClient</c>, which Microsoft documents as obsolete
/// for new development. SMTP rather than a vendor SDK because it is what the architecture itself
/// names as the integration key, and because one code path then serves the Mailpit container in
/// <c>docker-compose</c> and any production provider — Azure Communication Services included.</para>
///
/// <para>This class is the <b>only</b> place a MIME or SMTP type appears outside the host: R8 confines
/// provider SDKs to Infrastructure, so the two producers depend on <see cref="IEmailSender"/> and
/// never learn how the mail leaves.</para>
/// </summary>
internal sealed partial class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent '{Subject}' to {Recipient}.")]
    private static partial void LogSent(ILogger logger, string subject, string recipient);

    [LoggerMessage(Level = LogLevel.Error, Message =
        "Could not send '{Subject}' to {Recipient}. The action that triggered it is unaffected; " +
        "re-send it once the mail server is reachable.")]
    private static partial void LogFailed(
        ILogger logger, string subject, string recipient, Exception exception);

    public SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _logger = logger;
    }

    public async Task<EmailSendResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var mime = new MimeMessage
        {
            Subject = message.Subject,
            Body = new BodyBuilder
            {
                HtmlBody = message.HtmlBody,
                TextBody = message.TextBody,
            }.ToMessageBody(),
        };

        mime.From.Add(new MailboxAddress(_options.From.DisplayName, _options.From.Address));
        mime.To.Add(MailboxAddress.Parse(message.To));

        try
        {
            using var client = new SmtpClient();

            await client.ConnectAsync(
                _options.Smtp.Host,
                _options.Smtp.Port,
                _options.Smtp.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(_options.Smtp.Username))
            {
                // An empty password is passed as empty rather than refused: some relays authenticate
                // on the username alone, and Mailpit accepts anything.
                await client.AuthenticateAsync(
                    _options.Smtp.Username, _options.Smtp.Password ?? string.Empty, cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);

            LogSent(_logger, message.Subject, message.To);

            return EmailSendResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Caught broadly on purpose: every failure here — unreachable host, refused credentials,
            // a rejected recipient — means the same thing to the caller, which is that the account
            // work succeeded and the mail did not. Letting it escape would roll back an invitation
            // because a mail server was down.
            LogFailed(_logger, message.Subject, message.To, exception);

            return EmailSendResult.Failed(exception.Message);
        }
    }
}

/// <summary>
/// What runs when <c>Email:Enabled</c> is false: logs the message and reports success.
///
/// <para>Success, not failure, because "email is switched off" is a deployment choice rather than a
/// fault — a developer without the Mailpit profile running should be able to invite an administrator
/// and read the link out of the log, and the integration suite should not need an SMTP server to
/// exercise the paths that send.</para>
///
/// <para>The subject and recipient are logged; <b>the body is not</b>. Both messages carry a one-time
/// credential in their link, and a log that records it is a second copy of it.</para>
/// </summary>
internal sealed partial class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;

    [LoggerMessage(Level = LogLevel.Information, Message =
        "Email is disabled (Email:Enabled=false); '{Subject}' for {Recipient} was not sent.")]
    private static partial void LogSuppressed(ILogger logger, string subject, string recipient);

    public LoggingEmailSender(ILogger<LoggingEmailSender> logger) => _logger = logger;

    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        LogSuppressed(_logger, message.Subject, message.To);

        return Task.FromResult(EmailSendResult.Success);
    }
}
