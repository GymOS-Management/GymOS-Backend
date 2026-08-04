namespace GymOS.BuildingBlocks.Infrastructure.Auditing;

/// <summary>
/// One audit entry (AD-7, ARCHITECTURE.md §2.6). Append-only: written by the interceptor
/// and by explicit handler calls, read by the audit endpoint, never updated or deleted
/// through any API — which is why this type is immutable and carries no behavior.
///
/// The mechanism is a building block (every module is audited by the same interceptor)
/// while the frozen spec places the table in the <c>platform</c> schema; the mapping
/// honors the spec.
/// </summary>
public sealed class AuditLog
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary><see langword="null"/> for system-initiated work — seeding, jobs, event handlers.</summary>
    public Guid? UserId { get; init; }

    /// <summary>
    /// Denormalized on purpose: the log must still say who did this after the account is
    /// deleted. Keeping it is a legitimate-interest record, not a GDPR oversight — erasure
    /// of operational data does not erase the trail of administrative acts.
    /// </summary>
    public string? UserEmail { get; init; }

    public string? IpAddress { get; init; }

    public string? UserAgent { get; init; }

    /// <summary>Dotted, past tense: <c>user.suspended</c>, <c>role.permission_granted</c>.</summary>
    public required string Action { get; init; }

    public string? EntityType { get; init; }

    public Guid? EntityId { get; init; }

    /// <summary>State before the change, as jsonb. <see langword="null"/> on insert.</summary>
    public string? OldValues { get; init; }

    /// <summary>State after the change, as jsonb. <see langword="null"/> on delete.</summary>
    public string? NewValues { get; init; }

    /// <summary>Ties every entry produced by one request together (AD-38).</summary>
    public Guid? CorrelationId { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }
}
