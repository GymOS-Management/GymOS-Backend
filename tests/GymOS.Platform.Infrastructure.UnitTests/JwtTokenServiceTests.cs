using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Infrastructure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Xunit;

namespace GymOS.Platform.Infrastructure.UnitTests;

/// <summary>
/// Access-token issuing (AD-4). Assertions read the emitted token rather than the
/// service's inputs — what the client receives is the contract.
/// </summary>
public sealed class JwtTokenServiceTests
{
    private static readonly DateTimeOffset IssuedAt = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheTokenCarriesTheClaimsAd4Requires()
    {
        var request = new AccessTokenRequest(
            Guid.NewGuid(),
            "admin@gymos.test",
            Guid.NewGuid(),
            ["users.read", "users.manage"],
            IssuedAt);

        JsonWebToken token = Decode(CreateService().Issue(request).Token);

        Assert.Equal(request.UserId.ToString(), Claim(token, GymOSClaims.Subject));
        Assert.Equal(request.Email, Claim(token, GymOSClaims.Email));
        Assert.Equal(request.SecurityStamp.ToString(), Claim(token, GymOSClaims.SecurityStamp));
        Assert.Equal(
            JwtTokenService.ComputePermissionsHash(request.Permissions),
            Claim(token, GymOSClaims.PermissionsHash));

        // The permission set itself is not in the token — only its fingerprint, so a
        // user with hundreds of grants does not carry them on every request.
        Assert.DoesNotContain(
            token.Claims,
            claim => claim.Value.Contains("users.manage", StringComparison.Ordinal));
    }

    [Fact]
    public void IssuerAudienceAndLifetimeComeFromConfiguration()
    {
        JwtTokenService service = CreateService(options =>
        {
            options.Issuer = "gymos-test-issuer";
            options.Audience = "gymos-test-audience";
            options.AccessTokenLifetime = TimeSpan.FromMinutes(15);
        });

        IssuedAccessToken issued = service.Issue(Request());
        JsonWebToken token = Decode(issued.Token);

        Assert.Equal("gymos-test-issuer", token.Issuer);
        Assert.Equal("gymos-test-audience", Assert.Single(token.Audiences));
        Assert.Equal(IssuedAt.AddMinutes(15), issued.ExpiresAtUtc);
        Assert.Equal(issued.ExpiresAtUtc.UtcDateTime, token.ValidTo);
    }

    [Fact]
    public void ExpiryIsReportedAsTheTokenActuallyEncodesIt()
    {
        // JWT time claims are integer seconds (RFC 7519). Reporting the requested
        // instant while encoding a floored one would hand the client an expiry it
        // cannot rely on at the boundary.
        JwtTokenService service = CreateService(options => options.AccessTokenLifetime = TimeSpan.FromMinutes(15));
        DateTimeOffset withSubSecond = IssuedAt.AddMilliseconds(750);

        IssuedAccessToken issued = service.Issue(Request(issuedAt: withSubSecond));

        Assert.Equal(0, issued.ExpiresAtUtc.Millisecond);
        Assert.Equal(Decode(issued.Token).ValidTo, issued.ExpiresAtUtc.UtcDateTime);
    }

    [Fact]
    public void TheTokenIsNotValidBeforeItWasIssued()
    {
        JsonWebToken token = Decode(CreateService().Issue(Request()).Token);

        Assert.Equal(IssuedAt.UtcDateTime, token.ValidFrom);
    }

    [Fact]
    public void PermissionsHash_IsIndependentOfOrderAndDuplicates()
    {
        // Row order from a join is not stable. If it changed the hash, clients would
        // see phantom permission changes and re-read /auth/me for no reason.
        string first = JwtTokenService.ComputePermissionsHash(["b.read", "a.read", "b.read"]);
        string second = JwtTokenService.ComputePermissionsHash(["a.read", "b.read"]);

        Assert.Equal(first, second);
    }

    [Fact]
    public void PermissionsHash_ChangesWhenThePermissionSetChanges()
    {
        string before = JwtTokenService.ComputePermissionsHash(["users.read"]);
        string after = JwtTokenService.ComputePermissionsHash(["users.read", "users.manage"]);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void PermissionsHash_OfAnEmptySet_IsStable()
    {
        Assert.Equal(
            JwtTokenService.ComputePermissionsHash([]),
            JwtTokenService.ComputePermissionsHash([]));
    }

    private static AccessTokenRequest Request(DateTimeOffset? issuedAt = null) => new(
        Guid.NewGuid(),
        "admin@gymos.test",
        Guid.NewGuid(),
        ["users.read"],
        issuedAt ?? IssuedAt);

    private static JwtTokenService CreateService(Action<JwtOptions>? configure = null)
    {
        var options = new JwtOptions
        {
            Issuer = "gymos-backend",
            Audience = "gymos-clients",
            SigningKey = "test-signing-key-at-least-32-bytes-long-for-hmac-sha256",
        };

        configure?.Invoke(options);
        return new JwtTokenService(Options.Create(options));
    }

    private static JsonWebToken Decode(string token) => new JsonWebTokenHandler().ReadJsonWebToken(token);

    private static string? Claim(JsonWebToken token, string type) =>
        token.Claims.SingleOrDefault(claim => claim.Type == type)?.Value;
}
