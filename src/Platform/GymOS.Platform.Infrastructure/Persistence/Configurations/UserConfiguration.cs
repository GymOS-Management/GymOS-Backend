using GymOS.BuildingBlocks.Infrastructure.Persistence;
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

        // Optimistic concurrency via the server-maintained row_version column (spec §2/§3):
        // a ulong row-version shadow property maps to a SQL Server rowversion column.
        builder.HasGymOSRowVersion();
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

        // One assignment per (user, role, tenant); a null tenant participates in uniqueness, so a
        // platform-scope assignment cannot be duplicated. SQL Server unique indexes treat NULLs as
        // equal, which is what that requires — see RoleConfiguration for why the PostgreSQL version
        // had to ask for it explicitly with NULLS NOT DISTINCT and this one must not.
        // `.HasFilter(null)` for the reason RoleConfiguration spells out: EF's SQL Server provider
        // would otherwise add `WHERE [tenant_id] IS NOT NULL` on its own and quietly exempt every
        // platform-scope assignment from this constraint.
        builder.HasIndex(userRole => new { userRole.UserId, userRole.RoleId, userRole.TenantId })
            .IsUnique()
            .HasFilter(null);
    }
}
