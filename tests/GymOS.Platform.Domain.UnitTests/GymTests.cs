using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Gyms.Events;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Rules;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

public sealed class GymTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 16, 0, 0, TimeSpan.Zero);

    private static Gym CreateGym() => Gym.Provision(
        "acme-gym",
        "Acme Gym",
        new GymOwnerContact("Ada Owner", "ada@acme.test", "+357 99 123456"),
        new GymLocale("CY", "Asia/Nicosia", "EUR"));

    private static Gym CreateActiveGym()
    {
        Gym gym = CreateGym();
        gym.Activate();
        gym.DequeueDomainEvents();
        return gym;
    }

    [Fact]
    public void Provision_StartsInProvisioning_AndRaisesGymProvisioned()
    {
        Gym gym = CreateGym();

        Assert.Equal(GymStatus.Provisioning, gym.Status);
        Assert.False(gym.IsOperational);
        Assert.False(gym.IsDeleted);
        Assert.Null(gym.DeletedAtUtc);

        var provisioned = Assert.IsType<GymProvisioned>(Assert.Single(gym.DomainEvents));
        Assert.Equal(gym.Id, provisioned.GymId);
        Assert.Equal("acme-gym", provisioned.Slug);
    }

    [Fact]
    public void Provision_NormalizesTheOwnerContact()
    {
        Gym gym = Gym.Provision(
            "acme-gym",
            "  Acme Gym  ",
            new GymOwnerContact("  Ada Owner ", "  Ada@Acme.TEST ", "   "),
            new GymLocale("CY", "Asia/Nicosia", "EUR"));

        Assert.Equal("Acme Gym", gym.Name);
        Assert.Equal("Ada Owner", gym.OwnerName);
        Assert.Equal("ada@acme.test", gym.OwnerEmail);

        // A whitespace-only phone is "not given", not a phone number made of spaces.
        Assert.Null(gym.Phone);
    }

    [Theory]
    [InlineData("Acme-Gym", "gyms.slug_invalid")]      // upper case is reported, not corrected
    [InlineData("ac", "gyms.slug_invalid")]            // too short
    [InlineData("-acme", "gyms.slug_invalid")]         // leading hyphen is not a DNS label
    [InlineData("acme--gym", "gyms.slug_invalid")]     // doubled hyphen
    [InlineData("acme gym", "gyms.slug_invalid")]      // space
    public void Provision_RejectsASlugThatCannotBeAHostname(string slug, string expectedCode)
    {
        BusinessRuleViolationException violation = Assert.ThrowsAny<BusinessRuleViolationException>(
            () => Gym.Provision(
                slug,
                "Acme Gym",
                new GymOwnerContact("Ada", "ada@acme.test", null),
                new GymLocale("CY", "Asia/Nicosia", "EUR")));

        Assert.IsAssignableFrom<DomainException>(violation);
        Assert.Equal(expectedCode, violation.Code);
    }

    [Theory]
    [InlineData("CYP", "Asia/Nicosia", "EUR", "gyms.country_code_invalid")]
    [InlineData("CY", "Nicosia", "EUR", "gyms.timezone_invalid")]
    [InlineData("CY", "Asia/Nicosia", "EURO", "gyms.currency_invalid")]
    public void Provision_RejectsALocaleThatIsNotIso(
        string countryCode,
        string timeZoneId,
        string currency,
        string expectedCode)
    {
        BusinessRuleViolationException violation = Assert.ThrowsAny<BusinessRuleViolationException>(
            () => Gym.Provision(
                "acme-gym",
                "Acme Gym",
                new GymOwnerContact("Ada", "ada@acme.test", null),
                new GymLocale(countryCode, timeZoneId, currency)));

        Assert.Equal(expectedCode, violation.Code);
    }

    [Fact]
    public void Provision_RejectsAMalformedOwnerEmail()
    {
        BusinessRuleViolationException violation = Assert.ThrowsAny<BusinessRuleViolationException>(
            () => Gym.Provision(
                "acme-gym",
                "Acme Gym",
                new GymOwnerContact("Ada", "not-an-email", null),
                new GymLocale("CY", "Asia/Nicosia", "EUR")));

        Assert.Equal("gyms.owner_email_invalid", violation.Code);
    }

    [Fact]
    public void Provision_RequiresAName() =>
        Assert.Throws<ArgumentException>(() => Gym.Provision(
            "acme-gym",
            "   ",
            new GymOwnerContact("Ada", "ada@acme.test", null),
            new GymLocale("CY", "Asia/Nicosia", "EUR")));

    [Fact]
    public void Activate_MakesTheGymOperational()
    {
        Gym gym = CreateGym();
        gym.DequeueDomainEvents();

        gym.Activate();

        Assert.Equal(GymStatus.Active, gym.Status);
        Assert.True(gym.IsOperational);
        Assert.IsType<GymActivated>(Assert.Single(gym.DomainEvents));
    }

    [Fact]
    public void Suspend_RecordsTheReasonAndStopsOperation()
    {
        Gym gym = CreateActiveGym();

        gym.Suspend("  Non-payment, invoice 1042  ");

        Assert.Equal(GymStatus.Suspended, gym.Status);
        Assert.False(gym.IsOperational);
        Assert.Equal("Non-payment, invoice 1042", gym.SuspensionReason);

        var suspended = Assert.IsType<GymSuspended>(Assert.Single(gym.DomainEvents));
        Assert.Equal("Non-payment, invoice 1042", suspended.Reason);
    }

    [Fact]
    public void Suspend_RequiresAReason() =>
        // A suspension nobody can explain later is a support ticket.
        Assert.Throws<ArgumentException>(() => CreateActiveGym().Suspend("  "));

    [Fact]
    public void Reactivate_ClearsTheSuspensionReason()
    {
        Gym gym = CreateActiveGym();
        gym.Suspend("Non-payment");
        gym.DequeueDomainEvents();

        gym.Reactivate();

        Assert.Equal(GymStatus.Active, gym.Status);
        Assert.Null(gym.SuspensionReason);
        Assert.IsType<GymReactivated>(Assert.Single(gym.DomainEvents));
    }

    [Fact]
    public void Reactivate_IsRefusedForAGymThatIsNotSuspended() =>
        Assert.ThrowsAny<DomainException>(() => CreateActiveGym().Reactivate());

    [Fact]
    public void ProvisioningCanBeSuspendedBeforeTheGymEverWentLive()
    {
        // A deal that fell through during setup.
        Gym gym = CreateGym();

        gym.Suspend("Contract not signed");

        Assert.Equal(GymStatus.Suspended, gym.Status);
    }

    [Fact]
    public void ASuspendedGymThatNeverWentLiveReactivatesToActive()
    {
        Gym gym = CreateGym();
        gym.Suspend("Contract not signed");

        gym.Reactivate();

        // Unlike an invited administrator, there is no "never used" state to return to: a gym's
        // provisioning is finished by the operator, and reactivating is that same decision.
        Assert.Equal(GymStatus.Active, gym.Status);
    }

    [Fact]
    public void Delete_StampsTheTimeAndCarriesTheSlugOnTheEvent()
    {
        Gym gym = CreateActiveGym();

        gym.Delete(Now);

        Assert.Equal(GymStatus.Deleted, gym.Status);
        Assert.True(gym.IsDeleted);
        Assert.False(gym.IsOperational);
        Assert.Equal(Now, gym.DeletedAtUtc);

        var deleted = Assert.IsType<GymDeleted>(Assert.Single(gym.DomainEvents));
        Assert.Equal("acme-gym", deleted.Slug);
    }

    [Fact]
    public void Delete_IsTerminal()
    {
        Gym gym = CreateActiveGym();
        gym.Delete(Now);

        Assert.ThrowsAny<DomainException>(() => gym.Activate());
        Assert.ThrowsAny<DomainException>(() => gym.Suspend("Anything"));
        Assert.ThrowsAny<DomainException>(() => gym.Delete(Now));
    }

    [Fact]
    public void UpdateDetails_IsRefusedForADeletedGym()
    {
        Gym gym = CreateActiveGym();
        gym.Delete(Now);

        Assert.ThrowsAny<DomainException>(() => gym.UpdateDetails(
            "Renamed",
            new GymOwnerContact("Ada", "ada@acme.test", null),
            new GymLocale("CY", "Asia/Nicosia", "EUR")));
    }

    [Fact]
    public void UpdateDetails_ChangesEverythingExceptTheSlug()
    {
        Gym gym = CreateActiveGym();
        string slugBefore = gym.Slug;

        gym.UpdateDetails(
            "Acme Fitness",
            new GymOwnerContact("Grace Owner", "grace@acme.test", "+357 99 654321"),
            GymLocale.Of("gr", "Europe/Athens", "eur"));

        Assert.Equal(slugBefore, gym.Slug);
        Assert.Equal("Acme Fitness", gym.Name);
        Assert.Equal("grace@acme.test", gym.OwnerEmail);
        Assert.Equal("GR", gym.CountryCode);
        Assert.Equal("Europe/Athens", gym.TimeZoneId);
        Assert.Equal("EUR", gym.Currency);
        Assert.IsType<GymDetailsUpdated>(Assert.Single(gym.DomainEvents));
    }

    [Fact]
    public void UpdateDetails_DoesNotChangeTheStatus()
    {
        Gym gym = CreateActiveGym();
        gym.Suspend("Non-payment");

        gym.UpdateDetails(
            "Acme Fitness",
            new GymOwnerContact("Ada", "ada@acme.test", null),
            new GymLocale("CY", "Asia/Nicosia", "EUR"));

        // Editing a suspended gym's details is allowed — a support agent correcting an owner's email
        // during a billing dispute is exactly when it happens — and it does not lift the suspension.
        Assert.Equal(GymStatus.Suspended, gym.Status);
        Assert.Equal("Non-payment", gym.SuspensionReason);
    }

    [Fact]
    public void GymLocale_UpperCasesTheCodesSoAnOperatorTypingLowerCaseIsNotRejected()
    {
        GymLocale locale = GymLocale.Of(" cy ", " Asia/Nicosia ", " eur ");

        Assert.Equal("CY", locale.CountryCode);
        Assert.Equal("Asia/Nicosia", locale.TimeZoneId);
        Assert.Equal("EUR", locale.Currency);
    }
}
