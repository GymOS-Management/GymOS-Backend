using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.Platform.Application.Features.Abstractions;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Features;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GymOS.Platform.Infrastructure.Jobs;

/// <summary>
/// The nightly guarantee for the two triggers too wide to withdraw inline (M4.1 task 3): a plan
/// changing its own feature opinion, and a platform default flipping. Both can make every gym on
/// that plan (or every gym with no subscription, for a default) hold a now-redundant exception, and
/// doing that withdrawal synchronously inside the plan/default edit would turn a single billing
/// write into an unbounded one. <see cref="IFeatureExceptionMaintainer"/> already knows how to judge
/// one gym; this job is only "which gyms, and one transaction per gym" (the <see cref="RenewalJob"/>
/// shape).
///
/// <para><b>Candidates are the gyms that hold at least one exception</b> — a small population by
/// construction (most gyms hold none, per <see cref="IFeatureExceptionMaintainer"/>'s own fast
/// path), and the only ones a sweep could possibly change.</para>
///
/// <para><b>Reads each candidate's live plan straight from the database</b>, unlike the three
/// synchronous callers <see cref="IFeatureExceptionMaintainer"/> otherwise has: those callers sit
/// inside a transaction that has mutated a subscription in memory but not yet saved it, so a query
/// would see stale or invisible state (see that interface's remarks). This job runs standalone,
/// between transactions, over whatever the previous request already committed — there is no
/// in-flight mutation to derive the answer from, so asking <see cref="ISubscriptionRepository"/> is
/// not a workaround, it is the only source of truth available here.</para>
/// </summary>
internal sealed partial class FeatureOverrideSweepJob : IScheduledJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AppDbContext _dbContext;
    private readonly ILogger<FeatureOverrideSweepJob> _logger;

    [LoggerMessage(Level = LogLevel.Information, Message =
        "Feature override sweep checked {GymCount} gym(s) holding an exception: " +
        "{Withdrawn} exception(s) withdrawn, {Deferred} gym(s) deferred to an operator's own edit, " +
        "{Failed} gym(s) failed.")]
    private static partial void LogSwept(
        ILogger logger, int gymCount, int withdrawn, int deferred, int failed);

    [LoggerMessage(Level = LogLevel.Error, Message =
        "Feature override sweep step failed for gym {GymId}; its exceptions are unchanged and the " +
        "next sweep retries it.")]
    private static partial void LogGymFailed(ILogger logger, Guid gymId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message =
        "Feature override sweep deferred re-evaluation for gym {GymId}: an operator changed its " +
        "exceptions underneath the sweep, so their decision stands and the next sweep re-evaluates.")]
    private static partial void LogGymChangedUnderneath(ILogger logger, Guid gymId);

    public FeatureOverrideSweepJob(
        IServiceScopeFactory scopeFactory,
        AppDbContext dbContext,
        ILogger<FeatureOverrideSweepJob> logger)
    {
        _scopeFactory = scopeFactory;
        _dbContext = dbContext;
        _logger = logger;
    }

    public string Name => "feature-override-sweep";

    /// <summary>After RetentionJob (03:10) and PlatformStatsJob (03:30), so the night's work queues
    /// rather than contending.</summary>
    public JobSchedule Schedule => JobSchedule.DailyAtUtc(new TimeOnly(3, 50));

    /// <summary>
    /// Exposed separately from <see cref="ExecuteAsync"/> so a caller (the tests, chiefly) can see
    /// how many exceptions a run withdrew — the number that makes the idempotence guarantee
    /// checkable: one on a first pass over a genuine trigger, zero on every pass after.
    /// </summary>
    public async Task<int> ExecuteAndCountAsync(CancellationToken cancellationToken)
    {
        List<Guid> gyms = await _dbContext.Set<GymFeatureOverride>()
            .AsNoTracking()
            .Select(over => over.GymId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var withdrawn = 0;
        var deferred = 0;
        var failed = 0;

        foreach (Guid gymId in gyms)
        {
            try
            {
                // One scope and one transaction per gym: a gym whose withdrawal fails must not
                // abandon the sweep for every gym after it (RenewalJob's reasoning).
                await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
                var subscriptions = scope.ServiceProvider.GetRequiredService<ISubscriptionRepository>();
                var maintainer = scope.ServiceProvider.GetRequiredService<IFeatureExceptionMaintainer>();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                // The transaction the comment above has always promised, and which nothing actually
                // opened until now. Without it the live-plan read, the maintainer's reads and the
                // DELETE were separate implicit transactions under READ COMMITTED: a subscription
                // upgrade committing between them let a verdict computed against the OLD plan delete
                // an exception that is not redundant under the new one — silently, because the
                // override rows' xmin never saw the subscription change, so no concurrency error
                // fired. RenewalService is the model this job's own docs cite, and it does exactly
                // this: open a transaction, read the row that decides under a lock, then commit.
                await using IUnitOfWorkTransaction transaction =
                    await unitOfWork.BeginTransactionAsync(cancellationToken);

                // Committed state, read explicitly, because there is no pending change here to
                // derive it from instead (see the type's remarks). Read FOR UPDATE inside the
                // transaction so a console edit racing this sweep waits its turn rather than winning
                // at save time — again RenewalService's pattern, and the reason
                // FindLiveByGymForUpdateAsync exists.
                GymSubscription? live = await subscriptions.FindLiveByGymForUpdateAsync(gymId, cancellationToken);
                Guid? livePlanId = live?.PlanId;

                int count = await maintainer.WithdrawRedundantAsync(gymId, livePlanId, cancellationToken);

                if (count > 0)
                {
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                    withdrawn += count;
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // host shutdown mid-sweep: stop cleanly, the next tick resumes
            }
            catch (ConcurrencyConflictException)
            {
                // An expected interleaving, not a failure. GymFeatureOverride carries an xmin
                // token, so an operator restating an override between this sweep's read and its
                // delete makes the delete match nothing. That is the design working — the
                // operator's decision wins and the next sweep re-evaluates against it — and
                // reporting it at Error would page someone for correct behaviour. Caught before
                // the general handler below precisely so it never reaches that channel.
                //
                // ConcurrencyConflictException, NOT DbUpdateConcurrencyException. This catch named
                // the EF type for two days and was therefore unreachable: UnitOfWork.SaveChangesAsync
                // translates the EF exception at the persistence boundary and its own documentation
                // says the EF type "never reaches the Application layer". The fix that added this
                // handler could never fire, so the race it exists to demote kept being logged at
                // Error — and nothing failed, because no test asserted either the level or the type.
                // A_stale_override_delete_surfaces_as_ConcurrencyConflictException_not_the_EF_type
                // now pins the type that actually crosses that boundary.
                deferred++;
                LogGymChangedUnderneath(_logger, gymId);
            }
            catch (Exception exception)
            {
                failed++;
                LogGymFailed(_logger, gymId, exception);
            }
        }

        // The summary carries every outcome, not only the happy one. Reporting just "swept N gyms,
        // withdrew M" read healthy on a pass where gyms failed, which is how a job with a broken
        // handler looks fine in the log — RenewalJob carries its failure count for the same reason.
        LogSwept(_logger, gyms.Count, withdrawn, deferred, failed);

        return withdrawn;
    }

    public async Task ExecuteAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        await ExecuteAndCountAsync(cancellationToken);
}
