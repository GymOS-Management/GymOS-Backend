namespace GymOS.Platform.Application.Analytics.Abstractions;

/// <summary>An amount with its currency, because a bare decimal has no meaning across a wire (R4).</summary>
public sealed record MoneyDto(decimal Amount, string Currency);

/// <summary>
/// What is true right now (§3.2). Read live from the operational tables: AD-8's read model exists so
/// dashboards never scan <em>tenant</em> tables, and these counts are O(gyms). A summary served from
/// yesterday's row would not be a trade-off, it would be wrong.
/// </summary>
public sealed record DashboardSummary(
    int TotalGyms,
    int ActiveGyms,
    int SuspendedGyms,
    int ProvisioningGyms,
    int LiveSubscriptions,
    IReadOnlyList<MoneyDto> Mrr);

public sealed record HealthCheckStatus(string Name, string Status);

public sealed record DashboardHealth(string Status, IReadOnlyList<HealthCheckStatus> Checks);

public sealed record ActivityEntry(
    DateTimeOffset OccurredAtUtc, string Action, string EntityType, string? UserEmail);

/// <summary>
/// Live reads for the dashboard (§3.2). Everything here queries the operational tables at request
/// time — nothing reads AD-8's daily read model, which exists for the analytics *series*, not this.
/// </summary>
public interface IDashboardQueries
{
    Task<DashboardSummary> GetSummaryAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ActivityEntry>> GetRecentActivityAsync(int limit, CancellationToken cancellationToken);
}
