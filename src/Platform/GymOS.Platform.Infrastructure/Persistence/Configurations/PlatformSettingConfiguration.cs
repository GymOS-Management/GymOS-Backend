using GymOS.BuildingBlocks.Infrastructure.Persistence;
using GymOS.Platform.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.Platform.Infrastructure.Persistence.Configurations;

public sealed class PlatformSettingConfiguration : IEntityTypeConfiguration<PlatformSetting>
{
    public void Configure(EntityTypeBuilder<PlatformSetting> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("platform_settings", "platform", table =>
            // Exactly one of the two value columns is populated; the aggregate enforces it in code and
            // the database holds it under a bug. Written as an OR of two AND-ed predicates rather than
            // "(a IS NULL) <> (b IS NULL)" because T-SQL has no boolean scalar type — a predicate is
            // not a value <> can compare, only a search condition AND/OR can combine (SQL Server
            // rejects the Postgres-style XOR form with "Incorrect syntax near '<'").
            table.HasCheckConstraint(
                "ck_platform_settings_one_value",
                "([value] IS NULL AND [protected_value] IS NOT NULL) OR ([value] IS NOT NULL AND [protected_value] IS NULL)"));
        builder.HasKey(setting => setting.Id);

        builder.Property(setting => setting.Key).HasMaxLength(PlatformSetting.MaxKeyLength).IsRequired();
        builder.Property(setting => setting.Category).HasConversion<byte>();
        builder.Property(setting => setting.ValueType).HasConversion<byte>();
        builder.Property(setting => setting.Description).HasMaxLength(PlatformSetting.MaxDescriptionLength).IsRequired();

        builder.HasIndex(setting => setting.Key).IsUnique().HasDatabaseName("ux_platform_settings_key");

        builder.HasGymOSRowVersion();
    }
}
