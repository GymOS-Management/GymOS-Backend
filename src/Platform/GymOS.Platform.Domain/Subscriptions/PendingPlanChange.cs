namespace GymOS.Platform.Domain.Subscriptions;

/// <summary>
/// A plan change agreed now and applied at the end of the paid period — what a downgrade becomes.
///
/// <para><b>Why a downgrade waits.</b> The gym has paid for this period on the current terms, so
/// moving it to a cheaper plan today would take away service it already bought and leave the platform
/// owing the difference. The alternatives are worse: a negative invoice needs a credit-note document
/// that does not exist, and a carried balance needs a ledger that does not either. Deferring the
/// change to the moment the money runs out needs neither.</para>
///
/// <para><b>The terms are copied, not referenced</b> — the same reasoning as
/// <see cref="GymSubscription.PlanPriceId"/>. A pointer to a plan price would silently reprice the
/// scheduled change if the catalogue moved between the request and the renewal, which is exactly the
/// surprise the snapshot exists to prevent.</para>
///
/// <para>Rule R4 permits the naked <see cref="decimal"/> here because this is a record carrying an
/// amount beside the currency it is denominated in. It cannot be a <c>Money</c>: EF has no nullable
/// complex-type mapping, and this whole value is optional.</para>
/// </summary>
/// <param name="PlanPriceId">Provenance only, and nullable — see <see cref="GymSubscription.PlanPriceId"/>.</param>
/// <param name="ChangeAtUtc">
/// When the renewal job should apply it: the end of the current period. Stored rather than derived so
/// the job reads one column instead of re-deciding a policy the gym was already told about.
/// </param>
public sealed record PendingPlanChange(
    Guid PlanId,
    Guid? PlanPriceId,
    BillingCycle BillingCycle,
    decimal Amount,
    string CurrencyCode,
    DateTimeOffset ChangeAtUtc);
