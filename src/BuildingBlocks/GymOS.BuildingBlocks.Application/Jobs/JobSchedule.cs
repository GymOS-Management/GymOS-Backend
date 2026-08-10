namespace GymOS.BuildingBlocks.Application.Jobs;

/// <summary>
/// When a job runs — a closed set of shapes, deliberately not cron.
///
/// <para>The spec's folder plan says "IHostedService + cron"; the <em>shape</em> — a schedule the
/// host evaluates — is satisfied here with an expression language of exactly two forms, because
/// every job the platform has needs one of them and a cron parser is a dependency-policy decision
/// nobody has to make today (Hangfire is explicitly banned pending an ADR; a cron library can be
/// added the day a job genuinely needs "third Tuesday at dawn").</para>
///
/// <para>UTC only, like everything else (AD-40): a daily time is a UTC time, so schedule
/// arithmetic never meets a DST transition.</para>
/// </summary>
public abstract record JobSchedule
{
    private JobSchedule()
    {
    }

    /// <summary>Runs the job at a fixed interval, measured from each run's scheduled time.</summary>
    public static JobSchedule Every(TimeSpan interval)
    {
        return interval > TimeSpan.Zero
            ? new IntervalSchedule(interval)
            : throw new ArgumentOutOfRangeException(
                nameof(interval), interval, "A job interval must be positive.");
    }

    /// <summary>Runs the job once a day at the given UTC time of day.</summary>
    public static JobSchedule DailyAtUtc(TimeOnly timeOfDayUtc) => new DailySchedule(timeOfDayUtc);

    /// <summary>The first due time strictly after <paramref name="nowUtc"/>.</summary>
    public abstract DateTimeOffset NextAfter(DateTimeOffset nowUtc);

    private sealed record IntervalSchedule(TimeSpan Interval) : JobSchedule
    {
        public override DateTimeOffset NextAfter(DateTimeOffset nowUtc) => nowUtc + Interval;
    }

    private sealed record DailySchedule(TimeOnly TimeOfDayUtc) : JobSchedule
    {
        public override DateTimeOffset NextAfter(DateTimeOffset nowUtc)
        {
            DateTimeOffset utc = nowUtc.ToUniversalTime();
            DateTimeOffset todayAt = new(DateOnly.FromDateTime(utc.UtcDateTime), TimeOfDayUtc, TimeSpan.Zero);

            // Strictly after: a job asking at exactly its own due time gets tomorrow's slot,
            // never an immediate second run.
            return todayAt > utc ? todayAt : todayAt.AddDays(1);
        }
    }
}
