namespace GymOS.Platform.Application.Analytics.Abstractions;

public sealed record RevenuePoint(DateOnly Date, IReadOnlyList<MoneyDto> Mrr);

public sealed record GrowthPoint(
    DateOnly Date, int TotalGyms, int ActiveGyms, int SuspendedGyms, int ProvisioningGyms, int NewGyms);

/// <summary>
/// Churn with its two causes kept apart: <paramref name="Canceled"/> is a decision the customer
/// made, <paramref name="Expired"/> is a payment that failed. <paramref name="Total"/> is offered so
/// no caller has to add them, not so anyone has to choose between them.
/// </summary>
public sealed record ChurnPoint(DateOnly Date, int Canceled, int Expired, int Total);

/// <summary>Read access to the recorded series (§3.8). Only <c>daily_platform_stats</c> is touched.</summary>
public interface IAnalyticsQueries
{
    Task<IReadOnlyList<RevenuePoint>> GetRevenueAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);

    Task<IReadOnlyList<GrowthPoint>> GetGymsGrowthAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChurnPoint>> GetChurnAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);
}
