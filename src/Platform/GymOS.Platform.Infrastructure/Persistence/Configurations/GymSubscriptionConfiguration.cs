using GymOS.BuildingBlocks.Infrastructure.Persistence;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.Platform.Infrastructure.Persistence.Configurations;

public sealed class GymSubscriptionConfiguration : IEntityTypeConfiguration<GymSubscription>
{
    public void Configure(EntityTypeBuilder<GymSubscription> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("gym_subscriptions", "platform");
        builder.HasKey(subscription => subscription.Id);

        builder.Property(subscription => subscription.Status).HasConversion<short>();
        builder.Property(subscription => subscription.BillingCycle).HasConversion<short>();
        builder.Property(subscription => subscription.CancellationReason).HasMaxLength(1000);

        // The agreed price, copied at assignment rather than followed by reference — see
        // GymSubscription.PlanPriceId for why an FK could not be the snapshot §2.3 asks for.
        builder.HasMoney(subscription => subscription.Price, "price");

        builder.HasOne<Gym>()
            .WithMany()
            .HasForeignKey(subscription => subscription.GymId)
            .OnDelete(DeleteBehavior.Restrict);

        // Restrict, not Cascade: a gym is soft-deleted, and its subscriptions are the record of what it
        // paid for. A cascade here would quietly erase billing history the moment a row was ever hard-
        // deleted by a purge job.
        builder.HasOne<SubscriptionPlan>()
            .WithMany()
            .HasForeignKey(subscription => subscription.PlanId)
            .OnDelete(DeleteBehavior.Restrict);

        // Provenance only, and nullable: the price row it names can be withdrawn from the plan.
        builder.HasOne<PlanPrice>()
            .WithMany()
            .HasForeignKey(subscription => subscription.PlanPriceId)
            .OnDelete(DeleteBehavior.SetNull);

        // One *live* subscription per gym (§2.3). A filtered unique index rather than a plain one,
        // because ended subscriptions accumulate: a gym that cancels and returns has several rows, and
        // only one of them may be live. The status values are the numeric ones — Trialing=1, Active=2,
        // PastDue=3 — because SQL cannot see the enum.
        builder.HasIndex(subscription => subscription.GymId)
            .IsUnique()
            .HasFilter("status IN (1, 2, 3)")
            .HasDatabaseName("ux_gym_subscriptions_one_live_per_gym");

        // The renewal job (M3.4) will sweep by period end over live rows; history reads by gym.
        builder.HasIndex(subscription => new { subscription.Status, subscription.CurrentPeriodEndUtc });
        builder.HasIndex(subscription => new { subscription.GymId, subscription.CreatedAtUtc });
        builder.HasIndex(subscription => subscription.PlanId);

        builder.Property<uint>("xmin").IsRowVersion();
    }
}
