namespace GymOS.WebApi.Endpoints;

/// <summary>
/// Body of mark-paid. Only the reference travels in the body — the idempotency key and the optional
/// precondition are headers (<c>Idempotency-Key</c>, <c>If-Match</c>), like every other write.
/// </summary>
public sealed record MarkInvoicePaidRequest(string? PaymentReference);
