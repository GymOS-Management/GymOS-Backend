using GymOS.Platform.Infrastructure.Identity;
using Xunit;

namespace GymOS.Platform.Infrastructure.UnitTests;

public sealed class RefreshTokenGeneratorTests
{
    private readonly RefreshTokenGenerator _generator = new();

    [Fact]
    public void EveryTokenIsUnique()
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < 500; i++)
        {
            Assert.True(tokens.Add(_generator.GenerateRawToken()));
        }
    }

    [Fact]
    public void TokensAreUrlSafe_AndCarry256BitsOfEntropy()
    {
        string token = _generator.GenerateRawToken();

        // base64url of 32 bytes: 43 characters, no padding, nothing a cookie or a URL
        // would have to escape.
        Assert.Equal(43, token.Length);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
    }

    [Fact]
    public void HashingIsDeterministic_SoLookupByHashFindsTheRow()
    {
        string token = _generator.GenerateRawToken();

        Assert.Equal(_generator.ComputeHash(token), _generator.ComputeHash(token));
    }

    [Fact]
    public void DifferentTokensHashDifferently()
    {
        Assert.NotEqual(
            _generator.ComputeHash(_generator.GenerateRawToken()),
            _generator.ComputeHash(_generator.GenerateRawToken()));
    }

    [Fact]
    public void TheHashFitsTheTokenHashColumn()
    {
        // token_hash is varchar(128) with a unique index; SHA-256 as lowercase hex is 64.
        string hash = _generator.ComputeHash(_generator.GenerateRawToken());

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
    }

    [Fact]
    public void TheStoredHashIsNotTheTokenItself()
    {
        // The invariant AD-4 rests on: a database dump must not yield a usable credential.
        string token = _generator.GenerateRawToken();

        Assert.NotEqual(token, _generator.ComputeHash(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void HashingNothingIsAProgrammingError(string? rawToken)
    {
        Assert.ThrowsAny<ArgumentException>(() => _generator.ComputeHash(rawToken!));
    }
}
