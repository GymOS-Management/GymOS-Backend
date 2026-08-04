namespace GymOS.SharedKernel.Events;

/// <summary>
/// Base type for all domain events (AD-13/AD-14). A concrete event carries its
/// business payload as typed properties plus the three envelope fields the domain
/// itself owns: identity, canonical name, and schema version.
///
/// The remaining envelope fields (aggregate id/type, occurred-at, tenant, user,
/// correlation id, causation depth) are ambient context: the outbox writer captures
/// them at save time from the aggregate being persisted, <c>IClock</c>, and the
/// current request context. Keeping them off this record keeps aggregates free of
/// infrastructure concerns and preserves time-as-input replayability (AD-40).
/// </summary>
public abstract record DomainEvent
{
    /// <summary>Unique per occurrence; the idempotency key for event handlers (AD-39).</summary>
    public Guid EventId { get; } = Guid.NewGuid();

    /// <summary>
    /// Canonical dotted name, past tense: <c>gymos.&lt;module&gt;.&lt;aggregate&gt;.&lt;action&gt;</c>
    /// (e.g. <c>gymos.platform.gym.suspended</c>). Stable API — renames are versioned
    /// migrations, never silent changes (AD-14).
    /// </summary>
    public abstract string EventName { get; }

    /// <summary>
    /// Payload schema version, starting at 1. Additive changes keep the version;
    /// breaking changes increment it (AD-14).
    /// </summary>
    public virtual int EventVersion => 1;
}
