using GymOS.BuildingBlocks.Infrastructure.Outbox;
using GymOS.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence;

/// <summary>
/// The single database context over the one PostgreSQL database (AD-15/ADR-52).
/// Entity configurations compose by assembly: building blocks (outbox) now, platform
/// entities with M1, and feature-module assemblies via the module loader when it
/// lands (each module owning its own schema, AD-16). Naming is snake_case via
/// convention; tenant tables add RLS policies with the first tenant-scoped entity.
/// </summary>
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

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
    }
}
