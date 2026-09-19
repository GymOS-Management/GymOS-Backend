namespace GymOS.Platform.Application.Settings.Abstractions;

/// <summary>Probes the effective SMTP settings without sending mail (M7 spec §4), reporting how far
/// the attempt got — connect, TLS negotiation, or authentication. Implemented in Task 8.</summary>
public interface ISmtpConnectionTester
{
    /// <summary>
    /// Whether <c>Email:Enabled</c> is on right now. <c>GymOS.Platform.Infrastructure.Email.EmailOptions</c>
    /// carries the rest of the SMTP shape, but Application never depends on Infrastructure (rule R7),
    /// so this one fact — the fact <c>TestEmailConnectionCommand</c> in <c>SettingsCommands.cs</c>
    /// needs before it decides whether to call <see cref="TestAsync"/> at all — is the only part of
    /// it exposed across that boundary.
    /// </summary>
    bool IsEmailEnabled { get; }

    Task<SmtpConnectionTestResult> TestAsync(CancellationToken ct);
}
