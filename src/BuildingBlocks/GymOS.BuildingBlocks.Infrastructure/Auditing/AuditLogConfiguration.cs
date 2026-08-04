using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.BuildingBlocks.Infrastructure.Auditing;

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("audit_logs", "platform");

        // Composite key, ordered id-first — not a modelling preference but a PostgreSQL
        // requirement: the table is range-partitioned by month on created_at_utc
        // (ARCHITECTURE.md §2.6), and every unique constraint on a partitioned table must
        // contain the partition key. Id leads so the per-partition index still serves
        // lookup-by-id, which is what `GET /audit-logs/{id}` does.
        //
        // Ids are Guids, so id alone remains unique in practice; created_at_utc is in the key
        // for the storage engine's benefit, not to widen identity.
        builder.HasKey(log => new { log.Id, log.CreatedAtUtc });

        builder.Property(log => log.UserEmail).HasMaxLength(320);
        builder.Property(log => log.IpAddress).HasMaxLength(45); // IPv6 max
        builder.Property(log => log.UserAgent).HasMaxLength(512);
        builder.Property(log => log.Action).HasMaxLength(100);
        builder.Property(log => log.EntityType).HasMaxLength(100);

        builder.Property(log => log.OldValues).HasColumnType("jsonb");
        builder.Property(log => log.NewValues).HasColumnType("jsonb");

        // No FK to users: the log outlives the accounts it mentions, which is the whole
        // point of denormalizing the email. A cascade here would erase history.
        builder.HasIndex(log => log.CreatedAtUtc).IsDescending();
        builder.HasIndex(log => new { log.EntityType, log.EntityId });
        builder.HasIndex(log => log.UserId);
        builder.HasIndex(log => log.Action);
    }
}
