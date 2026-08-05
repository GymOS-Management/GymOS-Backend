using System.Text.RegularExpressions;
using GymOS.SharedKernel.Rules;

namespace GymOS.Platform.Domain.Gyms.Rules;

/// <summary>
/// Value-level rules for a gym, as rule objects (<see cref="IBusinessRule"/>). Each is named, carries a
/// stable code, and is tested on its own.
/// </summary>
public static partial class GymRules
{
    /// <summary>
    /// A DNS label: lowercase alphanumerics and single hyphens, starting and ending alphanumeric.
    /// </summary>
    [GeneratedRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", RegexOptions.CultureInvariant)]
    internal static partial Regex SlugPattern();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant)]
    internal static partial Regex HexColourPattern();

    [GeneratedRegex(
        "^(?!-)[a-z0-9-]{1,63}(?<!-)(\\.(?!-)[a-z0-9-]{1,63}(?<!-))+$",
        RegexOptions.CultureInvariant)]
    internal static partial Regex HostnamePattern();
}

/// <summary>
/// The gym's URL-safe identifier.
///
/// <para>Constrained to a DNS label because a slug is not only a URL segment: the approved design gives
/// gyms a custom domain, and a slug is the natural default subdomain. A slug that cannot be a hostname
/// is a slug that has to be re-chosen the day subdomains are switched on — after members have bookmarked
/// it.</para>
///
/// <para>Lowercase is required rather than corrected. Silently lowercasing means the operator who typed
/// "Acme-Gym" sees "acme-gym" appear without being told why, and then reports it as a bug.</para>
/// </summary>
public sealed record GymSlugMustBeUrlSafe(string? Slug) : IBusinessRule
{
    public const int MinimumLength = 3;
    public const int MaximumLength = 63;

    public string Code => "gyms.slug_invalid";

    public string Message =>
        $"'{Slug}' is not a valid slug. Use {MinimumLength}–{MaximumLength} characters: lowercase " +
        "letters, digits, and single hyphens, starting and ending with a letter or digit.";

    public bool IsBroken() =>
        string.IsNullOrWhiteSpace(Slug) ||
        Slug.Length < MinimumLength ||
        Slug.Length > MaximumLength ||
        Slug.Contains("--", StringComparison.Ordinal) ||
        !GymRules.SlugPattern().IsMatch(Slug);
}

/// <summary>ISO 3166-1 alpha-2, upper case.</summary>
public sealed record CountryCodeMustBeIso(string? CountryCode) : IBusinessRule
{
    public string Code => "gyms.country_code_invalid";

    public string Message => $"'{CountryCode}' is not a two-letter ISO 3166-1 country code.";

    public bool IsBroken() =>
        string.IsNullOrWhiteSpace(CountryCode) ||
        CountryCode.Length != 2 ||
        !CountryCode.All(char.IsAsciiLetterUpper);
}

/// <summary>ISO 4217 alpha-3, upper case.</summary>
public sealed record CurrencyCodeMustBeIso(string? Currency) : IBusinessRule
{
    public string Code => "gyms.currency_invalid";

    public string Message => $"'{Currency}' is not a three-letter ISO 4217 currency code.";

    public bool IsBroken() =>
        string.IsNullOrWhiteSpace(Currency) ||
        Currency.Length != 3 ||
        !Currency.All(char.IsAsciiLetterUpper);
}

/// <summary>
/// An IANA time-zone identifier (AD-41 — tenants store IANA ids).
///
/// <para>Checked for shape only, not resolved against the machine's time-zone database. A domain rule
/// that consults the host's tz data would give different answers on different machines, and a gym would
/// become invalid because a container image was older. Resolvability is verified by the platform time
/// service at the edge, where a missing zone is an environment problem rather than a rejected form.</para>
/// </summary>
public sealed record TimeZoneMustLookLikeIana(string? TimeZoneId) : IBusinessRule
{
    public string Code => "gyms.timezone_invalid";

    public string Message =>
        $"'{TimeZoneId}' is not an IANA time-zone identifier, such as 'Asia/Nicosia' or 'UTC'.";

    public bool IsBroken()
    {
        if (string.IsNullOrWhiteSpace(TimeZoneId))
        {
            return true;
        }

        if (string.Equals(TimeZoneId, "UTC", StringComparison.Ordinal))
        {
            return false;
        }

        // Region/Location, e.g. Asia/Nicosia, America/Argentina/Cordoba.
        string[] parts = TimeZoneId.Split('/');

        return parts.Length is < 2 or > 3 ||
            parts.Any(part => part.Length == 0 || part.Any(char.IsWhiteSpace));
    }
}

/// <summary>A hex colour, so branding cannot store something a stylesheet will not render.</summary>
public sealed record ColourMustBeHex(string Field, string? Colour) : IBusinessRule
{
    public string Code => "gyms.colour_invalid";

    public string Message => $"{Field} must be a hex colour such as #1A2B3C, but was '{Colour}'.";

    // Null is permitted: branding colours are optional and fall back to the platform default.
    public bool IsBroken() => Colour is not null && !GymRules.HexColourPattern().IsMatch(Colour);
}

/// <summary>
/// A custom domain, when one is set. Optional, and validated only for shape — whether the gym actually
/// controls the domain is a DNS verification step, not a business rule.
/// </summary>
public sealed record CustomDomainMustBeAHostname(string? Domain) : IBusinessRule
{
    public string Code => "gyms.custom_domain_invalid";

    public string Message => $"'{Domain}' is not a valid hostname.";

    public bool IsBroken() =>
        Domain is not null &&
        (Domain.Length > 253 || !GymRules.HostnamePattern().IsMatch(Domain));
}

/// <summary>An email address for the gym's owner, checked as shallowly as everywhere else.</summary>
public sealed record OwnerEmailMustBeWellFormed(string? Email) : IBusinessRule
{
    public string Code => "gyms.owner_email_invalid";

    public string Message => $"'{Email}' is not a valid email address.";

    public bool IsBroken()
    {
        if (string.IsNullOrWhiteSpace(Email))
        {
            return true;
        }

        string[] parts = Email.Split('@');

        return parts.Length != 2 ||
            parts[0].Length == 0 ||
            parts[1].Length == 0 ||
            !parts[1].Contains('.', StringComparison.Ordinal) ||
            Email.Any(char.IsWhiteSpace);
    }
}
