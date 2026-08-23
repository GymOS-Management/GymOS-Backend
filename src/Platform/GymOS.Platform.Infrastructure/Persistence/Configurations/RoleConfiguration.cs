using GymOS.BuildingBlocks.Infrastructure.Persistence;
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
        // can't be duplicated either.
        //
        // That second half needed `.AreNullsDistinct(false)` under PostgreSQL, whose unique indexes
        // follow the SQL standard: NULL is never equal to NULL, so without NULLS NOT DISTINCT
        // (PostgreSQL 15+) any number of platform roles could share a name. SQL Server departs from
        // the standard here and treats NULLs as equal in a unique index — one NULL row, at most — so
        // the behaviour this index needs is the default and the modifier has no counterpart to write.
        // Removing it is not a relaxation; writing nothing is what keeps the constraint.
        //
        // `.HasFilter(null)` is doing real work and must stay. EF Core's SQL Server provider adds
        // `WHERE [tenant_id] IS NOT NULL` to any unique index over a nullable column, by default and
        // silently — scaffolding this migration produced exactly that. A filtered index does not
        // index the NULL rows at all, so it would have excluded every platform-scope role from the
        // constraint and allowed unlimited duplicates of the case this index exists to prevent. The
        // null clears the filter and restores the whole-table index the model asks for.
        builder.HasIndex(role => new { role.NormalizedName, role.TenantId })
            .IsUnique()
            .HasFilter(null);

        builder.HasMany(role => role.Permissions)
            .WithOne()
            .HasForeignKey(rolePermission => rolePermission.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(role => role.Permissions)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasGymOSRowVersion();
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
