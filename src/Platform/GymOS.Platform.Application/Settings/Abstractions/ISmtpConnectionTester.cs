namespace GymOS.Platform.Application.Settings.Abstractions;

/// <summary>Probes the effective SMTP settings without sending mail (M7 spec §4), reporting how far
/// the attempt got — connect, TLS negotiation, or authentication. Implemented in Task 8.</summary>
public interface ISmtpConnectionTester
{
    Task<SmtpConnectionTestResult> TestAsync(CancellationToken ct);
}
