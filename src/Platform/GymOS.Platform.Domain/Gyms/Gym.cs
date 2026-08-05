using GymOS.Platform.Domain.Gyms.Events;
using GymOS.Platform.Domain.Gyms.Rules;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Entities;
using GymOS.SharedKernel.Rules;

namespace GymOS.Platform.Domain.Gyms;

/// <summary>
/// A gym — which is also a tenant (ARCHITECTURE.md §1). This aggregate lives in the <c>platform</c>
/// schema and carries no <c>tenant_id</c>, because gyms are what tenants *are*: the row is the tenant's
/// definition, not a row belonging to one.
///
/// <para>All time arrives as method parameters (AD-40). Status changes go through one guarded
/// transition (<see cref="GymStatusTransitions"/>).</para>
/// </summary>
public sealed class Gym : AggregateRoot, IAuditable
{
    private Gym(
        Guid id,
        string slug,
        string name,
        GymOwnerContact owner,
        GymLocale locale)
        : base(id)
    {
        Slug = slug;
        Name = name;
        OwnerName = owner.Name;
        OwnerEmail = owner.Email;
        Phone = owner.Phone;
        CountryCode = locale.CountryCode;
        TimeZoneId = locale.TimeZoneId;
        Currency = locale.Currency;
        Status = GymStatus.Provisioning;
    }

    private Gym()
    {
    }

    /// <summary>
    /// URL-safe identifier, unique across the platform and **immutable after creation**.
    ///
    /// Immutable because it is the gym's public address: it appears in URLs members bookmark, in the
    /// step-up confirmation for deletion (AD-6), and — when custom domains arrive — potentially in a
    /// hostname. A mutable slug is a broken link and a confirmation phrase that no longer matches.
    /// </summary>
    public string Slug { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public GymStatus Status { get; private set; }

    /// <summary>Why the gym was suspended; cleared on reactivation.</summary>
    public string? SuspensionReason { get; private set; }

    public string OwnerName { get; private set; } = string.Empty;

    public string OwnerEmail { get; private set; } = string.Empty;

    public string? Phone { get; private set; }

    /// <summary>ISO 3166-1 alpha-2.</summary>
    public string CountryCode { get; private set; } = string.Empty;

    /// <summary>IANA identifier (AD-41). Everything is stored in UTC; this is how it is presented.</summary>
    public string TimeZoneId { get; private set; } = string.Empty;

    /// <summary>ISO 4217 alpha-3. One operating currency per gym (approved scope, AD-42).</summary>
    public string Currency { get; private set; } = string.Empty;

    /// <summary>
    /// When the gym was soft-deleted. The row survives the retention window so that invoices, audit
    /// entries, and any remaining tenant data stay readable; a purge job performs the hard delete.
    /// </summary>
    public DateTimeOffset? DeletedAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public bool IsDeleted => Status == GymStatus.Deleted;

    /// <summary>
    /// Whether the gym may operate right now. Derived, never stored: a flag would need a job to keep
    /// true, and the night the job failed a suspended gym would still be serving.
    /// </summary>
    public bool IsOperational => Status == GymStatus.Active;

    /// <summary>
    /// Creates a gym in <see cref="GymStatus.Provisioning"/>.
    ///
    /// Locale values are required and have **no defaults here** (ARCHITECTURE.md §2.2: "no defaults
    /// hardcoded — from platform settings"). A domain default would be a hidden policy that every gym
    /// silently inherited; the caller supplies them from settings.
    /// </summary>
    public static Gym Provision(string slug, string name, GymOwnerContact owner, GymLocale locale)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(locale);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Normalized before validation, not after: an email pasted with a trailing space is the address
        // the operator meant, and rejecting it as malformed teaches them to distrust the form.
        GymOwnerContact normalizedOwner = owner.Normalized();

        BusinessRules.CheckAll(
            new GymSlugMustBeUrlSafe(slug),
            new OwnerEmailMustBeWellFormed(normalizedOwner.Email),
            new CountryCodeMustBeIso(locale.CountryCode),
            new TimeZoneMustLookLikeIana(locale.TimeZoneId),
            new CurrencyCodeMustBeIso(locale.Currency));

        var gym = new Gym(Guid.NewGuid(), slug, name.Trim(), normalizedOwner, locale);
        gym.Raise(new GymProvisioned(gym.Id, gym.Slug, gym.Name));

        return gym;
    }

    /// <summary>Opens the gym for business.</summary>
    public void Activate()
    {
        TransitionTo(GymStatus.Active);
        SuspensionReason = null;
        Raise(new GymActivated(Id));
    }

    /// <summary>
    /// Changes the gym's details. Slug is absent by design — see <see cref="Slug"/>.
    /// </summary>
    public void UpdateDetails(string name, GymOwnerContact owner, GymLocale locale)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(locale);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        EnsureNotDeleted();

        GymOwnerContact normalized = owner.Normalized();

        BusinessRules.CheckAll(
            new OwnerEmailMustBeWellFormed(normalized.Email),
            new CountryCodeMustBeIso(locale.CountryCode),
            new TimeZoneMustLookLikeIana(locale.TimeZoneId),
            new CurrencyCodeMustBeIso(locale.Currency));

        Name = name.Trim();
        OwnerName = normalized.Name;
        OwnerEmail = normalized.Email;
        Phone = normalized.Phone;
        CountryCode = locale.CountryCode;
        TimeZoneId = locale.TimeZoneId;
        Currency = locale.Currency;

        Raise(new GymDetailsUpdated(Id));
    }

    /// <summary>Withholds service. Reversible, and the reason is recorded and returned by the API.</summary>
    public void Suspend(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        TransitionTo(GymStatus.Suspended);
        SuspensionReason = reason.Trim();

        Raise(new GymSuspended(Id, SuspensionReason));
    }

    /// <summary>Lifts a suspension, returning the gym to Active.</summary>
    public void Reactivate()
    {
        if (Status != GymStatus.Suspended)
        {
            throw new DomainException("Only a suspended gym can be reactivated.");
        }

        TransitionTo(GymStatus.Active);
        SuspensionReason = null;

        Raise(new GymReactivated(Id));
    }

    /// <summary>
    /// Soft-deletes the gym. Terminal: there is no transition back (see
    /// <see cref="GymStatusTransitions"/>).
    /// </summary>
    public void Delete(DateTimeOffset now)
    {
        TransitionTo(GymStatus.Deleted);
        DeletedAtUtc = now;

        // The slug travels with the event so a consumer can release or reserve it without having to
        // load an aggregate that is now deleted.
        Raise(new GymDeleted(Id, Slug));
    }

    private void EnsureNotDeleted()
    {
        if (IsDeleted)
        {
            throw new DomainException("A deleted gym cannot be modified.");
        }
    }

    /// <summary>The only place <see cref="Status"/> is assigned.</summary>
    private void TransitionTo(GymStatus target)
    {
        GymStatusTransitions.EnsureAllowed(Status, target);
        Status = target;
    }
}

/// <summary>
/// Who to contact about the gym. Grouped rather than passed as three loose strings so a caller cannot
/// transpose the name and the email — the kind of mistake that compiles and then emails the wrong person.
/// </summary>
public sealed record GymOwnerContact(string Name, string Email, string? Phone)
{
    public GymOwnerContact Normalized() => new(
        Name.Trim(),
        Email.Trim().ToLowerInvariant(),
        string.IsNullOrWhiteSpace(Phone) ? null : Phone.Trim());
}

/// <summary>
/// Where the gym operates and in what currency. Grouped for the same reason as
/// <see cref="GymOwnerContact"/>: three ISO-ish codes in a row is an invitation to swap two of them.
/// </summary>
public sealed record GymLocale(string CountryCode, string TimeZoneId, string Currency)
{
    /// <summary>
    /// Upper-cases the codes so validation compares one form. Country and currency codes are
    /// case-insensitive by standard, and an operator typing "cy" should not be told it is invalid.
    /// </summary>
    public static GymLocale Of(string? countryCode, string? timeZoneId, string? currency) => new(
        (countryCode ?? string.Empty).Trim().ToUpperInvariant(),
        (timeZoneId ?? string.Empty).Trim(),
        (currency ?? string.Empty).Trim().ToUpperInvariant());
}
