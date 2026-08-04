using GymOS.BuildingBlocks.Infrastructure.Auditing;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Infrastructure.Persistence.Auditing;
using Xunit;

namespace GymOS.Platform.Infrastructure.UnitTests;

/// <summary>
/// What the audit trail records, and — the part worth guarding hardest — what it must never
/// record. A password hash written here would sit in a table designed to be read by humans
/// and retained for years.
/// </summary>
public sealed class AuditPolicyTests
{
    private readonly AuditPolicy _policy = new(PlatformAuditedEntities.All);

    [Fact]
    public void TheDeclaredEntitiesAreAudited()
    {
        Assert.True(_policy.IsAudited(typeof(User)));
        Assert.True(_policy.IsAudited(typeof(Role)));
        Assert.True(_policy.IsAudited(typeof(UserRole)));
        Assert.True(_policy.IsAudited(typeof(RolePermission)));
    }

    [Fact]
    public void HighVolumeAndSelfReferentialEntitiesAreDeliberatelyNotAudited()
    {
        // RefreshToken churns on every sign-in and rotation; auditing it would bury the
        // administrative actions the log exists for. Sessions are recorded as `auth.*`
        // entries instead. Auditing AuditLog itself has no reader.
        Assert.False(_policy.IsAudited(typeof(RefreshToken)));
        Assert.False(_policy.IsAudited(typeof(AuditLog)));
    }

    [Fact]
    public void AuditingIsOptIn_SoAnUndeclaredTypeIsNeverCaptured()
    {
        Assert.False(_policy.IsAudited(typeof(Permission)));
        Assert.Null(_policy.For(typeof(string)));
    }

    [Theory]
    [InlineData(nameof(User.PasswordHash))]
    [InlineData(nameof(User.SecurityStamp))]
    public void CredentialsOnADeclaredEntityAreRedacted(string propertyName)
    {
        Assert.True(_policy.ShouldRedact(typeof(User), propertyName));
    }

    [Theory]
    [InlineData("PasswordResetTokenHash")]
    [InlineData("ApiKey")]
    [InlineData("SmtpSecret")]
    [InlineData("SigningKey")]
    [InlineData("PrivateKey")]
    [InlineData("RecoveryCredential")]
    public void SensitiveLookingPropertiesAreRedactedWithoutBeingDeclared(string propertyName)
    {
        // The point of the heuristic: a field added years from now is masked by default,
        // rather than depending on whoever adds it remembering this file exists.
        Assert.True(_policy.ShouldRedact(typeof(User), propertyName));
    }

    [Theory]
    [InlineData(nameof(User.Email))]
    [InlineData(nameof(User.FirstName))]
    [InlineData(nameof(User.Status))]
    [InlineData(nameof(User.FailedLoginAttempts))]
    public void OrdinaryPropertiesAreRecordedInFull(string propertyName)
    {
        // A trail that redacted everything would be useless; the diff has to stay readable.
        Assert.False(_policy.ShouldRedact(typeof(User), propertyName));
    }

    [Fact]
    public void AnExplicitExemptionOverridesTheHeuristic()
    {
        var policy = new AuditPolicy([
            AuditedEntity.Of<User>("user", neverRedacted: ["TokenCount"]),
        ]);

        // The escape hatch for a name the heuristic flags wrongly. It wins, because the
        // declaration is a decision and the pattern is only a guess.
        Assert.False(policy.ShouldRedact(typeof(User), "TokenCount"));
        Assert.True(policy.ShouldRedact(typeof(User), "TokenValue"));
    }

    [Fact]
    public void RedactionMarkerIsPresentRatherThanOmitted()
    {
        // The field must still appear in the diff so a reader can see it changed, without
        // seeing what it changed to.
        Assert.False(string.IsNullOrWhiteSpace(AuditPolicy.RedactedMarker));
    }

    [Fact]
    public void AuditedEntityNamesAreStableAndDecoupledFromClrNames()
    {
        // entity_type is written into permanent rows; deriving it from the class name would
        // mean a refactor silently splits history in two.
        AuditedEntity user = Assert.Single(PlatformAuditedEntities.All, entity => entity.ClrType == typeof(User));
        Assert.Equal("user", user.Name);
    }
}
