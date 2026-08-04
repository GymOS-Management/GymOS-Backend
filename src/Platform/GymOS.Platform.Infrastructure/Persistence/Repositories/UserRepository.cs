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
}
