using GymOS.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.BuildingBlocks.Infrastructure.Auditing;

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("audit_logs", "platform");

        // Composite key, ordered id-first — not a modelling preference but a requirement of the
        // storage engine: the table is range-partitioned by month on created_at_utc
        // (ARCHITECTURE.md §2.6), and every unique index on a partitioned table must contain the
        // partitioning column. Id leads so the index still serves lookup-by-id, which is what
        // `GET /audit-logs/{id}` does.
        //
        // The rule is the same on SQL Server as it was on PostgreSQL, so this key survived the
        // migration untouched; only the DDL that partitions the table changed (a partition
        // function and scheme in RuntimeDatabaseProgrammability, rather than PARTITION BY RANGE).
        //
        // Ids are Guids, so id alone remains unique in practice; created_at_utc is in the key
        // for the storage engine's benefit, not to widen identity.
        builder.HasKey(log => new { log.Id, log.CreatedAtUtc });

        builder.Property(log => log.UserEmail).HasMaxLength(320);
        builder.Property(log => log.IpAddress).HasMaxLength(45); // IPv6 max
        builder.Property(log => log.UserAgent).HasMaxLength(512);
        builder.Property(log => log.Action).HasMaxLength(100);
        builder.Property(log => log.EntityType).HasMaxLength(100);

        builder.Property(log => log.OldValues).HasColumnType(JsonConventions.JsonColumnType);
        builder.Property(log => log.NewValues).HasColumnType(JsonConventions.JsonColumnType);

        // No FK to users: the log outlives the accounts it mentions, which is the whole
        // point of denormalizing the email. A cascade here would erase history.
        builder.HasIndex(log => log.CreatedAtUtc).IsDescending();
        builder.HasIndex(log => new { log.EntityType, log.EntityId });
        builder.HasIndex(log => log.UserId);
        builder.HasIndex(log => log.Action);
    }
}
