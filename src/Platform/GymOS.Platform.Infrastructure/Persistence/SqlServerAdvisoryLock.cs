using System.Data;
using System.Data.Common;
using GymOS.BuildingBlocks.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GymOS.Platform.Infrastructure.Persistence;

/// <summary>
/// <see cref="IAdvisoryLock"/> as a SQL Server <b>transaction-scoped</b> application lock.
///
/// <para><c>sp_getapplock</c> with <c>@LockOwner = 'Transaction'</c> rather than <c>'Session'</c>,
/// which is what the job lock uses: this one is released by the transaction ending, whichever way it
/// ends. A handler that throws between acquiring and committing cannot leave the lock held — there is
/// no dispose to forget, and a crashed process releases it with its transaction.</para>
///
/// <para><b>Names are passed through, not hashed.</b> The PostgreSQL original wrapped the name in
/// <c>hashtext()</c> because <c>pg_advisory_xact_lock</c> only takes a bigint key, which made two
/// distinct names capable of colliding on one hash. <c>sp_getapplock</c> takes the string itself
/// (<c>@Resource</c>, nvarchar(255)), so that collision class does not exist here — the namespacing
/// prefix below is now the only thing separating lock spaces, and it is exact rather than probable.</para>
/// </summary>
internal sealed class SqlServerAdvisoryLock : IAdvisoryLock
{
    /// <summary>Wait indefinitely, matching <c>pg_advisory_xact_lock</c>'s blocking semantics.</summary>
    private const int WaitForever = -1;

    private readonly AppDbContext _dbContext;

    public SqlServerAdvisoryLock(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task AcquireAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Namespaced so a set lock can never collide with the job locks in the same database.
        string resource = $"gymos:set:{name}";

        // The interface documents that callers hold a transaction, because a transaction-scoped lock
        // released the instant it is taken protects nothing. Under PostgreSQL a missing transaction
        // was silent: `pg_advisory_xact_lock` outside one acquires and releases within the implicit
        // single-statement transaction, so the check-then-act it guards simply ran unserialized.
        // SQL Server refuses instead ("the transaction is not active"), and this turns that into a
        // sentence that names the caller's mistake rather than the server's error number.
        IDbContextTransaction? transaction = _dbContext.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                $"Advisory lock '{name}' was requested with no ambient transaction. A " +
                "transaction-scoped lock taken outside a transaction is released immediately, so " +
                "the invariant it guards would not be serialized. Open a transaction " +
                "(IUnitOfWork.BeginTransactionAsync) before acquiring.");

        DbConnection connection = _dbContext.Database.GetDbConnection();

        await using DbCommand command = connection.CreateCommand();

        // Enlisted explicitly: a command created off the connection does not inherit the ambient
        // transaction, and one that ran outside it would take the lock on its own autocommit
        // transaction and release it on the next statement.
        command.Transaction = transaction.GetDbTransaction();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "sys.sp_getapplock";

        AddParameter(command, "@Resource", DbType.String, resource);
        AddParameter(command, "@LockMode", DbType.String, "Exclusive");
        AddParameter(command, "@LockOwner", DbType.String, "Transaction");
        AddParameter(command, "@LockTimeout", DbType.Int32, WaitForever);

        DbParameter returnValue = command.CreateParameter();
        returnValue.ParameterName = "@ReturnValue";
        returnValue.DbType = DbType.Int32;
        returnValue.Direction = ParameterDirection.ReturnValue;
        command.Parameters.Add(returnValue);

        await command.ExecuteNonQueryAsync(cancellationToken);

        // 0 granted, 1 granted after waiting. Everything negative is a refusal: -1 timeout (not
        // reachable with an infinite @LockTimeout), -2 cancelled, -3 chosen as a deadlock victim,
        // -999 a parameter or call error. None of them may be mistaken for "acquired" — the caller
        // is about to check-then-act on the strength of holding this.
        int result = returnValue.Value is int code ? code : -999;

        if (result < 0)
        {
            throw new InvalidOperationException(
                $"Could not acquire advisory lock '{name}' (sp_getapplock returned {result}).");
        }
    }

    private static void AddParameter(DbCommand command, string name, DbType type, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
