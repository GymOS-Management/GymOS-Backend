namespace GymOS.WebApi.Endpoints;

/// <summary>
/// Request bodies for the subscription endpoints (AD-12: API surface, since generated clients are built
/// from these).
/// </summary>
/// <param name="BillingCycle"><c>Monthly</c>, <c>Quarterly</c>, or <c>Yearly</c> — must be a cycle the plan prices.</param>
/// <param name="Currency">ISO 4217, and must be a currency the plan prices for that cycle.</param>
public sealed record AssignSubscriptionRequest(Guid PlanId, string BillingCycle, string Currency);

/// <param name="Immediately">
/// Default false: cancellation takes effect at the end of the paid period, so the gym keeps what it paid
/// for. True ends it now and forfeits the remainder.
/// </param>
public sealed record CancelSubscriptionRequest(string? Reason, bool Immediately = false);

/// <param name="PastDue">True records payment as late; false records it as received.</param>
public sealed record SetPaymentStateRequest(bool PastDue);
