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

    /// <summary>
    /// How often the job is meant to run: the interval, or one day for a daily schedule. The
    /// monitoring read side measures staleness in multiples of this.
    /// </summary>
    public abstract TimeSpan Period { get; }

    /// <summary>A short human description for a console row: "every 15m", "daily at 03:10 UTC".</summary>
    public abstract string Describe();

    private sealed record IntervalSchedule(TimeSpan Interval) : JobSchedule
    {
        public override DateTimeOffset NextAfter(DateTimeOffset nowUtc) => nowUtc + Interval;

        public override TimeSpan Period => Interval;

        public override string Describe()
        {
            if (Interval < TimeSpan.FromMinutes(1))
            {
                return $"every {(int)Interval.TotalSeconds}s";
            }

            int hours = (int)Interval.TotalHours;
            int minutes = Interval.Minutes;

            return (hours, minutes) switch
            {
                (0, _) => $"every {minutes}m",
                (_, 0) => $"every {hours}h",
                _ => $"every {hours}h {minutes}m",
            };
        }
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

        public override TimeSpan Period => TimeSpan.FromDays(1);

        public override string Describe() => $"daily at {TimeOfDayUtc:HH\\:mm} UTC";
    }
}
