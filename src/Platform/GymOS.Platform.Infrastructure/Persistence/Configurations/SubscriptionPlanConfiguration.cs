using GymOS.BuildingBlocks.Infrastructure.Persistence;
using GymOS.Platform.Domain.Features;
using GymOS.Platform.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.Platform.Infrastructure.Persistence.Configurations;

public sealed class SubscriptionPlanConfiguration : IEntityTypeConfiguration<SubscriptionPlan>
{
    public void Configure(EntityTypeBuilder<SubscriptionPlan> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("subscription_plans", "platform", table =>
            // The domain upper-cases the code; the CHECK keeps it that way for anything reaching the
            // table another way, so the unique index keeps meaning "one plan per code".
            table.HasCheckConstraint("ck_subscription_plans_code_upper", "code = upper(code)"));

        builder.HasKey(plan => plan.Id);

        builder.Property(plan => plan.Code).HasMaxLength(40);
        builder.Property(plan => plan.Name).HasMaxLength(120);
        builder.Property(plan => plan.Description).HasMaxLength(2000);

        builder.HasIndex(plan => plan.Code).IsUnique();

        // The catalogue is read as "sellable plans in display order", which is this index exactly.
        builder.HasIndex(plan => new { plan.IsActive, plan.SortOrder });

        builder.HasMany(plan => plan.Prices)
            .WithOne()
            .HasForeignKey(price => price.PlanId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(plan => plan.Prices)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // What the plan includes (§2.4), reached through the plan exactly as its prices are.
        builder.HasMany(plan => plan.Features)
            .WithOne()
            .HasForeignKey(feature => feature.PlanId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(plan => plan.Features)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasGymOSRowVersion();
    }
}

public sealed class PlanFeatureConfiguration : IEntityTypeConfiguration<PlanFeature>
{
    public void Configure(EntityTypeBuilder<PlanFeature> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("plan_features", "platform");
        builder.HasKey(feature => feature.Id);

        // One opinion per (plan, feature) — the aggregate upserts, and this is what holds under a
        // race between two operators editing the same plan.
        builder.HasIndex(feature => new { feature.PlanId, feature.FeatureId })
            .IsUnique()
            .HasDatabaseName("ux_plan_features_plan_feature");

        // Restrict: features are never deleted; if that changes, plan contents must be reviewed.
        builder.HasOne<Feature>()
            .WithMany()
            .HasForeignKey(feature => feature.FeatureId)
            .OnDelete(DeleteBehavior.Restrict);

        // No row version: written only through the SubscriptionPlan aggregate, whose token guards it —
        // the PlanPrice precedent, recorded in ConcurrencyModelTests' exemption list.
    }
}

public sealed class PlanPriceConfiguration : IEntityTypeConfiguration<PlanPrice>
{
    public void Configure(EntityTypeBuilder<PlanPrice> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("plan_prices", "platform");
        builder.HasKey(price => price.Id);

        builder.Property(price => price.BillingCycle).HasConversion<short>();

        // Money's first persisted use (AD-42): price_amount numeric(14,4) + price_currency varchar(3).
        // §2.3 specifies numeric(12,2) for this column; the shared convention's 14,4 is wider on both
        // axes, so every value the spec allows still fits — and one money shape across the platform
        // beats a per-table decision about precision.
        builder.HasMoney(price => price.Price, "price");

        // Lookup by plan and cycle, which is how a price is found when assigning a subscription.
        builder.HasIndex(price => new { price.PlanId, price.BillingCycle });

        // The *unique* index — (plan, cycle, currency) per §2.3 — is created as raw SQL in the
        // AddSubscriptionPlans migration, because `currency` lives inside the Money complex property and
        // EF cannot build an index across one ("no corresponding CLR property or field"). The aggregate
        // enforces the same rule in code, so a caller still gets a typed conflict rather than a database
        // error; the index is what holds under a race. Isolated and documented, per the house rule for
        // raw SQL in migrations.
    }
}
