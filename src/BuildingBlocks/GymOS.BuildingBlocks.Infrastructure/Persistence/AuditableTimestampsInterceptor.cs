using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.SharedKernel.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GymOS.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Stamps <see cref="IAuditable"/> rows: created/updated on insert, updated on
/// modification — from <c>IClock</c> at the save edge (AD-40). One timestamp per
/// SaveChanges so every row touched by one unit of work carries the same instant.
/// </summary>
public sealed class AuditableTimestampsInterceptor : SaveChangesInterceptor
{
    private readonly IClock _clock;

    public AuditableTimestampsInterceptor(IClock clock) => _clock = clock;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        StampTimestamps(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        StampTimestamps(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void StampTimestamps(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        DateTimeOffset now = _clock.UtcNow;

        foreach (var entry in context.ChangeTracker.Entries<IAuditable>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAtUtc = now;
                entry.Entity.UpdatedAtUtc = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAtUtc = now;
            }
        }
    }
}
