using GymOS.BuildingBlocks.Application.Abstractions;

namespace GymOS.Platform.Infrastructure.Persistence;

/// <summary>
/// The single save that commits a use case. Thin on purpose: the interesting behavior —
/// timestamps and outbox capture — lives in the context's interceptors, so every save
/// gets it whether or not the handler knows (AD-13).
/// </summary>
internal sealed class UnitOfWork : IUnitOfWork
{
    private readonly AppDbContext _dbContext;

    public UnitOfWork(AppDbContext dbContext) => _dbContext = dbContext;

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        _dbContext.SaveChangesAsync(cancellationToken);
}
