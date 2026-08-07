namespace GymOS.BuildingBlocks.Infrastructure.Idempotency;

/// <summary>
/// One claimed (operation, key) — AD-39's record table. Written only by IdempotencyStore;
/// deliberately absent from PlatformAuditedEntities (bookkeeping, not business state).
/// </summary>
public sealed class IdempotencyRecord
{
    public Guid Id { get; set; }

    /// <summary><see langword="null"/> until tenant-scoped consumers exist.</summary>
    public Guid? TenantId { get; set; }

    public string OperationKey { get; set; } = string.Empty;

    public string IdempotencyKey { get; set; } = string.Empty;

    public string RequestHash { get; set; } = string.Empty;

    /// <summary>
    /// Fencing token identifying the current claim holder. Set on claim, replaced on takeover;
    /// MarkFinished/ReleaseClaim are gated on it so a stale holder can never overwrite or delete
    /// the live owner's record.
    /// </summary>
    public Guid ClaimToken { get; set; }

    /// <summary><see cref="GymOS.BuildingBlocks.Application.Idempotency.IdempotencyRecordStatus"/> values.</summary>
    public int Status { get; set; }

    /// <summary>0 = success; otherwise (int)ErrorKind + 1 (see StoredOutcomeCodec).</summary>
    public int ResponseStatus { get; set; }

    /// <summary>jsonb: success DTO or serialized Error.</summary>
    public string? ResponseBody { get; set; }

    public DateTimeOffset LeaseExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}
