using GymOS.Platform.Infrastructure.Identity;
using Microsoft.Extensions.Options;
using Xunit;

namespace GymOS.Platform.Infrastructure.UnitTests;

/// <summary>
/// Argon2id hashing (AD-4). Cost parameters are held at their floor throughout: these
/// tests verify format and logic, and paying production-grade memory cost per assertion
/// would make the suite slow without testing anything extra.
/// </summary>
public sealed class Argon2PasswordHasherTests
{
    private const string Password = "correct-horse-battery-staple";

    [Fact]
    public void AHashVerifiesAgainstItsOwnPassword()
    {
        Argon2PasswordHasher hasher = CreateHasher();

        string hash = hasher.Hash(Password);

        Assert.True(hasher.Verify(Password, hash));
    }

    [Fact]
    public void AWrongPasswordDoesNotVerify()
    {
        Argon2PasswordHasher hasher = CreateHasher();

        string hash = hasher.Hash(Password);

        Assert.False(hasher.Verify(Password + "!", hash));
        Assert.False(hasher.Verify(Password.ToUpperInvariant(), hash));
        Assert.False(hasher.Verify(string.Empty, hash));
    }

    [Fact]
    public void AnEmptyPasswordIsHandled_NotThrown()
    {
        // Reachable from an unauthenticated request: the sign-in handler hashes whatever
        // was posted — empty included — to keep unknown-account timing flat. Throwing
        // here would turn `{"email":"x","password":""}` into a 500.
        Argon2PasswordHasher hasher = CreateHasher();

        string hashOfEmpty = hasher.Hash(string.Empty);

        Assert.StartsWith("$argon2id$", hashOfEmpty, StringComparison.Ordinal);
        Assert.False(hasher.Verify(string.Empty, hasher.Hash(Password)));
    }

    [Fact]
    public void TheSamePasswordHashedTwice_ProducesDifferentHashes()
    {
        Argon2PasswordHasher hasher = CreateHasher();

        // A fresh random salt per hash: identical passwords must not produce identical
        // rows, or a database dump reveals which users share a password.
        Assert.NotEqual(hasher.Hash(Password), hasher.Hash(Password));
    }

    [Fact]
    public void HashesAreStoredInPhcFormat_CarryingTheirOwnParameters()
    {
        Argon2PasswordHasher hasher = CreateHasher(options =>
        {
            options.MemorySizeKib = 8192;
            options.Iterations = 3;
            options.DegreeOfParallelism = 2;
        });

        string hash = hasher.Hash(Password);

        Assert.StartsWith("$argon2id$v=19$m=8192,t=3,p=2$", hash, StringComparison.Ordinal);
        Assert.Equal(6, hash.Split('$').Length);
    }

    [Fact]
    public void RaisingCostParameters_DoesNotInvalidateExistingHashes()
    {
        // The scenario this protects: an operator tunes Argon2 for new hardware. If
        // verification used the configured parameters instead of the stored ones, every
        // existing user would be locked out at once.
        var options = new PasswordHashingOptions { MemorySizeKib = 8192, Iterations = 1, DegreeOfParallelism = 1 };
        var mutable = new MutableOptions<PasswordHashingOptions>(options);
        var hasher = new Argon2PasswordHasher(mutable);

        string legacyHash = hasher.Hash(Password);

        mutable.Value = new PasswordHashingOptions
        {
            MemorySizeKib = 16384,
            Iterations = 3,
            DegreeOfParallelism = 2,
        };

        Assert.True(hasher.Verify(Password, legacyHash));
        Assert.StartsWith("$argon2id$v=19$m=16384,t=3,p=2$", hasher.Hash(Password), StringComparison.Ordinal);
    }

    [Fact]
    public void HashLengthFitsThePasswordHashColumn()
    {
        // password_hash is varchar(500); a hash that silently overflowed it would fail
        // at INSERT time, after the user believed their password was set.
        Argon2PasswordHasher hasher = CreateHasher(options => options.HashSizeBytes = 64);

        Assert.InRange(hasher.Hash(Password).Length, 1, 500);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("$argon2i$v=19$m=8192,t=1,p=1$c2FsdHNhbHQ$aGFzaGhhc2g")]     // wrong variant
    [InlineData("$argon2id$v=16$m=8192,t=1,p=1$c2FsdHNhbHQ$aGFzaGhhc2g")]    // wrong Argon2 version
    [InlineData("$argon2id$v=19$m=8192,t=1$c2FsdHNhbHQ$aGFzaGhhc2g")]        // missing parallelism
    [InlineData("$argon2id$v=19$m=0,t=1,p=1$c2FsdHNhbHQ$aGFzaGhhc2g")]       // nonsensical cost
    [InlineData("$argon2id$v=19$m=8192,t=1,p=1$!!!not-base64!!!$aGFzaGhhc2g")]
    [InlineData("$argon2id$v=19$m=8192,t=1,p=1$c2FsdHNhbHQ$")]               // empty tag
    public void AMalformedStoredHash_VerifiesAsFalse_RatherThanThrowing(string storedHash)
    {
        // A corrupt or foreign hash must be indistinguishable from a wrong password:
        // an exception here would surface as a 500 and confirm the account exists.
        Argon2PasswordHasher hasher = CreateHasher();

        Assert.False(hasher.Verify(Password, storedHash));
    }

    private static Argon2PasswordHasher CreateHasher(Action<PasswordHashingOptions>? configure = null)
    {
        var options = new PasswordHashingOptions { MemorySizeKib = 8192, Iterations = 1, DegreeOfParallelism = 1 };
        configure?.Invoke(options);
        return new Argon2PasswordHasher(Options.Create(options));
    }

    /// <summary>An <see cref="IOptions{T}"/> whose value can change after construction.</summary>
    private sealed class MutableOptions<T> : IOptions<T>
        where T : class
    {
        public MutableOptions(T value) => Value = value;

        public T Value { get; set; }
    }
}
