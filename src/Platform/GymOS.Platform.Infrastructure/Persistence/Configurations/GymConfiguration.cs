using GymOS.Platform.Domain.Gyms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.Platform.Infrastructure.Persistence.Configurations;

public sealed class GymConfiguration : IEntityTypeConfiguration<Gym>
{
    public void Configure(EntityTypeBuilder<Gym> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("gyms", "platform", table =>
            // The slug is lower-cased by the domain rule, and the CHECK keeps it that way for anything
            // that reaches the table another way — a data fix, a future import. Without it, one
            // mixed-case row would make the unique index stop meaning "one gym per address".
            table.HasCheckConstraint("ck_gyms_slug_lowercase", "slug = lower(slug)"));

        builder.HasKey(gym => gym.Id);

        builder.Property(gym => gym.Slug).HasMaxLength(63);
        builder.Property(gym => gym.Name).HasMaxLength(200);
        builder.Property(gym => gym.OwnerName).HasMaxLength(200);
        builder.Property(gym => gym.OwnerEmail).HasMaxLength(320);
        builder.Property(gym => gym.Phone).HasMaxLength(40);
        // varchar, not char(2)/char(3): bpchar compares ignoring trailing spaces, which would make a
        // padded value equal to a clean one in an index. The exact length is a domain rule.
        builder.Property(gym => gym.CountryCode).HasMaxLength(2);
        builder.Property(gym => gym.TimeZoneId).HasMaxLength(100);
        builder.Property(gym => gym.Currency).HasMaxLength(3);
        builder.Property(gym => gym.SuspensionReason).HasMaxLength(1000);
        builder.Property(gym => gym.Status).HasConversion<short>();

        // Unique across every gym including the deleted ones — no filter. A slug is a public address
        // that may still be in a member's bookmark, so it is never handed to a different gym.
        builder.HasIndex(gym => gym.Slug).IsUnique();

        // The console's default list is "live gyms, newest first"; status also carries the
        // deleted/not-deleted split, so this one index serves both.
        builder.HasIndex(gym => new { gym.Status, gym.CreatedAtUtc });

        // Optimistic concurrency via PostgreSQL's xmin system column (ARCHITECTURE.md §3).
        builder.Property<uint>("xmin").IsRowVersion();
    }
}

public sealed class GymBrandingConfiguration : IEntityTypeConfiguration<GymBranding>
{
    public void Configure(EntityTypeBuilder<GymBranding> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("gym_branding", "platform");
        builder.HasKey(branding => branding.Id);

        builder.Property(branding => branding.LogoUrl).HasMaxLength(500);
        builder.Property(branding => branding.FaviconUrl).HasMaxLength(500);
        builder.Property(branding => branding.PrimaryColour).HasMaxLength(7);
        builder.Property(branding => branding.SecondaryColour).HasMaxLength(7);
        builder.Property(branding => branding.CustomDomain).HasMaxLength(253);
        builder.Property(branding => branding.EmailSenderName).HasMaxLength(100);

        // One branding record per gym. A second row would mean the detail view and the member-facing
        // site could disagree about which logo is current.
        builder.HasIndex(branding => branding.GymId).IsUnique();

        builder.HasOne<Gym>()
            .WithOne()
            .HasForeignKey<GymBranding>(branding => branding.GymId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique only among the gyms that set one: a custom domain must resolve to exactly one gym, and
        // PostgreSQL treats nulls as distinct so the unset majority does not collide. Filtered so the
        // index stays small and its meaning stays "claimed domains".
        builder.HasIndex(branding => branding.CustomDomain)
            .IsUnique()
            .HasFilter("custom_domain IS NOT NULL");

        builder.Property<uint>("xmin").IsRowVersion();
    }
}
