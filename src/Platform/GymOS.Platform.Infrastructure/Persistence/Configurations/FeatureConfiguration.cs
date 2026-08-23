using GymOS.BuildingBlocks.Infrastructure.Persistence;
using GymOS.Platform.Domain.Features;
using GymOS.Platform.Domain.Features.Rules;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.Platform.Infrastructure.Persistence.Configurations;

public sealed class FeatureConfiguration : IEntityTypeConfiguration<Feature>
{
    public void Configure(EntityTypeBuilder<Feature> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("features", "platform");
        builder.HasKey(feature => feature.Id);

        builder.Property(feature => feature.Key)
            .HasMaxLength(FeatureKeyMustBeAnIdentifier.MaximumLength);
        builder.Property(feature => feature.Name)
            .HasMaxLength(FeatureNameIsRequired.MaximumLength);
        builder.Property(feature => feature.Description).HasMaxLength(1000);
        builder.Property(feature => feature.Category).HasMaxLength(60);

        // The key is how code and the API name a feature, so uniqueness is the database's promise
        // rather than the seeder's good behaviour.
        builder.HasIndex(feature => feature.Key)
            .IsUnique()
            .HasDatabaseName("ux_features_key");

        // The catalogue is read as "everything, grouped" — twelve rows today, so the index is for
        // stable ordering rather than for speed.
        builder.HasIndex(feature => new { feature.Category, feature.Name });

        builder.HasGymOSRowVersion();
    }
}

public sealed class GymFeatureOverrideConfiguration : IEntityTypeConfiguration<GymFeatureOverride>
{
    public void Configure(EntityTypeBuilder<GymFeatureOverride> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("gym_feature_overrides", "platform");
        builder.HasKey(over => over.Id);

        builder.Property(over => over.Reason)
            .HasMaxLength(OverrideReasonIsRequired.MaximumLength);

        // One exception per (gym, feature) — §2.4. The current row answers "what was decided"; the
        // audit trail keeps what was decided before it.
        builder.HasIndex(over => new { over.GymId, over.FeatureId })
            .IsUnique()
            .HasDatabaseName("ux_gym_feature_overrides_gym_feature");

        // Cascade: an override is meaningless without its gym, and unlike an invoice it is not a
        // record of money — nothing later needs to read the exceptions of a purged gym.
        builder.HasOne<Gym>()
            .WithMany()
            .HasForeignKey(over => over.GymId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict: features are never deleted, and if that ever changes the override rows must be
        // reviewed rather than silently vanish.
        builder.HasOne<Feature>()
            .WithMany()
            .HasForeignKey(over => over.FeatureId)
            .OnDelete(DeleteBehavior.Restrict);

        // Provenance only, nullable: losing the author must never lose the exception itself, the
        // same reasoning as a subscription's plan-price pointer.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(over => over.SetByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // The gym detail view reads every override of one gym.
        builder.HasIndex(over => over.GymId);

        builder.HasGymOSRowVersion();
    }
}
