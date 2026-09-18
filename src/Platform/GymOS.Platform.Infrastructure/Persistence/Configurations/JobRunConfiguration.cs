using GymOS.Platform.Domain.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.Platform.Infrastructure.Persistence.Configurations;

public sealed class JobRunConfiguration : IEntityTypeConfiguration<JobRun>
{
    public void Configure(EntityTypeBuilder<JobRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("job_runs", "platform");
        builder.HasKey(run => run.Id);

        builder.Property(run => run.JobName).HasMaxLength(JobRun.MaxNameLength).IsRequired();
        builder.Property(run => run.Instance).HasMaxLength(JobRun.MaxInstanceLength).IsRequired();
        builder.Property(run => run.Status).HasConversion<byte>();
        builder.Property(run => run.Error).HasMaxLength(JobRun.MaxErrorLength);

        // One index, three readers: the history page (newest first for one job), the newest-run-per-job
        // query behind the overview, and the retention sweep's "everything but the newest".
        builder.HasIndex(run => new { run.JobName, run.StartedAtUtc })
            .IsDescending(false, true)
            .HasDatabaseName("ix_job_runs_name_started");

        // No row version: one writer per row, serialised by IJobLock, and a finished row is never updated.
    }
}
