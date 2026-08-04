using GymOS.SharedKernel.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace GymOS.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Model-building conventions every GymOS DbContext applies (call from
/// <c>ConfigureConventions</c>). Central rule: domain events are never persisted as
/// entity state — they exist only as outbox rows (AD-13). Without this, EF discovers
/// the <c>AggregateRoot.DomainEvents</c> collection on every mapped aggregate and
/// tries to model <see cref="DomainEvent"/> as an entity.
/// </summary>
public static class GymOSModelConventions
{
    public static ModelConfigurationBuilder ApplyGymOSConventions(this ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.IgnoreAny<DomainEvent>();
        return configurationBuilder;
    }

    /// <summary>
    /// Declares every single-<see cref="Guid"/> primary key as client-assigned. Call at the
    /// end of <c>OnModelCreating</c>.
    ///
    /// GymOS entities assign their own ids in their factory methods, but EF's default for a
    /// Guid key is <c>ValueGeneratedOnAdd</c> — and for an entity discovered through a
    /// navigation rather than an explicit <c>Add</c>, EF then reads "key already has a value"
    /// as "this row already exists" and tracks it as <b>Modified</b> instead of <b>Added</b>.
    /// The resulting UPDATE matches nothing and surfaces as a
    /// <c>DbUpdateConcurrencyException</c> — an error message that points at optimistic
    /// concurrency, which is not the problem at all.
    ///
    /// Aggregates added via a repository were never affected (an explicit <c>Add</c> forces
    /// the state), so this only ever bit collections reconciled through an aggregate — which
    /// is precisely where it is hardest to spot. Setting it once here removes the trap for
    /// every current and future entity.
    /// </summary>
    public static ModelBuilder ApplyClientAssignedKeys(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.FindPrimaryKey() is { Properties: [IMutableProperty key] } &&
                key.ClrType == typeof(Guid))
            {
                key.ValueGenerated = ValueGenerated.Never;
            }
        }

        return modelBuilder;
    }
}
