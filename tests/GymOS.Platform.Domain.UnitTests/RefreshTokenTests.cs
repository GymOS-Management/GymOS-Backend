using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

public sealed class RefreshTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Issue_IsActiveUntilExpiry()
    {
        RefreshToken token = RefreshToken.Issue(Guid.NewGuid(), "hash", Now.AddDays(7), "10.0.0.1");

        Assert.True(token.IsActive(Now));
        Assert.True(token.IsActive(Now.AddDays(6)));
        Assert.False(token.IsActive(Now.AddDays(8))); // expired
    }

    [Fact]
    public void Revoke_RecordsChainAndDeactivates()
    {
        RefreshToken token = RefreshToken.Issue(Guid.NewGuid(), "hash", Now.AddDays(7), "10.0.0.1");

        token.Revoke(Now.AddHours(1), "10.0.0.2", "rotated", replacedByTokenHash: "next-hash");

        Assert.True(token.IsRevoked);
        Assert.False(token.IsActive(Now.AddHours(2)));
        Assert.Equal("next-hash", token.ReplacedByTokenHash);
        Assert.Equal("rotated", token.RevocationReason);
        Assert.Throws<DomainException>(() => token.Revoke(Now.AddHours(2), "10.0.0.3", "again"));
    }
}
