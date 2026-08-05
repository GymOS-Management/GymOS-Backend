namespace GymOS.WebApi.Endpoints;

/// <summary>
/// Request bodies for the plan-catalogue endpoints (AD-12: this is the shape generated clients are built
/// from, so it is API surface).
/// </summary>
/// <param name="Code">
/// Upper-case identifier, e.g. <c>PRO_ANNUAL</c>. Permanent — it appears in exports and integrations.
/// </param>
/// <param name="MaxMembers">Ceiling, or null for unlimited. Zero is refused: it is not a sellable plan.</param>
public sealed record CreatePlanRequest(
    string Code,
    string Name,
    string? Description,
    int TrialDays,
    int? MaxMembers,
    int? MaxTrainers,
    int? MaxStaff,
    long? StorageLimitMb,
    int SortOrder);

/// <summary>Code is absent: it is immutable after creation.</summary>
public sealed record UpdatePlanRequest(
    string Name,
    string? Description,
    int TrialDays,
    int? MaxMembers,
    int? MaxTrainers,
    int? MaxStaff,
    long? StorageLimitMb,
    int SortOrder,
    bool IsPublic);

/// <param name="BillingCycle">
/// <c>Monthly</c>, <c>Quarterly</c>, or <c>Yearly</c>. Rejected if unrecognised rather than guessed —
/// billing a gym annually for a monthly plan is not a mistake worth being lenient about.
/// </param>
/// <param name="Currency">ISO 4217, e.g. <c>EUR</c>. Decides how the amount is rounded.</param>
/// <param name="Amount">
/// Rounded to the currency's minor units on the way in: 19.999 becomes 20.00 for EUR and 20 for JPY.
/// </param>
public sealed record SetPlanPriceRequest(string BillingCycle, string Currency, decimal Amount);
