using GymOS.Platform.Domain.Invoices;
using GymOS.SharedKernel;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

/// <summary>
/// The invoice lifecycle as a matrix, exhaustively — every ordered pair is either allowed or refused,
/// so the two states nothing sets yet (Draft, Overdue) cannot become quietly reachable when their
/// producers arrive.
/// </summary>
public sealed class InvoiceStatusTransitionTests
{
    public static TheoryData<InvoiceStatus, InvoiceStatus> AllowedTransitions() => new()
    {
        { InvoiceStatus.Issued, InvoiceStatus.Paid },
        { InvoiceStatus.Issued, InvoiceStatus.Void },
    };

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public void AllowedTransitionsAreAccepted(InvoiceStatus from, InvoiceStatus to)
    {
        Assert.True(InvoiceStatusTransitions.IsAllowed(from, to));
        InvoiceStatusTransitions.EnsureAllowed(from, to); // must not throw
    }

    [Fact]
    public void EveryPairOutsideTheAllowListIsRefused()
    {
        var allowed = AllowedTransitions()
            .Select(row => ((InvoiceStatus)row[0], (InvoiceStatus)row[1]))
            .ToHashSet();

        var statuses = Enum.GetValues<InvoiceStatus>();

        foreach (InvoiceStatus from in statuses)
        {
            foreach (InvoiceStatus to in statuses)
            {
                if (allowed.Contains((from, to)))
                {
                    continue;
                }

                Assert.False(InvoiceStatusTransitions.IsAllowed(from, to));
                Assert.Throws<DomainException>(() => InvoiceStatusTransitions.EnsureAllowed(from, to));
            }
        }
    }

    [Theory]
    [InlineData(InvoiceStatus.Paid)]
    [InlineData(InvoiceStatus.Void)]
    public void ASettledInvoiceIsTerminal(InvoiceStatus terminal)
    {
        // An invoice is a financial record: correcting one is a credit note, not an edit. Nothing may
        // move out of Paid or Void.
        foreach (InvoiceStatus target in Enum.GetValues<InvoiceStatus>())
        {
            Assert.False(InvoiceStatusTransitions.IsAllowed(terminal, target));
        }
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft)]
    [InlineData(InvoiceStatus.Overdue)]
    public void TheStatesWithNoProducerYetGoNowhere(InvoiceStatus unreachable)
    {
        // Draft waits on manual invoicing; Overdue waits on the M3.4 job. Until something sets them,
        // no transition out of them exists either — the day one gains a producer, its moves are added
        // here deliberately rather than found to be already open.
        foreach (InvoiceStatus target in Enum.GetValues<InvoiceStatus>())
        {
            Assert.False(InvoiceStatusTransitions.IsAllowed(unreachable, target));
            Assert.False(InvoiceStatusTransitions.IsAllowed(target, unreachable));
        }
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft)]
    [InlineData(InvoiceStatus.Issued)]
    [InlineData(InvoiceStatus.Paid)]
    [InlineData(InvoiceStatus.Overdue)]
    [InlineData(InvoiceStatus.Void)]
    public void ATransitionToTheCurrentStateIsRefused(InvoiceStatus status) =>
        Assert.Throws<DomainException>(() => InvoiceStatusTransitions.EnsureAllowed(status, status));

    [Fact]
    public void StatusNumbersAreTheOnesTheDatabaseStores()
    {
        // Stored as smallint. Renumbering would reinterpret every existing invoice — the one kind of
        // row a business is legally required to be able to reproduce years later.
        Assert.Equal(1, (int)InvoiceStatus.Draft);
        Assert.Equal(2, (int)InvoiceStatus.Issued);
        Assert.Equal(3, (int)InvoiceStatus.Paid);
        Assert.Equal(4, (int)InvoiceStatus.Overdue);
        Assert.Equal(5, (int)InvoiceStatus.Void);
    }
}
