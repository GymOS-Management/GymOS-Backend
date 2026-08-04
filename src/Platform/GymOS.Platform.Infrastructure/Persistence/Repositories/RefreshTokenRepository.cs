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

    public async Task<IReadOnlyList<RefreshToken>> ListByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await _dbContext.Set<RefreshToken>()
            .Where(token => token.UserId == userId)
            .ToListAsync(cancellationToken);

    public void Add(RefreshToken refreshToken) => _dbContext.Set<RefreshToken>().Add(refreshToken);
}
