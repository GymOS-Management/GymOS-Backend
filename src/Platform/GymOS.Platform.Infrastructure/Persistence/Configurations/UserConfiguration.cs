using GymOS.Platform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.Platform.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("users", "platform");
        builder.HasKey(user => user.Id);

        builder.Property(user => user.Email).HasMaxLength(320);
        builder.Property(user => user.NormalizedEmail).HasMaxLength(320);
        builder.Property(user => user.PasswordHash).HasMaxLength(500);
        builder.Property(user => user.FirstName).HasMaxLength(100);
        builder.Property(user => user.LastName).HasMaxLength(100);
        builder.Property(user => user.SuspensionReason).HasMaxLength(1000);
        builder.Property(user => user.Status).HasConversion<short>();

        builder.HasIndex(user => user.NormalizedEmail).IsUnique();

        builder.HasMany(user => user.Roles)
            .WithOne()
            .HasForeignKey(userRole => userRole.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(user => user.Roles)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // Optimistic concurrency via PostgreSQL's xmin system column (spec §2/§3):
        // a uint row-version shadow property maps to xmin under Npgsql.
        builder.Property<uint>("xmin").IsRowVersion();
    }
}

public sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("user_roles", "platform");
        builder.HasKey(userRole => userRole.Id);

        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(userRole => userRole.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        // One assignment per (user, role, tenant); null tenant participates in
        // uniqueness (PostgreSQL 15+ NULLS NOT DISTINCT).
        builder.HasIndex(userRole => new { userRole.UserId, userRole.RoleId, userRole.TenantId })
            .IsUnique()
            .AreNullsDistinct(false);
    }
}
