using System.Data;
using System.Data.Common;
using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GymOS.Platform.Infrastructure.Jobs;

/// <summary>
/// <see cref="IJobLock"/> as a SQL Server session application lock — the database every instance
/// already shares is the coordinator, the same reasoning as the outbox's claim query (AD-13).
///
/// <para>The lock is <b>session-scoped</b>: it lives exactly as long as the connection that took it.
/// Taking it explicitly opens the scope's <see cref="AppDbContext"/> connection and holds it open, so
/// the job's own queries in the same scope ride the same session and the lock cannot outlive — or die
/// before — the run it protects. Disposing the handle unlocks and closes; a crashed process drops its
/// connection and SQL Server releases the lock by itself, so a dead instance can never wedge a job
/// shut.</para>
///
/// <para><b>Try-acquire, not wait.</b> <c>@LockTimeout = 0</c> is what makes this the skip-this-tick
/// primitive <see cref="IJobLock"/> describes: the instance that loses simply runs on the next tick,
/// rather than queueing up behind the winner and running twice in a row.</para>
///
/// <para><b>Names are passed through, not hashed</b> — see <c>SqlServerAdvisoryLock</c> for why the
/// PostgreSQL original's <c>hashtext()</c> has no counterpart and needs none.</para>
/// </summary>
internal sealed class SqlServerJobLock : IJobLock
{
    /// <summary>Return immediately rather than queue: a job that loses its tick runs on the next one.</summary>
    private const int DoNotWait = 0;

    private readonly AppDbContext _dbContext;

    public SqlServerJobLock(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<IAsyncDisposable?> TryAcquireAsync(string jobName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);

        // Held open for the run — see the type's remarks. Closed by the handle, or by scope
        // disposal if acquisition fails mid-way.
        await _dbContext.Database.OpenConnectionAsync(cancellationToken);

        int result = await ExecuteAppLockAsync(_dbContext, "sys.sp_getapplock", LockKey(jobName), DoNotWait, cancellationToken);

        // 0 granted, 1 granted after waiting (unreachable at @LockTimeout = 0). -1 is "someone else
        // holds it", which is the ordinary outcome on every instance but one and is not an error.
        if (result < 0)
        {
            await _dbContext.Database.CloseConnectionAsync();
            return null;
        }

        return new Handle(_dbContext, jobName);
    }

    /// <summary>
    /// Namespaced so a job lock can never collide with any other application-lock use of the same
    /// database — in particular the <c>gymos:set:</c> space <c>SqlServerAdvisoryLock</c> owns.
    /// </summary>
    private static string LockKey(string jobName) => $"gymos:job:{jobName}";

    /// <summary>
    /// Issues <c>sp_getapplock</c>/<c>sp_releaseapplock</c> and returns the procedure's return code.
    /// </summary>
    /// <remarks>
    /// Raw ADO.NET rather than <c>SqlQueryRaw</c>: the value these procedures report is a RETURN
    /// code, not a result set, and EF composes over a raw SQL query (it wraps it to apply
    /// <c>SingleAsync</c>'s TOP), which a multi-statement batch capturing the code into a variable
    /// would not survive.
    /// </remarks>
    private static async Task<int> ExecuteAppLockAsync(
        AppDbContext dbContext,
        string procedure,
        string resource,
        int? lockTimeout,
        CancellationToken cancellationToken)
    {
        DbConnection connection = dbContext.Database.GetDbConnection();

        await using DbCommand command = connection.CreateCommand();

        // Null in the normal case — a job takes its lock before opening a transaction. Set anyway,
        // because an ADO command that ignores an ambient transaction on its connection throws.
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = procedure;

        AddParameter(command, "@Resource", DbType.String, resource);
        AddParameter(command, "@LockOwner", DbType.String, "Session");

        if (lockTimeout is int timeout)
        {
            // sp_releaseapplock takes no @LockMode/@LockTimeout, so both are acquire-only.
            AddParameter(command, "@LockMode", DbType.String, "Exclusive");
            AddParameter(command, "@LockTimeout", DbType.Int32, timeout);
        }

        DbParameter returnValue = command.CreateParameter();
        returnValue.ParameterName = "@ReturnValue";
        returnValue.DbType = DbType.Int32;
        returnValue.Direction = ParameterDirection.ReturnValue;
        command.Parameters.Add(returnValue);

        await command.ExecuteNonQueryAsync(cancellationToken);

        return returnValue.Value is int code ? code : -999;
    }

    private static void AddParameter(DbCommand command, string name, DbType type, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed class Handle : IAsyncDisposable
    {
        private readonly AppDbContext _dbContext;
        private readonly string _jobName;

        public Handle(AppDbContext dbContext, string jobName)
        {
            _dbContext = dbContext;
            _jobName = jobName;
        }

        public async ValueTask DisposeAsync()
        {
            // Explicit unlock rather than relying on connection close alone: the scope's context may
            // pool the physical connection, and although sp_reset_connection releases session
            // application locks when the pooled connection is next handed out, that is the pool's
            // behaviour rather than this type's guarantee — and it leaves the lock held for however
            // long the connection sits idle in between. Releasing here bounds it to the run.
            await ExecuteAppLockAsync(
                _dbContext, "sys.sp_releaseapplock", LockKey(_jobName), lockTimeout: null, CancellationToken.None);

            await _dbContext.Database.CloseConnectionAsync();
        }
    }
}
