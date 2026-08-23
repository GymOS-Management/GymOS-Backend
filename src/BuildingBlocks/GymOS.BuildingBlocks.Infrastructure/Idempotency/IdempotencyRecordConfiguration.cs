using GymOS.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.BuildingBlocks.Infrastructure.Idempotency;

/// <summary>
/// Maps platform.idempotency_records. The unique claim index is NOT declared here: it needs
/// COALESCE over the nullable tenant_id (two NULLs must conflict), which EF cannot express —
/// it lives as raw SQL in the AddIdempotencyRecords migration, same technique and reason as
/// ix_plan_prices_plan_cycle_currency.
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
