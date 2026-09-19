using GymOS.BuildingBlocks.Infrastructure.Outbox;
using GymOS.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence;

/// <summary>
/// The single database context over the one PostgreSQL database (AD-15/ADR-52).
/// Entity configurations compose by assembly: building blocks (outbox) now, platform
/// entities with M1, and feature-module assemblies via the module loader when it
/// lands (each module owning its own schema, AD-16). Naming is snake_case via
/// convention; tenant tables add RLS policies with the first tenant-scoped entity.
/// Also the Data Protection key store (M7, AD-9): <see cref="DataProtectionKeys"/> is the ring
/// ASP.NET's key manager reads and writes directly, outside the aggregate/repository pattern.
/// </summary>
public sealed class AppDbContext : DbContext, IDataProtectionKeyContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.ApplyGymOSConventions();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OutboxMessage).Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Last: entities assign their own ids, so no Guid key is store-generated.
        modelBuilder.ApplyClientAssignedKeys();
    }
}
