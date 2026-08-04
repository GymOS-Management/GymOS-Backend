using GymOS.SharedKernel.Events;
using Microsoft.EntityFrameworkCore;

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
}
