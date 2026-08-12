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
    /// Marks the redundant overrides for removal, judging them against
    /// <paramref name="livePlanIdAfterChange"/> — the plan that will be live for this gym
    /// <b>once the caller's transaction commits</b>, or <see langword="null"/> when the gym will
    /// have no live subscription at all.
    ///
    /// <para>The caller passes this rather than a gym id alone <b>on purpose</b>: every caller has
    /// just mutated the subscription in memory (assigned it, changed its plan, or ended it) and
    /// already knows the answer. Re-deriving it here by querying <c>ISubscriptionRepository</c>
    /// would ask the database a question about a row the caller has not saved yet — invisible for a
    /// brand-new assignment (an unsaved <c>Add</c>), and wrong for an ending subscription (the query
    /// still matches the not-yet-saved, still-live row and hands back the plan that is ending, not
    /// "no live plan"). This is a fixed defect, not a hypothetical one — see the M4.1 task 2 review.
    /// </para>
    ///
    /// <para><b>Does not save</b> — the caller's transaction does, so the withdrawal and the change
    /// that caused it commit together or not at all (the <c>InvoiceIssuer</c> precedent).</para>
    /// </summary>
    Task<int> WithdrawRedundantAsync(
        Guid gymId, Guid? livePlanIdAfterChange, CancellationToken cancellationToken);
}
