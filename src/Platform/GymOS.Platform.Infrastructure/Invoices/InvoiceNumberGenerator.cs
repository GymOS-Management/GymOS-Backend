using System.Data;
using System.Data.Common;
using System.Globalization;
using GymOS.Platform.Application.Invoices.Abstractions;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GymOS.Platform.Infrastructure.Invoices;

/// <summary>
/// Draws invoice numbers from <c>platform.next_invoice_number</c> (the RuntimeDatabaseProgrammability
/// migration), which creates a per-year sequence on first use and returns the next value from it.
///
/// <para>A database sequence rather than <c>MAX + 1</c> because a sequence is the one counter SQL
/// Server hands out race-free without locking the table — eight concurrent issuers get eight distinct
/// numbers, which the persistence tests assert literally.</para>
///
/// <para><b>A stored procedure, not a function.</b> The PostgreSQL original was a plpgsql function
/// called from a <c>SELECT</c>, because PostgreSQL lets a function issue DDL. SQL Server does not: a
/// scalar user-defined function may not execute <c>CREATE SEQUENCE</c>, nor call <c>NEXT VALUE FOR</c>,
/// nor perform any side effect at all. The per-year sequence therefore has to be created and read by a
/// procedure, and a procedure cannot be composed into a query — hence the raw ADO.NET call below
/// rather than <c>SqlQueryRaw</c>, which EF would wrap in a subquery.</para>
/// </summary>
internal sealed class InvoiceNumberGenerator : IInvoiceNumberGenerator
{
    private readonly AppDbContext _dbContext;

    public InvoiceNumberGenerator(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<string> NextAsync(int year, CancellationToken cancellationToken)
    {
        DbConnection connection = _dbContext.Database.GetDbConnection();
        await _dbContext.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using DbCommand command = connection.CreateCommand();

            // Invoices are issued inside the aggregate's transaction, so this must run in it too —
            // both because an ADO command that ignores an ambient transaction on its connection
            // throws, and because a number drawn outside the transaction would be consumed even if
            // the invoice were rolled back.
            command.Transaction = _dbContext.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandType = CommandType.StoredProcedure;
            command.CommandText = "platform.next_invoice_number";

            DbParameter yearParameter = command.CreateParameter();
            yearParameter.ParameterName = "@year";
            yearParameter.DbType = DbType.Int32;
            yearParameter.Value = year;
            command.Parameters.Add(yearParameter);

            DbParameter numberParameter = command.CreateParameter();
            numberParameter.ParameterName = "@number";
            numberParameter.DbType = DbType.Int64;
            numberParameter.Direction = ParameterDirection.Output;
            command.Parameters.Add(numberParameter);

            await command.ExecuteNonQueryAsync(cancellationToken);

            long next = numberParameter.Value is long value
                ? value
                : throw new InvalidOperationException(
                    $"platform.next_invoice_number returned no number for {year}.");

            return string.Create(CultureInfo.InvariantCulture, $"{year}-{next:D6}");
        }
        finally
        {
            await _dbContext.Database.CloseConnectionAsync();
        }
    }
}
