using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.Platform.Application.Invoices;
using GymOS.Platform.Application.Invoices.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GymOS.Platform.Infrastructure.Jobs;

/// <summary>
/// The overdue sweep (M3.4 design §4): issued invoices whose due date passed become
/// <c>Overdue</c>, and a current period going unpaid takes its subscription to PastDue in the
/// same per-invoice transaction. Named in the frozen folder plan (ARCHITECTURE.md,
/// <c>BackgroundJobs/</c>); same isolation shape as <see cref="RenewalJob"/>.
/// </summary>
internal sealed partial class InvoiceOverdueJob : IScheduledJob
{
    private readonly IInvoiceQueries _invoiceQueries;
    private readonly IDatabaseClock _databaseClock;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InvoiceOverdueJob> _logger;

    [LoggerMessage(Level = LogLevel.Information, Message =
        "Overdue sweep processed {DueCount} invoice(s): {MarkedOverdue} marked overdue, of which " +
        "{SubscriptionsPastDue} took their subscription past due; {Failed} failed.")]
    private static partial void LogSweepFinished(
        ILogger logger, int dueCount, int markedOverdue, int subscriptionsPastDue, int failed);

    [LoggerMessage(Level = LogLevel.Error, Message =
        "Overdue step failed for invoice {InvoiceId}; its state is unchanged and the next sweep " +
        "retries it.")]
    private static partial void LogStepFailed(ILogger logger, Guid invoiceId, Exception exception);

    public InvoiceOverdueJob(
        IInvoiceQueries invoiceQueries,
        IDatabaseClock databaseClock,
        IServiceScopeFactory scopeFactory,
        ILogger<InvoiceOverdueJob> logger)
    {
        _invoiceQueries = invoiceQueries;
        _databaseClock = databaseClock;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public string Name => "invoice-overdue";

    /// <summary>
    /// Hourly: due dates carry day precision (issue date + 14 days), so an hour's granularity is
    /// already generous — the read models keep deriving overdue live in the gap.
    /// </summary>
    public JobSchedule Schedule => JobSchedule.Every(TimeSpan.FromHours(1));

    public async Task ExecuteAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Durable verdicts are measured with the database's clock, not this host's — branding a
        // punctual customer Overdue has no undo transition, so the guard in MarkOverdue must be
        // fed a `now` that means something (see IDatabaseClock).
        DateTimeOffset databaseNow = await _databaseClock.UtcNowAsync(cancellationToken);

        IReadOnlyList<Guid> due = await _invoiceQueries.ListNewlyOverdueAsync(databaseNow, cancellationToken);
        if (due.Count == 0)
        {
            return;
        }

        int markedOverdue = 0, subscriptionsPastDue = 0, failed = 0;

        foreach (Guid invoiceId in due)
        {
            try
            {
                using IServiceScope scope = _scopeFactory.CreateScope();
                OverdueAction action = await scope.ServiceProvider
                    .GetRequiredService<InvoiceOverdueService>()
                    .ProcessAsync(invoiceId, databaseNow, cancellationToken);

                if (action is not OverdueAction.None)
                {
                    markedOverdue++;
                }

                if (action is OverdueAction.MarkedOverdueAndSubscriptionPastDue)
                {
                    subscriptionsPastDue++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                LogStepFailed(_logger, invoiceId, exception);
            }
        }

        LogSweepFinished(_logger, due.Count, markedOverdue, subscriptionsPastDue, failed);
    }
}
