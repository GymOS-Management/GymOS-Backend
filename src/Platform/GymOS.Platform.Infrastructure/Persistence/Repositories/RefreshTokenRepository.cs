using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence.Repositories;

internal sealed class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly AppDbContext _dbContext;

    public RefreshTokenRepository(AppDbContext dbContext) => _dbContext = dbContext;

    /// <summary>Hits the unique index on <c>token_hash</c> — one row or none, never a scan.</summary>
    public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        _dbContext.Set<RefreshToken>()
            .SingleOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);

    public async Task<RefreshToken?> FindByHashForUpdateAsync(
        string tokenHash,
        CancellationToken cancellationToken)
    {
        // Two statements, the SubscriptionRepository pattern: EF has no pessimistic-locking operator, so
        // the lock is taken as a scalar statement (a row lock needs no entity) and the aggregate
        // then loads through the normal pipeline on the same connection and transaction, where
        // the lock is already held. Concurrent rotations of one token serialize here — which is
        // what lets the second one see a revoked token and detect the reuse.
        await _dbContext.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM platform.refresh_tokens WITH (UPDLOCK, ROWLOCK) WHERE token_hash = {tokenHash}",
            cancellationToken);

        return await FindByHashAsync(tokenHash, cancellationToken);
    }

    public async Task<IReadOnlyList<RefreshToken>> ListByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await _dbContext.Set<RefreshToken>()
            .Where(token => token.UserId == userId)
            .ToListAsync(cancellationToken);

    public void Add(RefreshToken refreshToken) => _dbContext.Set<RefreshToken>().Add(refreshToken);
}
