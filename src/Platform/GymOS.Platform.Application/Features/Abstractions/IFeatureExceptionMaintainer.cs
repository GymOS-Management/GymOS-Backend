namespace GymOS.Platform.Application.Features.Abstractions;

/// <summary>
/// Withdraws a gym's exceptions that no longer differ from what it would get anyway.
///
/// <para>Called by every path that changes one gym's subscription, and by the nightly sweep for the
/// platform-wide triggers. Built as "re-evaluate this gym's exceptions" rather than "delete redundant
/// rows" because override <b>expiry</b>, when its product decision is taken, is one more predicate in
/// the same pass.</para>
/// </summary>
public interface IFeatureExceptionMaintainer
{
    /// <summary>
    /// Marks the redundant overrides for removal. <b>Does not save</b> — the caller's transaction
    /// does, so the withdrawal and the change that caused it commit together or not at all (the
    /// <c>InvoiceIssuer</c> precedent).
    /// </summary>
    Task<int> WithdrawRedundantAsync(Guid gymId, CancellationToken cancellationToken);
}
