namespace GymOS.BuildingBlocks.Application.Idempotency;

/// <summary>
/// Declares a command idempotent (AD-39). The pipeline claims the caller's
/// <c>Idempotency-Key</c> before the handler runs and replays the stored outcome on retry.
/// </summary>
public interface IIdempotentCommand
{
    /// <summary>Stable operation identifier, e.g. <c>billing.mark-invoice-paid</c>.</summary>
    string OperationKey { get; }

    /// <summary>How long a completed record replays; financial operations declare 72h+.</summary>
    TimeSpan RetentionPeriod { get; }

    /// <summary>True → a request without an <c>Idempotency-Key</c> header is rejected 400.</summary>
    bool KeyRequired { get; }
}
