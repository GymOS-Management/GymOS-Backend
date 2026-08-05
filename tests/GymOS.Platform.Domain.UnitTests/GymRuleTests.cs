using GymOS.Platform.Domain.Gyms.Rules;
using GymOS.TestingSupport;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

/// <summary>
/// The gym's value rules, each on its own. The codes are API surface — clients branch on them — so they
/// are asserted, not just the verdicts.
/// </summary>
public sealed class GymRuleTests
{
    [Theory]
    [InlineData("acme-gym")]
    [InlineData("acme")]
    [InlineData("a1b")]
    [InlineData("acme-gym-nicosia-city-centre")]
    public void AValidSlugIsAccepted(string slug) =>
        BusinessRuleAssert.Satisfied(new GymSlugMustBeUrlSafe(slug));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ab")]                  // shorter than the minimum
    [InlineData("Acme")]                // upper case
    [InlineData("-acme")]               // leading hyphen
    [InlineData("acme-")]               // trailing hyphen
    [InlineData("acme--gym")]           // doubled hyphen
    [InlineData("acme_gym")]            // underscore is not a DNS label character
    [InlineData("acme.gym")]            // a dot would make it more than one label
    [InlineData("acme gym")]
    public void AnInvalidSlugIsRejected(string? slug) =>
        BusinessRuleAssert.Broken(new GymSlugMustBeUrlSafe(slug), "gyms.slug_invalid");

    [Fact]
    public void ASlugLongerThanADnsLabelIsRejected() =>
        BusinessRuleAssert.Broken(new GymSlugMustBeUrlSafe(new string('a', 64)), "gyms.slug_invalid");

    [Fact]
    public void ASlugExactlyAtTheLimitIsAccepted() =>
        BusinessRuleAssert.Satisfied(new GymSlugMustBeUrlSafe(new string('a', 63)));

    [Theory]
    [InlineData("CY")]
    [InlineData("GB")]
    public void AnIsoCountryCodeIsAccepted(string code) =>
        BusinessRuleAssert.Satisfied(new CountryCodeMustBeIso(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cy")]                  // the caller upper-cases before validating
    [InlineData("CYP")]
    [InlineData("C1")]
    public void ANonIsoCountryCodeIsRejected(string? code) =>
        BusinessRuleAssert.Broken(new CountryCodeMustBeIso(code), "gyms.country_code_invalid");

    [Theory]
    [InlineData("EUR")]
    [InlineData("GBP")]
    public void AnIsoCurrencyCodeIsAccepted(string code) =>
        BusinessRuleAssert.Satisfied(new CurrencyCodeMustBeIso(code));

    [Theory]
    [InlineData(null)]
    [InlineData("eur")]
    [InlineData("EU")]
    [InlineData("EURO")]
    [InlineData("EU1")]
    public void ANonIsoCurrencyCodeIsRejected(string? code) =>
        BusinessRuleAssert.Broken(new CurrencyCodeMustBeIso(code), "gyms.currency_invalid");

    [Theory]
    [InlineData("UTC")]
    [InlineData("Asia/Nicosia")]
    [InlineData("Europe/Athens")]
    [InlineData("America/Argentina/Cordoba")]
    public void AnIanaTimeZoneIsAccepted(string timeZoneId) =>
        BusinessRuleAssert.Satisfied(new TimeZoneMustLookLikeIana(timeZoneId));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Nicosia")]                         // no region
    [InlineData("Asia/")]                           // empty location
    [InlineData("Asia/Nicosia/City/Centre")]        // too many parts
    [InlineData("Asia/Nico sia")]                   // whitespace
    [InlineData("GMT Standard Time")]               // a Windows id, not IANA (AD-41)
    public void ANonIanaTimeZoneIsRejected(string? timeZoneId) =>
        BusinessRuleAssert.Broken(new TimeZoneMustLookLikeIana(timeZoneId), "gyms.timezone_invalid");

    [Theory]
    [InlineData("#1A2B3C")]
    [InlineData("#abcdef")]
    [InlineData(null)]                              // optional: falls back to the platform default
    public void AValidColourIsAccepted(string? colour) =>
        BusinessRuleAssert.Satisfied(new ColourMustBeHex("Primary colour", colour));

    [Theory]
    [InlineData("1A2B3C")]                          // missing #
    [InlineData("#1A2B3")]                          // too short
    [InlineData("#1A2B3CD")]
    [InlineData("#GGGGGG")]
    [InlineData("rebeccapurple")]                   // a CSS name is not a hex colour
    public void AnInvalidColourIsRejected(string colour) =>
        BusinessRuleAssert.Broken(new ColourMustBeHex("Primary colour", colour), "gyms.colour_invalid");

    [Theory]
    [InlineData("acme.com")]
    [InlineData("gym.acme.co.uk")]
    [InlineData(null)]                              // optional
    public void AValidCustomDomainIsAccepted(string? domain) =>
        BusinessRuleAssert.Satisfied(new CustomDomainMustBeAHostname(domain));

    [Theory]
    [InlineData("acme")]                            // a single label is not a domain
    [InlineData("-acme.com")]
    [InlineData("acme-.com")]
    [InlineData("acme..com")]
    [InlineData("https://acme.com")]                // a URL, not a hostname
    [InlineData("acme.com/gym")]
    [InlineData("Acme.com")]                        // the caller lower-cases before validating
    public void AnInvalidCustomDomainIsRejected(string domain) =>
        BusinessRuleAssert.Broken(new CustomDomainMustBeAHostname(domain), "gyms.custom_domain_invalid");

    [Fact]
    public void ACustomDomainLongerThanADnsNameIsRejected()
    {
        string tooLong = string.Join('.', Enumerable.Repeat(new string('a', 63), 5));

        BusinessRuleAssert.Broken(
            new CustomDomainMustBeAHostname(tooLong),
            "gyms.custom_domain_invalid");
    }

    [Theory]
    [InlineData("ada@acme.test")]
    [InlineData("ada.owner+billing@acme.co.uk")]
    public void AWellFormedOwnerEmailIsAccepted(string email) =>
        BusinessRuleAssert.Satisfied(new OwnerEmailMustBeWellFormed(email));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ada")]
    [InlineData("ada@")]
    [InlineData("@acme.test")]
    [InlineData("ada@acme")]                        // no dot in the domain
    [InlineData("ada@@acme.test")]
    [InlineData("ada owner@acme.test")]
    public void AMalformedOwnerEmailIsRejected(string? email) =>
        BusinessRuleAssert.Broken(new OwnerEmailMustBeWellFormed(email), "gyms.owner_email_invalid");

    [Fact]
    public void EveryGymRuleCodeFollowsTheConvention() =>
        // The whole catalog at once, so the naming cannot drift one rule at a time.
        BusinessRuleAssert.CodesFollowConvention(
            new GymSlugMustBeUrlSafe("x"),
            new CountryCodeMustBeIso("CY"),
            new CurrencyCodeMustBeIso("EUR"),
            new TimeZoneMustLookLikeIana("UTC"),
            new ColourMustBeHex("Primary colour", "#1A2B3C"),
            new CustomDomainMustBeAHostname("acme.com"),
            new OwnerEmailMustBeWellFormed("ada@acme.test"));
}
