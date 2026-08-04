using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core adapter for the <see cref="User"/> aggregate. Both lookups include the role
/// assignments: they are part of the aggregate, and loading a User without them would
/// let a caller save one back with an empty role collection.
/// </summary>
internal sealed class UserRepository : IUserRepository
{
    private readonly AppDbContext _dbContext;

    public UserRepository(AppDbContext dbContext) => _dbContext = dbContext;

    public Task<User?> FindByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        _dbContext.Set<User>()
            .Include(user => user.Roles)
            .SingleOrDefaultAsync(user => user.NormalizedEmail == normalizedEmail, cancellationToken);

    public Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken) =>
        _dbContext.Set<User>()
            .Include(user => user.Roles)
            .SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);

    public void Add(User user) => _dbContext.Set<User>().Add(user);

    /// <summary>
    /// Overwrites the loaded row version with the caller's, so EF puts *their* expectation in the
    /// UPDATE's WHERE clause. If the row has moved on, the statement matches nothing and EF
    /// reports a concurrency failure.
    ///
    /// `xmin` is PostgreSQL's own row version, mapped as a shadow concurrency token
    /// (see UserConfiguration) — nothing has to be stored or incremented by hand.
    /// </summary>
    public void ExpectVersion(User user, uint expectedVersion) =>
        _dbContext.Entry(user).Property<uint>("xmin").OriginalValue = expectedVersion;

    /// <summary>
    /// Flags the audit timestamp as modified, which is enough to make EF issue an UPDATE against
    /// the users row — and PostgreSQL advances `xmin` on any update. The timestamps interceptor
    /// then fills in the value, so the row's version and its updated_at stay consistent with each
    /// other.
    /// </summary>
    public void MarkAggregateChanged(User user) =>
        _dbContext.Entry(user).Property(entity => entity.UpdatedAtUtc).IsModified = true;
}
