using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Gyms.Events;
using GymOS.SharedKernel.Rules;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

public sealed class GymBrandingTests
{
    private static readonly Guid GymId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void CreateEmpty_HasNothingSet()
    {
        // A gym with no branding renders the platform default, which is the correct behaviour on day
        // one — nobody uploads a logo before they have signed up.
        GymBranding branding = GymBranding.CreateEmpty(GymId);

        Assert.Equal(GymId, branding.GymId);
        Assert.Null(branding.LogoUrl);
        Assert.Null(branding.FaviconUrl);
        Assert.Null(branding.PrimaryColour);
        Assert.Null(branding.SecondaryColour);
        Assert.Null(branding.CustomDomain);
        Assert.Null(branding.EmailSenderName);
        Assert.Empty(branding.DomainEvents);
    }

    [Fact]
    public void Update_StoresTheBrandingAndRaisesBrandingUpdated()
    {
        GymBranding branding = GymBranding.CreateEmpty(GymId);

        branding.Update(
            "https://cdn.gymos.test/logo.png",
            "https://cdn.gymos.test/favicon.ico",
            "#1A2B3C",
            "#FFFFFF",
            "Acme.COM",
            "Acme Gym");

        Assert.Equal("https://cdn.gymos.test/logo.png", branding.LogoUrl);
        Assert.Equal("#1A2B3C", branding.PrimaryColour);
        Assert.Equal("Acme Gym", branding.EmailSenderName);

        // Lower-cased, because hostnames are case-insensitive and the uniqueness index is not.
        Assert.Equal("acme.com", branding.CustomDomain);

        var updated = Assert.IsType<GymBrandingUpdated>(Assert.Single(branding.DomainEvents));
        Assert.Equal(GymId, updated.GymId);
    }

    [Fact]
    public void Update_ReplacesWholesaleRatherThanMerging()
    {
        GymBranding branding = GymBranding.CreateEmpty(GymId);
        branding.Update("logo.png", "favicon.ico", "#1A2B3C", "#FFFFFF", "acme.com", "Acme Gym");

        branding.Update(null, null, "#000000", null, null, null);

        // Declarative: the caller sends the state they want, so two editors converge instead of
        // composing into something neither chose.
        Assert.Null(branding.LogoUrl);
        Assert.Null(branding.FaviconUrl);
        Assert.Equal("#000000", branding.PrimaryColour);
        Assert.Null(branding.SecondaryColour);
        Assert.Null(branding.CustomDomain);
        Assert.Null(branding.EmailSenderName);
    }

    [Fact]
    public void Update_TreatsWhitespaceAsNotSet()
    {
        GymBranding branding = GymBranding.CreateEmpty(GymId);

        branding.Update("   ", "", "   ", null, "  ", " ");

        // Storing an empty string would make "has a custom domain?" answer yes for a gym that has none.
        Assert.Null(branding.LogoUrl);
        Assert.Null(branding.FaviconUrl);
        Assert.Null(branding.PrimaryColour);
        Assert.Null(branding.CustomDomain);
        Assert.Null(branding.EmailSenderName);
    }

    [Theory]
    [InlineData("not-a-colour", null, "gyms.colour_invalid")]
    [InlineData(null, "#12345", "gyms.colour_invalid")]
    public void Update_RejectsAColourAStylesheetWouldNotRender(
        string? primary,
        string? secondary,
        string expectedCode)
    {
        GymBranding branding = GymBranding.CreateEmpty(GymId);

        BusinessRuleViolationException violation = Assert.ThrowsAny<BusinessRuleViolationException>(
            () => branding.Update(null, null, primary, secondary, null, null));

        Assert.Equal(expectedCode, violation.Code);
    }

    [Fact]
    public void Update_RejectsACustomDomainThatIsNotAHostname()
    {
        GymBranding branding = GymBranding.CreateEmpty(GymId);

        BusinessRuleViolationException violation = Assert.ThrowsAny<BusinessRuleViolationException>(
            () => branding.Update(null, null, null, null, "https://acme.com/gym", null));

        Assert.Equal("gyms.custom_domain_invalid", violation.Code);
    }

    [Fact]
    public void Update_LeavesNothingChangedWhenARuleIsBroken()
    {
        GymBranding branding = GymBranding.CreateEmpty(GymId);
        branding.Update("logo.png", null, "#1A2B3C", null, "acme.com", "Acme Gym");
        branding.DequeueDomainEvents();

        Assert.ThrowsAny<BusinessRuleViolationException>(
            () => branding.Update("new-logo.png", null, "nonsense", null, "acme.com", "Acme Gym"));

        // Rules are checked before any assignment, so a rejected update is not a partial one.
        Assert.Equal("logo.png", branding.LogoUrl);
        Assert.Equal("#1A2B3C", branding.PrimaryColour);
        Assert.Empty(branding.DomainEvents);
    }
}
