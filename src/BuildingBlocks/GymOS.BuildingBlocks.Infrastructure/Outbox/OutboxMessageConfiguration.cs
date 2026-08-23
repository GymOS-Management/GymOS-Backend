using GymOS.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.BuildingBlocks.Infrastructure.Outbox;

/// <summary>
/// Maps the outbox tables into the dedicated <c>outbox</c> schema (building-block
/// infrastructure owns it — no module does). Monthly partitioning and the purge job
/// arrive with the outbox processor milestone.
/// </summary>
public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("messages", "outbox");
        builder.HasKey(message => message.Id);

        builder.Property(message => message.EventName).HasMaxLength(200);
        builder.Property(message => message.AggregateType).HasMaxLength(200);
        builder.Property(message => message.Payload).HasColumnType(JsonConventions.JsonColumnType);

        builder.HasIndex(message => message.EventId).IsUnique();
        builder.HasIndex(message => message.CreatedAtUtc);
        builder.HasIndex(message => message.EventName);
    }
}

public sealed class OutboxDeliveryConfiguration : IEntityTypeConfiguration<OutboxDelivery>
{
    public void Configure(EntityTypeBuilder<OutboxDelivery> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("deliveries", "outbox");
        builder.HasKey(delivery => delivery.Id);

        builder.Property(delivery => delivery.HandlerName).HasMaxLength(400);
        builder.Property(delivery => delivery.Status).HasConversion<short>();
        builder.Property(delivery => delivery.LastError).HasMaxLength(4000);

        builder.HasIndex(delivery => new { delivery.MessageId, delivery.HandlerName }).IsUnique();
        builder.HasIndex(delivery => new { delivery.Status, delivery.NextRetryAtUtc });

        builder.HasOne<OutboxMessage>()
            .WithMany()
            .HasForeignKey(delivery => delivery.MessageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
