namespace GymOS.WebApi.Endpoints;

/// <summary>
/// Request bodies for the gym-management endpoints (AD-12: this is the shape the generated clients are
/// built from, so it is API surface).
/// </summary>
/// <param name="Slug">
/// The gym's permanent public address. Lower-case, digits, and hyphens; 3–63 characters. Immutable after
/// creation and never reused, including by a deleted gym.
/// </param>
/// <param name="CountryCode">ISO 3166-1 alpha-2, e.g. <c>CY</c>.</param>
/// <param name="TimeZoneId">IANA identifier, e.g. <c>Asia/Nicosia</c>.</param>
/// <param name="Currency">ISO 4217 alpha-3, e.g. <c>EUR</c>. One operating currency per gym.</param>
public sealed record ProvisionGymRequest(
    string Slug,
    string Name,
    string OwnerName,
    string OwnerEmail,
    string? Phone,
    string CountryCode,
    string TimeZoneId,
    string Currency);

/// <summary>
/// Slug is absent: it is immutable after creation, because it appears in URLs members bookmark and in the
/// confirmation phrase for deletion.
/// </summary>
public sealed record UpdateGymRequest(
    string Name,
    string OwnerName,
    string OwnerEmail,
    string? Phone,
    string CountryCode,
    string TimeZoneId,
    string Currency);

/// <param name="Reason">Required and stored — a suspension nobody can explain later is a support ticket.</param>
public sealed record SuspendGymRequest(string Reason);

/// <summary>
/// The complete branding the gym should end up with, not a delta: an omitted value is cleared. Sending
/// the state you want is what makes two concurrent editors converge on a chosen result.
/// </summary>
/// <param name="PrimaryColour">Hex, e.g. <c>#1A2B3C</c>. Null falls back to the platform default.</param>
/// <param name="CustomDomain">
/// The gym's own domain, unique across the platform. Shape is validated here; whether the gym controls
/// it is a DNS verification step outside this endpoint.
/// </param>
public sealed record UpdateGymBrandingRequest(
    string? LogoUrl,
    string? FaviconUrl,
    string? PrimaryColour,
    string? SecondaryColour,
    string? CustomDomain,
    string? EmailSenderName);
