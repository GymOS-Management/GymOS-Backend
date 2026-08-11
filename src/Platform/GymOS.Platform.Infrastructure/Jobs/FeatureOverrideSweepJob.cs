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
        "{Withdrawn} exception(s) withdrawn.")]
    private static partial void LogSwept(ILogger logger, int gymCount, int withdrawn);

    [LoggerMessage(Level = LogLevel.Error, Message =
        "Feature override sweep step failed for gym {GymId}; its exceptions are unchanged and the " +
        "next sweep retries it.")]
    private static partial void LogGymFailed(ILogger logger, Guid gymId, Exception exception);

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

                // Committed state, read explicitly, because there is no pending change here to
                // derive it from instead (see the type's remarks).
                GymSubscription? live = await subscriptions.FindLiveByGymAsync(gymId, cancellationToken);
                Guid? livePlanId = live?.PlanId;

                int count = await maintainer.WithdrawRedundantAsync(gymId, livePlanId, cancellationToken);

                if (count > 0)
                {
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                    withdrawn += count;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // host shutdown mid-sweep: stop cleanly, the next tick resumes
            }
            catch (Exception exception)
            {
                LogGymFailed(_logger, gymId, exception);
            }
        }

        LogSwept(_logger, gyms.Count, withdrawn);

        return withdrawn;
    }

    public async Task ExecuteAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        await ExecuteAndCountAsync(cancellationToken);
}
