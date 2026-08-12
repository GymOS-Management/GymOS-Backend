using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence.Repositories;

internal sealed class UserActivationTokenRepository : IUserActivationTokenRepository
{
    private readonly AppDbContext _dbContext;

    public UserActivationTokenRepository(AppDbContext dbContext) => _dbContext = dbContext;

    /// <summary>
    /// Hits the unique index on <c>token_hash</c> — one row or none, never a scan.
    ///
    /// <para>Tracked, and that is required rather than incidental: the command path calls
    /// <c>Consume</c> on what comes back, and an untracked entity would take that mutation to the
    /// garbage collector instead of the database, leaving the link replayable.</para>
    /// </summary>
    public Task<UserActivationToken?> FindByHashAsync(
        string tokenHash,
        CancellationToken cancellationToken) =>
        _dbContext.Set<UserActivationToken>()
            .SingleOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);

    /// <inheritdoc />
    public Task<UserActivationToken?> FindByHashForReadingAsync(
        string tokenHash,
        CancellationToken cancellationToken) =>
        _dbContext.Set<UserActivationToken>()
            .AsNoTracking()
            .SingleOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);

    public void Add(UserActivationToken token) => _dbContext.Set<UserActivationToken>().Add(token);

    public async Task InvalidateOutstandingAsync(
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Loaded and consumed through the aggregate rather than a bulk `ExecuteUpdate`: the rows are
        // few by construction (at most one live token per user, which is what this maintains), and
        // going through `Consume` keeps one definition of what spending a token means.
        List<UserActivationToken> outstanding = await _dbContext.Set<UserActivationToken>()
            .Where(token => token.UserId == userId && token.ConsumedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (UserActivationToken token in outstanding)
        {
            token.Consume(now);
        }
    }

    public Task<int> PurgeExpiredAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken) =>
        _dbContext.Set<UserActivationToken>()
            .Where(token => token.ExpiresAtUtc < olderThanUtc)
            .ExecuteDeleteAsync(cancellationToken);
}
