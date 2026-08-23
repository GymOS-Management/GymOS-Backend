namespace GymOS.BuildingBlocks.Infrastructure.Outbox;

/// <summary>
/// Per-handler delivery state for one outbox message (AD-13): each subscribed handler
/// gets its own row, so one failing handler retries in isolation and never blocks the
/// others. Rows are claimed with <c>WITH (UPDLOCK, READPAST, ROWLOCK)</c> by the processor
/// (lands with the background-runner milestone).
/// </summary>
public sealed class OutboxDelivery
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required Guid MessageId { get; init; }

    /// <summary>Fully qualified handler type name — unique with <see cref="MessageId"/>.</summary>
    public required string HandlerName { get; init; }

    public OutboxDeliveryStatus Status { get; set; } = OutboxDeliveryStatus.Pending;

    public int Attempts { get; set; }

    public DateTimeOffset? NextRetryAtUtc { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? ProcessedAtUtc { get; set; }
}

/// <summary>Delivery lifecycle; Failed means dead-lettered (AD-46 unified semantics).</summary>
public enum OutboxDeliveryStatus
{
    Pending = 1,
    Processing = 2,
    Processed = 3,
    Failed = 4,
}
