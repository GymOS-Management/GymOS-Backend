using System.Text.RegularExpressions;
using GymOS.BuildingBlocks.Application.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

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
internal sealed partial class UnitOfWork : IUnitOfWork
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
            when (exception.InnerException is SqlException { Number: DuplicateKeyInIndex or UniqueConstraintViolation } unique)
        {
            // A unique index refused the write — the race the handlers' pre-checks cannot close,
            // and the reason those indexes exist. Not a server fault, so not a 500: translated
            // into the conflict the pre-check itself would have returned.
            throw new UniquenessConflictException(exception, ConstraintNameOf(unique));
        }
    }

    /// <summary>2601 — a unique <b>index</b> rejected the row.</summary>
    private const int DuplicateKeyInIndex = 2601;

    /// <summary>2627 — a unique <b>constraint</b> (or primary key) rejected the row.</summary>
    private const int UniqueConstraintViolation = 2627;

    /// <summary>
    /// The index or constraint that refused the write, recovered from the server's message.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is parsed rather than read from a property.</b> Npgsql surfaced the name
    /// structurally, as <c>PostgresException.ConstraintName</c>. <see cref="SqlException"/> has no
    /// equivalent member — the name exists only inside <see cref="Exception.Message"/> — so recovering
    /// it means reading the message, and the alternative is losing it. Losing it is not an option:
    /// <c>SetGymFeaturesCommand</c> retries on precisely one constraint
    /// (<c>ux_gym_feature_overrides_gym_feature</c>) and must not retry on any other, so a null name
    /// there silently turns a documented "the later decision wins" into a 409.</para>
    /// <para><b>The two messages put the name in different places</b>, which is why the error number
    /// selects the group rather than one pattern trying to serve both:</para>
    /// <list type="bullet">
    /// <item>2601 — "Cannot insert duplicate key row in object 'platform.t' with unique index
    /// 'ix_name'." The name is the <i>second</i> quoted token; the first is the table.</item>
    /// <item>2627 — "Violation of UNIQUE KEY constraint 'uq_name'. Cannot insert duplicate key in
    /// object 'platform.t'." The name is the <i>first</i>.</item>
    /// </list>
    /// <para><b>It returns null rather than guessing</b> when the message does not match. A wrong name
    /// is worse than none: it could match a constraint the caller retries on. The connection is opened
    /// with the invariant language (<c>Current Language=us_english</c> in every connection string this
    /// repository ships, and the default for a fresh SQL Server), because these messages are localised
    /// on a server configured otherwise and the quoting is all that survives translation.</para>
    /// </remarks>
    private static string? ConstraintNameOf(SqlException exception)
    {
        MatchCollection quoted = QuotedName().Matches(exception.Message);

        int index = exception.Number == DuplicateKeyInIndex ? 1 : 0;

        return quoted.Count > index ? quoted[index].Groups["name"].Value : null;
    }

    [GeneratedRegex("'(?<name>[^']*)'")]
    private static partial Regex QuotedName();

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
