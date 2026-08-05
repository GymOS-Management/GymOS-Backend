using GymOS.Platform.Domain.Gyms.Rules;
using GymOS.SharedKernel.Entities;
using GymOS.SharedKernel.Rules;

namespace GymOS.Platform.Domain.Gyms;

/// <summary>
/// How a gym appears to its members (ARCHITECTURE.md §2.2) — logo, colours, sender name, and optionally
/// its own domain.
///
/// <para>Its own aggregate rather than fields on <see cref="Gym"/>, for two operational reasons: branding
/// is edited by different people at different times than the gym's commercial details, and every field
/// here is optional while none of the gym's are. Keeping them apart means a branding change does not
/// appear in the audit trail as a change to the gym's ownership.</para>
///
/// <para>Every value is optional. A gym with no branding renders the platform default, which is the
/// correct behaviour on day one — nobody uploads a logo before they have signed up.</para>
/// </summary>
public sealed class GymBranding : AggregateRoot, IAuditable
{
    private GymBranding(Guid id, Guid gymId)
        : base(id) => GymId = gymId;

    private GymBranding()
    {
    }

    public Guid GymId { get; private set; }

    /// <summary>Reference to a validated file (AD-44), never a caller-supplied URL to somewhere else.</summary>
    public string? LogoUrl { get; private set; }

    public string? FaviconUrl { get; private set; }

    /// <summary>Hex, e.g. <c>#1A2B3C</c>. Null falls back to the platform default.</summary>
    public string? PrimaryColour { get; private set; }

    public string? SecondaryColour { get; private set; }

    /// <summary>
    /// The gym's own domain, unique across the platform when set. Shape is validated here; whether the
    /// gym controls it is a DNS verification step that belongs to the hosting workflow, not to this
    /// aggregate.
    /// </summary>
    public string? CustomDomain { get; private set; }

    /// <summary>The "from" name on member email. Not the address — that is platform-controlled.</summary>
    public string? EmailSenderName { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>
    /// Creates the empty branding record that accompanies a new gym, so the detail view and the update
    /// endpoint always have something to read and write rather than needing a create-or-update path.
    /// </summary>
    public static GymBranding CreateEmpty(Guid gymId) => new(Guid.NewGuid(), gymId);

    /// <summary>
    /// Replaces the branding wholesale. Declarative rather than field-by-field: the caller sends the
    /// state they want, so two editors converge instead of composing into something neither chose —
    /// the same reasoning as replacing a user's role set.
    /// </summary>
    public void Update(
        string? logoUrl,
        string? faviconUrl,
        string? primaryColour,
        string? secondaryColour,
        string? customDomain,
        string? emailSenderName)
    {
        string? normalizedDomain = Normalize(customDomain)?.ToLowerInvariant();
        string? normalizedPrimary = Normalize(primaryColour);
        string? normalizedSecondary = Normalize(secondaryColour);

        BusinessRules.CheckAll(
            new ColourMustBeHex("Primary colour", normalizedPrimary),
            new ColourMustBeHex("Secondary colour", normalizedSecondary),
            new CustomDomainMustBeAHostname(normalizedDomain));

        LogoUrl = Normalize(logoUrl);
        FaviconUrl = Normalize(faviconUrl);
        PrimaryColour = normalizedPrimary;
        SecondaryColour = normalizedSecondary;
        CustomDomain = normalizedDomain;
        EmailSenderName = Normalize(emailSenderName);

        Raise(new Events.GymBrandingUpdated(GymId));
    }

    /// <summary>
    /// Empty and whitespace both mean "not set". Storing an empty string would make "has a custom
    /// domain?" answer yes for a gym that has none.
    /// </summary>
    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
