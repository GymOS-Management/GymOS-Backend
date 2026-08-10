using System.Globalization;
using GymOS.Platform.Application.Invoices.Abstractions;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GymOS.Platform.Infrastructure.Invoices;

/// <summary>
/// Draws invoice numbers from <c>platform.next_invoice_number</c> (the AddInvoices migration), which
/// creates a per-year sequence on first use and returns <c>nextval</c> from it.
///
/// <para>A database sequence rather than <c>MAX + 1</c> because sequences are the one counter
/// PostgreSQL hands out race-free without locking the table — eight concurrent issuers get eight
/// distinct numbers, which the persistence tests assert literally.</para>
/// </summary>
internal sealed class InvoiceNumberGenerator : IInvoiceNumberGenerator
{
    private readonly AppDbContext _dbContext;

    public InvoiceNumberGenerator(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<string> NextAsync(int year, CancellationToken cancellationToken)
    {
        var yearParameter = new NpgsqlParameter<int>("p_year", year);

        long next = await _dbContext.Database
            .SqlQueryRaw<long>("SELECT platform.next_invoice_number(@p_year) AS \"Value\"", yearParameter)
            .SingleAsync(cancellationToken);

        return string.Create(CultureInfo.InvariantCulture, $"{year}-{next:D6}");
    }
}
