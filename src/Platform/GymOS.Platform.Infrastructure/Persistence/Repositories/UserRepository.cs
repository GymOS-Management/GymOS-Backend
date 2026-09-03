using System.Data;
using System.Data.Common;
using GymOS.BuildingBlocks.Infrastructure.Persistence;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

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
    /// One <c>UPDATE</c>, with the increment and the threshold test in the same statement, so two
    /// attempts can never read the same streak: SQL Server evaluates every <c>CASE</c> against the
    /// row's value as of that statement, under the row lock the update takes. <c>OUTPUT</c> returns
    /// what the row now holds, so the aggregate reflects this attempt's result and not a later one's.
    ///
    /// <para>Raw ADO.NET rather than <c>ExecuteUpdate</c> or <c>SqlQuery</c>: the first cannot
    /// return the updated values, and the second wraps its SQL in a subquery, which an
    /// <c>UPDATE … OUTPUT</c> cannot be. The same pattern as <c>InvoiceNumberGenerator</c>, for the
    /// same reason. The <c>WHERE</c> clause is the lockout check from <see cref="User.IsLockedOut"/>,
    /// restated in SQL so that a guess arriving after a concurrent one has already locked the account
    /// is not tallied: zero rows affected is the "already locked" outcome, and the aggregate is then
    /// simply refreshed to what the store says.</para>
    ///
    /// <para>The row version advances, as it does on any update, so the tracked aggregate is reloaded
    /// afterwards. That is what lets the caller's own <c>SaveChangesAsync</c> — the audit entry and,
    /// on the locking attempt, the outbox message — go through instead of colliding with the very
    /// update this method just made. It costs one <c>SELECT</c> on a path that has just paid for an
    /// Argon2 verification.</para>
    /// </summary>
    public async Task RecordFailedLoginAsync(
        User user,
        DateTimeOffset now,
        int maxAttempts,
        TimeSpan lockoutDuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        DateTimeOffset lockedOutUntil = now.Add(lockoutDuration);
        int failedLoginAttempts;
        DateTimeOffset? lockedOutUntilUtc;

        DbConnection connection = _dbContext.Database.GetDbConnection();
        await _dbContext.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using DbCommand command = connection.CreateCommand();
            command.Transaction = _dbContext.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = """
                UPDATE platform.users
                SET failed_login_attempts = CASE WHEN failed_login_attempts + 1 >= @maxAttempts
                                                 THEN 0
                                                 ELSE failed_login_attempts + 1 END,
                    locked_out_until_utc  = CASE WHEN failed_login_attempts + 1 >= @maxAttempts
                                                 THEN @lockedOutUntil
                                                 ELSE locked_out_until_utc END
                OUTPUT inserted.failed_login_attempts, inserted.locked_out_until_utc
                WHERE id = @userId
                  AND (locked_out_until_utc IS NULL OR locked_out_until_utc <= @now);
                """;

            AddParameter(command, "@userId", DbType.Guid, user.Id);
            AddParameter(command, "@maxAttempts", DbType.Int32, maxAttempts);
            AddParameter(command, "@lockedOutUntil", DbType.DateTimeOffset, lockedOutUntil);
            AddParameter(command, "@now", DbType.DateTimeOffset, now);

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                // Already locked out by a concurrent attempt (or the row is gone). Nothing was
                // counted; the reload below brings the aggregate in line with the store.
                failedLoginAttempts = -1;
                lockedOutUntilUtc = null;
            }
            else
            {
                failedLoginAttempts = reader.GetInt32(0);
                lockedOutUntilUtc = await reader.IsDBNullAsync(1, cancellationToken)
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(1);
            }
        }
        finally
        {
            await _dbContext.Database.CloseConnectionAsync();
        }

        if (failedLoginAttempts >= 0)
        {
            user.ReflectFailedLogin(failedLoginAttempts, lockedOutUntilUtc, now);
        }

        // Unchanged against the row as it now stands — values, and the advanced row version — so
        // nothing about this aggregate is written again by the caller's SaveChanges. Domain events
        // live on the object, not in the tracker, and survive the reload.
        await _dbContext.Entry(user).ReloadAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, DbType type, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// Overwrites the loaded row version with the caller's, so EF puts *their* expectation in the
    /// UPDATE's WHERE clause. If the row has moved on, the statement matches nothing and EF
    /// reports a concurrency failure.
    ///
    /// `row_version` is a SQL Server rowversion, mapped as a shadow concurrency token
    /// (see UserConfiguration) — nothing has to be stored or incremented by hand.
    /// </summary>
    public void ExpectVersion(User user, ulong expectedVersion) =>
        _dbContext.Entry(user).Property<ulong>(ConcurrencyConventions.RowVersionProperty).OriginalValue = expectedVersion;

    /// <summary>
    /// Flags the audit timestamp as modified, which is enough to make EF issue an UPDATE against
    /// the users row — and SQL Server advances `row_version` on any update. The timestamps interceptor
    /// then fills in the value, so the row's version and its updated_at stay consistent with each
    /// other.
    /// </summary>
    public void MarkAggregateChanged(User user) =>
        _dbContext.Entry(user).Property(entity => entity.UpdatedAtUtc).IsModified = true;
}
