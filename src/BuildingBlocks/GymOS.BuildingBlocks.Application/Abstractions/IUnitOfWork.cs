namespace GymOS.BuildingBlocks.Application.Abstractions;

/// <summary>
/// Commits the work of one use case. Handlers mutate aggregates through repositories
/// and call this exactly once at the end: the single save is what makes state, the
/// audit rows, and the outbox rows commit in one transaction (AD-13/AD-39). No
/// EF Core type crosses into the Application layer — the abstraction is this method
/// and nothing more (AD-3).
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
