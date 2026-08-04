namespace GymOS.BuildingBlocks.Infrastructure.Outbox;

/// <summary>
/// One captured domain event (AD-13/AD-14): envelope fields as queryable columns,
/// the typed payload as jsonb. Written by <see cref="OutboxSaveChangesInterceptor"/>
/// in the same transaction as the state change — no ghost events, no lost events.
/// Append-only; delivery state lives per handler in <see cref="OutboxDelivery"/>.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Unique per occurrence — the handler idempotency key (AD-39).</summary>
    public required Guid EventId { get; init; }

    /// <summary>Canonical dotted name, e.g. <c>gymos.platform.gym.suspended</c>.</summary>
    public required string EventName { get; init; }

    public required int EventVersion { get; init; }

    public required DateTimeOffset OccurredAtUtc { get; init; }

    /// <summary><see langword="null"/> for platform-level events (AD-14).</summary>
    public Guid? TenantId { get; init; }

    public required string AggregateType { get; init; }

    public required Guid AggregateId { get; init; }

    /// <summary>Acting user; <see langword="null"/> for system/job-initiated work.</summary>
    public Guid? UserId { get; init; }

    /// <summary>Ties the event to its originating request (AD-38 correlation spine).</summary>
    public Guid? CorrelationId { get; init; }

    /// <summary>Automation loop guard (AD-30): trigger depth + 1, 0 for user-initiated.</summary>
    public required int CausationDepth { get; init; }

    /// <summary>The concrete event record serialized as JSON (jsonb column).</summary>
    public required string Payload { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }
}
