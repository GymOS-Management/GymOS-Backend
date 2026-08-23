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

        // A scheduled plan change, in the subscription's own row. An owned type rather than the
        // complex property Price uses, because the whole value is optional and EF has no nullable
        // complex-type mapping — the price columns are never null, these five always are together.
        //
        // The amount is a plain decimal beside its currency code rather than a Money for the same
        // reason (rule R4's serialisation-boundary case); MoneyConventions' column type is reused so
        // a pending price and a live one cannot disagree about precision.
        builder.OwnsOne(subscription => subscription.PendingChange, pending =>
        {
            pending.Property(change => change.PlanId).HasColumnName("pending_plan_id");
            pending.Property(change => change.PlanPriceId).HasColumnName("pending_plan_price_id");
            pending.Property(change => change.BillingCycle)
                .HasColumnName("pending_billing_cycle")
                .HasConversion<short>();
            pending.Property(change => change.Amount)
                .HasColumnName("pending_price_amount")
                .HasColumnType(MoneyConventions.AmountColumnType);
            pending.Property(change => change.CurrencyCode)
                .HasColumnName("pending_price_currency")
                .HasMaxLength(MoneyConventions.CurrencyCodeLength);
            pending.Property(change => change.ChangeAtUtc).HasColumnName("pending_change_at_utc");
        });

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

        // The nightly churn counts filter on these two dates, and neither was indexed:
        // `canceled_at_utc` appeared in no index at all. Both buckets are terminal and their rows are
        // never deleted, so they grow faster than any other slice of this table while the numbers they
        // answer stay near zero — the job ends up reading every cancellation in the platform's history
        // to count one day's. Partial, because only the matching status is ever asked about.
        //
        // Literal status values, like the one-live-per-gym filter above, because SQL cannot see the
        // enum: Canceled = 4, Expired = 5.
        builder.HasIndex(subscription => subscription.CanceledAtUtc)
            .HasFilter("status = 4")
            .HasDatabaseName("ix_gym_subscriptions_canceled_at_utc");

        builder.HasIndex(subscription => subscription.ExpiredAtUtc)
            .HasFilter("status = 5")
            .HasDatabaseName("ix_gym_subscriptions_expired_at_utc");

        builder.HasGymOSRowVersion();
    }
}
