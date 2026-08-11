using GymOS.Platform.Application.Features;

namespace GymOS.WebApi.Endpoints;

/// <summary>
/// Body of the catalogue edit (§3.5). The key is absent deliberately — it is immutable, and a field
/// a caller can send but the server ignores is worse than no field at all.
/// </summary>
public sealed record UpdateFeatureRequest(
    string Name,
    string? Description,
    string? Category,
    bool DefaultEnabled,
    bool IsPremium);

/// <summary>
/// Body of <c>PUT /plans/{id}/features</c>. Only the keys listed are touched — see the endpoint's
/// description for why partial requests are the safe default here.
/// </summary>
public sealed record SetPlanFeaturesRequest(IReadOnlyList<PlanFeatureRequest> Features);

/// <summary>
/// Body of <c>PUT /gyms/{id}/features</c>. Only the keys listed are touched, and each carries the
/// reason its exception exists — required only when the value actually differs from what the gym's
/// plan gives, because asking for what the gym already gets withdraws an exception rather than
/// creating one.
/// </summary>
public sealed record SetGymFeaturesRequest(IReadOnlyList<GymFeatureRequest> Features);
