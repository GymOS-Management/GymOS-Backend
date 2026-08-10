namespace GymOS.Platform.Application.Invoices.Abstractions;

/// <summary>
/// Hands out invoice numbers, <c>YYYY-NNNNNN</c>, sequential within each year (frozen §2.3: a DB
/// sequence per year).
///
/// <para><b>Gap-tolerant, deliberately.</b> The number is drawn inside the issuing transaction, and a
/// rollback after the draw burns it — the sequence does not give numbers back, because making it do so
/// would serialize every issuer behind a lock. Whether the ledger may carry gaps is a question for
/// accounting counsel, flagged in <c>docs/BusinessRules/Invoices.md</c>; the mechanism either way is
/// this interface.</para>
/// </summary>
public interface IInvoiceNumberGenerator
{
    /// <summary>The next number for the given year, formatted <c>YYYY-NNNNNN</c>.</summary>
    Task<string> NextAsync(int year, CancellationToken cancellationToken);
}
