using GymOS.Platform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.Platform.Infrastructure.Persistence.Configurations;

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("roles", "platform");
        builder.HasKey(role => role.Id);

        builder.Property(role => role.Name).HasMaxLength(100);
        builder.Property(role => role.NormalizedName).HasMaxLength(100);
        builder.Property(role => role.Description).HasMaxLength(500);
        builder.Property(role => role.Scope).HasConversion<short>();

        // Role names are unique within their scope; a platform role (null tenant)
        // can't be duplicated either (NULLS NOT DISTINCT).
        builder.HasIndex(role => new { role.NormalizedName, role.TenantId })
            .IsUnique()
            .AreNullsDistinct(false);

        builder.HasMany(role => role.Permissions)
            .WithOne()
            .HasForeignKey(rolePermission => rolePermission.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(role => role.Permissions)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Property<uint>("xmin").IsRowVersion();
    }
}

public sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("role_permissions", "platform");
        builder.HasKey(rolePermission => new { rolePermission.RoleId, rolePermission.PermissionId });

        builder.HasOne<Permission>()
            .WithMany()
            .HasForeignKey(rolePermission => rolePermission.PermissionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("permissions", "platform");
        builder.HasKey(permission => permission.Id);

        builder.Property(permission => permission.Key).HasMaxLength(100);
        builder.Property(permission => permission.Module).HasMaxLength(50);
        builder.Property(permission => permission.Description).HasMaxLength(500);

        builder.HasIndex(permission => permission.Key).IsUnique();
    }
}
