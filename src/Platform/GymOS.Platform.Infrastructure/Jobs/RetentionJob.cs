using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.Platform.Infrastructure.Idempotency;
using GymOS.Platform.Infrastructure.Identity;
using GymOS.Platform.Infrastructure.Persistence.Auditing;

namespace GymOS.Platform.Infrastructure.Jobs;

/// <summary>
/// The recurring half of data retention (AD-23), on the schedule the two startup maintainers were
/// always waiting for — STATUS.md's standing instruction to fold them in once the job runner
/// landed, executed.
///
/// <para>The startup calls stay: first boot on an empty database still needs audit partitions
/// before the first request, and a process that starts once a month should not wait a day to
/// purge. This job is what makes retention independent of how often the process restarts.</para>
///
/// <para>Both halves are already idempotent and non-fatal by construction (they log and carry on;
/// see each maintainer), which is exactly the <see cref="IScheduledJob"/> contract: a missed or
/// failed run leaves state the next run still recognizes as due. Partition <em>detachment</em> —
/// the other half of the retention policy — waits on the counsel-supplied retention window, and
/// belongs here when it arrives.</para>
/// </summary>
internal sealed class RetentionJob : IScheduledJob
{
    private readonly AuditLogPartitionMaintainer _auditPartitions;
    private readonly IdempotencyRecordMaintainer _idempotencyRecords;
    private readonly RefreshTokenMaintainer _refreshTokens;
    private readonly ActivationTokenMaintainer _activationTokens;

    public RetentionJob(
        AuditLogPartitionMaintainer auditPartitions,
        IdempotencyRecordMaintainer idempotencyRecords,
        RefreshTokenMaintainer refreshTokens,
        ActivationTokenMaintainer activationTokens)
    {
        _auditPartitions = auditPartitions;
        _idempotencyRecords = idempotencyRecords;
        _refreshTokens = refreshTokens;
        _activationTokens = activationTokens;
    }

    public string Name => "retention";

    /// <summary>Nightly, off the hour to stay clear of on-the-hour crowds like backups.</summary>
    public JobSchedule Schedule => JobSchedule.DailyAtUtc(new TimeOnly(3, 10));

    public async Task ExecuteAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _auditPartitions.EnsurePartitionsAsync(cancellationToken);
        await _idempotencyRecords.PurgeExpiredAsync(cancellationToken);

        // The purge IRefreshTokenRepository always documented: rotation inserts a row per refresh,
        // and the session-ending paths load every token a user ever held.
        await _refreshTokens.PurgeUnusableAsync(cancellationToken);

        // Same shape, same reason (ADR-53): every invitation, reset and re-send leaves a row, and
        // spent credentials must not accumulate forever.
        await _activationTokens.PurgeExpiredAsync(cancellationToken);
    }
}
