using System.ComponentModel.DataAnnotations;

namespace GymOS.Platform.Infrastructure.Persistence.Auditing;

/// <summary>
/// How far ahead monthly audit partitions are created (configuration section
/// <c>Auditing:Partitions</c>).
/// </summary>
public sealed class AuditLogPartitionOptions
{
    public const string SectionName = "Auditing:Partitions";

    /// <summary>
    /// Months of partitions to keep ahead of the current one.
    ///
    /// The value is a bet on deployment cadence: maintenance runs at startup, so this is how
    /// long the platform can go without a restart before audit rows start landing in the
    /// DEFAULT partition. Three months is deliberately generous — the cost of an unused empty
    /// partition is a catalog row, and the cost of running out is misfiled audit history.
    /// </summary>
    [Range(1, 24)]
    public int MonthsAhead { get; set; } = 3;

    /// <summary>
    /// Set false to skip maintenance at startup — for hosts that must not touch DDL, or where
    /// a scheduled job owns it instead.
    /// </summary>
    public bool RunOnStartup { get; set; } = true;
}
