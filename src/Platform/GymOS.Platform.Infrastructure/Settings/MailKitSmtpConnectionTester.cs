using System.Net.Sockets;
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Settings.Abstractions;
using GymOS.Platform.Infrastructure.Email;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace GymOS.Platform.Infrastructure.Settings;

/// <summary>
/// Probes the effective SMTP settings without sending mail (M7 spec §4, Task 8): connects,
/// authenticates when a username is configured, and issues a <c>NOOP</c> — the same round trips a
/// real send would open, none of the send itself. ADR-53 confines <see cref="IEmailSender"/> to
/// exactly two messages; this class never resolves that interface and never composes a
/// <c>MimeMessage</c>. It borrows <see cref="SmtpEmailSender"/>'s connect/authenticate shape because
/// the two must never disagree about how a connection is opened.
/// </summary>
internal sealed class MailKitSmtpConnectionTester : ISmtpConnectionTester
{
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(15);

    private readonly ISettings<EmailOptions> _settings;

    public MailKitSmtpConnectionTester(ISettings<EmailOptions> settings) => _settings = settings;

    public bool IsEmailEnabled => _settings.Current.Enabled;

    public async Task<SmtpConnectionTestResult> TestAsync(CancellationToken ct)
    {
        EmailOptions options = _settings.Current;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(OverallTimeout);
        using var client = new SmtpClient();
        string stage = "Connect";

        try
        {
            await client.ConnectAsync(
                options.Smtp.Host,
                options.Smtp.Port,
                options.Smtp.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
                timeout.Token);

            if (!string.IsNullOrWhiteSpace(options.Smtp.Username))
            {
                stage = "Authenticate";
                await client.AuthenticateAsync(
                    options.Smtp.Username, options.Smtp.Password ?? string.Empty, timeout.Token);
            }

            await client.NoOpAsync(timeout.Token);

            // MailKit does not expose the server's greeting banner past ConnectAsync, so the one
            // honest fact left to report about which server answered is what the handshake actually
            // negotiated: the capability set the server advertised on EHLO.
            string server = client.Capabilities.ToString();

            await client.DisconnectAsync(quit: true, timeout.Token);
            return new SmtpConnectionTestResult(true, null, server, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller's own token, not the 15-second timeout — rethrow so the request's normal
            // cancellation handling applies instead of this being reported as a test failure.
            throw;
        }
        catch (OperationCanceledException)
        {
            return new SmtpConnectionTestResult(false, stage, null, "Timed out after 15 seconds.");
        }
        catch (SslHandshakeException exception)
        {
            return new SmtpConnectionTestResult(false, "Tls", null, exception.Message);
        }
        catch (Exception exception) when (exception is SocketException or IOException
            or MailKit.ProtocolException or AuthenticationException or SmtpCommandException)
        {
            // SmtpCommandException is listed explicitly rather than folded into ProtocolException:
            // verified against MailKit 4.17 (the version pinned in Directory.Packages.props) that it
            // derives from MailKit.CommandException, not MailKit.ProtocolException, so it would
            // otherwise escape this handler uncaught.
            return new SmtpConnectionTestResult(false, stage, null, exception.Message);
        }
    }
}
