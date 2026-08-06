using GymOS.SharedKernel.Results;

namespace GymOS.BuildingBlocks.Application.Idempotency;

/// <summary>The three protocol-level rejections of AD-39's decision table, with stable codes.</summary>
public static class IdempotencyErrors
{
    /// <summary>400 — the command declares <c>KeyRequired</c> and no header was sent.</summary>
    public static Error KeyRequired { get; } = Error.Validation(
        "idempotency.key_required",
        "This operation requires an Idempotency-Key header so a retry cannot execute twice.");

    /// <summary>422 (via host override table) — same key, different request body: always a client bug.</summary>
    public static Error KeyReuseMismatch { get; } = Error.Conflict(
        "idempotency.key_reuse_mismatch",
        "This Idempotency-Key was already used for a different request. Use a new key for a new request.");

    /// <summary>409 + Retry-After — a concurrent duplicate holds a live claim; never run two.</summary>
    public static Error OperationInProgress { get; } = Error.Conflict(
        "idempotency.operation_in_progress",
        "An identical request is already in flight. Retry after the current attempt finishes.");
}
