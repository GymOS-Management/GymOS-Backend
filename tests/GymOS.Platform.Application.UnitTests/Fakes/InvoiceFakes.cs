using System.Globalization;
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Invoices.Abstractions;
using GymOS.Platform.Domain.Invoices;

namespace GymOS.Platform.Application.UnitTests.Fakes;

/// <summary>In-memory stand-in for the invoice write side.</summary>
internal sealed class FakeInvoiceRepository : IInvoiceRepository
{
    private readonly List<Invoice> _invoices = [];

    public IReadOnlyList<Invoice> Invoices => _invoices;

    public Dictionary<Guid, uint> ExpectedVersions { get; } = [];

    /// <summary>Set to make <see cref="ExpectVersion"/> behave as a stale-version write.</summary>
    public uint? ConflictingVersion { get; set; }

    public void Seed(Invoice invoice) => _invoices.Add(invoice);

    public Task<Invoice?> FindByIdAsync(Guid invoiceId, CancellationToken cancellationToken) =>
        Task.FromResult(_invoices.SingleOrDefault(invoice => invoice.Id == invoiceId));

    public void Add(Invoice invoice) => _invoices.Add(invoice);

    public void ExpectVersion(Invoice invoice, uint expectedVersion)
    {
        ExpectedVersions[invoice.Id] = expectedVersion;

        if (ConflictingVersion == expectedVersion)
        {
            throw new ConcurrencyConflictException();
        }
    }
}

/// <summary>
/// Counts per year like the real sequence does, so tests can assert literal numbers without a
/// database in the room.
/// </summary>
internal sealed class FakeInvoiceNumberGenerator : IInvoiceNumberGenerator
{
    private readonly Dictionary<int, long> _counters = [];

    public Task<string> NextAsync(int year, CancellationToken cancellationToken)
    {
        long next = _counters.TryGetValue(year, out long current) ? current + 1 : 1;
        _counters[year] = next;
        return Task.FromResult(string.Create(CultureInfo.InvariantCulture, $"{year}-{next:D6}"));
    }
}
