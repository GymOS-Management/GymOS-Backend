using GymOS.BuildingBlocks.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GymOS.Platform.Infrastructure.Persistence;

/// <summary>
/// The single save that commits a use case. Thin on purpose: the interesting behavior —
/// timestamps, audit capture, outbox capture — lives in the context's interceptors, so every save
/// gets it whether or not the handler knows (AD-13).
///
/// Its one active responsibility is translating EF's concurrency failure at the boundary, so
/// <c>DbUpdateConcurrencyException</c> — an EF type — never reaches the Application layer that is
/// forbidden from referencing EF (rule R7).
/// </summary>
internal sealed class UnitOfWork : IUnitOfWork
{
    private readonly AppDbContext _dbContext;

    public UnitOfWork(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // The tracked entities are left as they are: recovery means re-reading in a fresh
            // scope, and silently reloading here would hand the caller a "successful" write that
            // discarded whatever the other writer did.
            throw new ConcurrencyConflictException(exception);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } unique)
        {
            // A unique index refused the write — the race the handlers' pre-checks cannot close,
            // and the reason those indexes exist. Not a server fault, so not a 500: translated
            // into the conflict the pre-check itself would have returned.
            throw new UniquenessConflictException(exception, unique.ConstraintName);
        }
    }

    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new Transaction(await _dbContext.Database.BeginTransactionAsync(cancellationToken));

    public void DiscardTrackedChanges() => _dbContext.ChangeTracker.Clear();

    private sealed class Transaction : IUnitOfWorkTransaction
    {
        private readonly Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction _transaction;

        public Transaction(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction) =>
            _transaction = transaction;

        public Task CommitAsync(CancellationToken cancellationToken) =>
            _transaction.CommitAsync(cancellationToken);

        // Disposing an uncommitted EF transaction rolls it back — the contract the interface states.
        public ValueTask DisposeAsync() => _transaction.DisposeAsync();
    }
}
