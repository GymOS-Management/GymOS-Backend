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
