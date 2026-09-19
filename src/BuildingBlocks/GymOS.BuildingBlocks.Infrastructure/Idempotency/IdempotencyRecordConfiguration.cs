using GymOS.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.BuildingBlocks.Infrastructure.Idempotency;

/// <summary>
/// Maps platform.idempotency_records. The unique claim index is declared in raw SQL in
/// <c>20260823193131_RuntimeDatabaseProgrammability</c> (see the comment at its
/// <c>CREATE UNIQUE INDEX</c>), not here: EF can express it, but SQL Server treats NULLs as equal in
/// a unique index, so a plain index over the nullable tenant_id already makes two platform-scope
/// claims with one key collide — the sentinel the PostgreSQL version needed is gone.
/// </summary>
internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("idempotency_records", "platform");
        builder.HasKey(record => record.Id);
        builder.Property(record => record.OperationKey).HasMaxLength(100).IsRequired();
        builder.Property(record => record.IdempotencyKey).HasMaxLength(200).IsRequired();
        builder.Property(record => record.RequestHash).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(record => record.ClaimToken).IsRequired();
        builder.Property(record => record.ResponseBody).HasColumnType(JsonConventions.JsonColumnType);
        builder.HasIndex(record => record.ExpiresAt).HasDatabaseName("ix_idempotency_records_expires_at");
    }
}
