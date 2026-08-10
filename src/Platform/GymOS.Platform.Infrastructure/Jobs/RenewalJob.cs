using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.Platform.Application.Subscriptions;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GymOS.Platform.Infrastructure.Jobs;

/// <summary>
/// The renewal sweep (M3.4 design §3): finds live subscriptions whose period or trial has run out
/// and hands each to <see cref="SubscriptionRenewalService"/> <b>in its own scope</b>, so every
/// subscription is its own transaction — one gym's failure is logged and isolated, the rest of
/// the sweep continues, and the next tick retries whatever is still due.
/// </summary>
internal sealed partial class RenewalJob : IScheduledJob
{
    private readonly ISubscriptionQueries _subscriptionQueries;
    private readonly IDatabaseClock _databaseClock;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RenewalJob> _logger;

    [LoggerMessage(Level = LogLevel.Information, Message =
        "Renewal sweep processed {DueCount} due subscription(s): {Renewed} renewed, " +
        "{Converted} trial(s) converted, {Finalized} cancellation(s) finalized, {Expired} expired, " +
        "{Failed} failed.")]
    private static partial void LogSweepFinished(
        ILogger logger, int dueCount, int renewed, int converted, int finalized, int expired, int failed);

    [LoggerMessage(Level = LogLevel.Error, Message =
        "Renewal step failed for subscription {SubscriptionId}; its state is unchanged and the " +
        "next sweep retries it.")]
    private static partial void LogStepFailed(ILogger logger, Guid subscriptionId, Exception exception);

    public RenewalJob(
        ISubscriptionQueries subscriptionQueries,
        IDatabaseClock databaseClock,
        IServiceScopeFactory scopeFactory,
        ILogger<RenewalJob> logger)
    {
        _subscriptionQueries = subscriptionQueries;
        _databaseClock = databaseClock;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public string Name => "subscription-renewal";

    /// <summary>
    /// Every 15 minutes: a renewal lands within a quarter hour of its period boundary, which is
    /// invisible next to a monthly cycle, while a failed sweep's retry never lags a business day.
    /// </summary>
    public JobSchedule Schedule => JobSchedule.Every(TimeSpan.FromMinutes(15));

    public async Task ExecuteAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The tick's `now` came from THIS host's clock; the verdicts below are durable and some
        // are terminal, so they are measured with the database's clock instead — a fast host
        // must never expire a subscription or bill a renewal early (see IDatabaseClock).
        DateTimeOffset databaseNow = await _databaseClock.UtcNowAsync(cancellationToken);

        IReadOnlyList<Guid> due = await _subscriptionQueries.ListDueForRenewalAsync(databaseNow, cancellationToken);
        if (due.Count == 0)
        {
            return;
        }

        int renewed = 0, converted = 0, finalized = 0, expired = 0, failed = 0;

        foreach (Guid subscriptionId in due)
        {
            try
            {
                // A fresh scope per subscription: its own DbContext, its own SaveChanges, its own
                // transaction — the isolation the design demands.
                using IServiceScope scope = _scopeFactory.CreateScope();
                RenewalAction action = await scope.ServiceProvider
                    .GetRequiredService<SubscriptionRenewalService>()
                    .ProcessAsync(subscriptionId, databaseNow, cancellationToken);

                switch (action)
                {
                    case RenewalAction.Renewed or RenewalAction.AppliedPendingChangeAndRenewed:
                        renewed++;
                        break;
                    case RenewalAction.ConvertedTrial:
                        converted++;
                        break;
                    case RenewalAction.FinalizedCancellation:
                        finalized++;
                        break;
                    case RenewalAction.Expired:
                        expired++;
                        break;
                    case RenewalAction.None:
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // host shutdown mid-sweep: stop cleanly, the next tick resumes
            }
            catch (Exception exception)
            {
                failed++;
                LogStepFailed(_logger, subscriptionId, exception);
            }
        }

        LogSweepFinished(_logger, due.Count, renewed, converted, finalized, expired, failed);
    }
}
