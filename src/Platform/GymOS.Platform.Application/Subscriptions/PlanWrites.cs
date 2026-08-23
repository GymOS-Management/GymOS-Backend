using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel.Currencies;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Subscriptions;

/// <summary>
/// The steps every plan-write handler shares — the precondition, the read-back, and turning the
/// caller's strings into domain values. Collected so a new plan endpoint cannot omit one.
/// </summary>
internal static class PlanWrites
{
    /// <summary>
    /// Applies the caller's <c>If-Match</c>. Required on a PUT, which overwrites what the caller read;
    /// honoured but not demanded on an action, whose own state checks already refuse the unsafe
    /// interleavings (API_STANDARDS.md §3).
    /// </summary>
    public static Result ApplyExpectedVersion(
        IPlanRepository plans,
        SubscriptionPlan plan,
        string? expectedVersion,
        bool versionRequired)
    {
        if (string.IsNullOrWhiteSpace(expectedVersion))
        {
            return versionRequired ? Result.Failure(PlanErrors.VersionRequired) : Result.Success();
        }

        if (!RowVersion.TryParse(expectedVersion, out ulong parsed))
        {
            return Result.Failure(PlanErrors.MalformedVersion);
        }

        plans.ExpectVersion(plan, parsed);
        return Result.Success();
    }

    /// <summary>
    /// Parses a billing cycle by name. Strict, unlike a list filter: a price is being written, and
    /// guessing which cycle an unrecognised word meant is how a gym gets billed annually for a monthly
    /// plan.
    /// </summary>
    public static Result<BillingCycle> ParseBillingCycle(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out BillingCycle cycle) && Enum.IsDefined(cycle)
            ? Result.Success(cycle)
            : Result.Failure<BillingCycle>(PlanErrors.UnknownBillingCycle(value ?? string.Empty));

    /// <summary>Parses an ISO 4217 code into a <see cref="Currency"/>, which knows its minor units.</summary>
    public static Result<Currency> ParseCurrency(string? value) =>
        Currency.TryOf(value, out Currency currency)
            ? Result.Success(currency)
            : Result.Failure<Currency>(PlanErrors.InvalidCurrency(value ?? string.Empty));

    /// <summary>
    /// Reads the plan back after a write, so the response matches a following GET — including the new
    /// row version the caller needs for their next edit.
    /// </summary>
    public static async Task<Result<PlanDetail>> ReadBackAsync(
        IPlanQueries planQueries,
        Guid planId,
        CancellationToken cancellationToken)
    {
        PlanDetail? detail = await planQueries.GetAsync(planId, cancellationToken);

        return detail is null
            ? Result.Failure<PlanDetail>(PlanErrors.NotFound(planId))
            : Result.Success(detail);
    }
}
