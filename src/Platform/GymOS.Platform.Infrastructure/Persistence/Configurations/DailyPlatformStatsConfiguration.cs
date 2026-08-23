using GymOS.BuildingBlocks.Infrastructure.Persistence;
using GymOS.Platform.Domain.Analytics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.Platform.Infrastructure.Persistence.Configurations;

public sealed class DailyPlatformStatsConfiguration : IEntityTypeConfiguration<DailyPlatformStats>
{
    public void Configure(EntityTypeBuilder<DailyPlatformStats> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("daily_platform_stats", "platform");
        builder.HasKey(stats => stats.Id);

        // One row per day, and the write-once rule's last line of defence: a second insert for a
        // date already recorded is refused by the database, not merely skipped by the job.
        builder.HasIndex(stats => stats.StatDate)
            .IsUnique()
            .HasDatabaseName("ux_daily_platform_stats_date");

        builder.Metadata
            .FindNavigation(nameof(DailyPlatformStats.Mrr))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(stats => stats.Mrr)
            .WithOne()
            .HasForeignKey(mrr => mrr.StatsId)
            .OnDelete(DeleteBehavior.Cascade);

        // No row version: one writer, serialised by IJobLock, and rows are never updated. See the aggregate.
    }
}

public sealed class PlatformMrrConfiguration : IEntityTypeConfiguration<PlatformMrr>
{
    public void Configure(EntityTypeBuilder<PlatformMrr> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("daily_platform_stats_mrr", "platform");
        builder.HasKey(mrr => mrr.Id);

        builder.HasMoney(mrr => mrr.Amount, "amount");

        // The (stats, currency) uniqueness lives in the migration as raw SQL: EF cannot index into a
        // complex property ("no corresponding CLR property or field"), the same limitation
        // AddSubscriptionPlans documented for (plan, cycle, currency). The aggregate refuses a
        // duplicate currency in code, so a caller gets a typed error; the index holds under a bug.
    }
}
