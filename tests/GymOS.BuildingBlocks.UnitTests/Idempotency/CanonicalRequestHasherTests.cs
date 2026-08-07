// tests/GymOS.BuildingBlocks.UnitTests/Idempotency/CanonicalRequestHasherTests.cs
using GymOS.BuildingBlocks.Application.Idempotency;
using Xunit;

namespace GymOS.BuildingBlocks.UnitTests.Idempotency;

public sealed class CanonicalRequestHasherTests
{
    private sealed record AlphaFirst(string Amount, string Currency);

    // Same shape, reversed declaration order: canonicalization must make them hash equal.
    private sealed record CurrencyFirst(string Currency, string Amount);

    [Fact]
    public void Same_values_hash_identically_regardless_of_property_declaration_order()
    {
        string a = CanonicalRequestHasher.Hash(new AlphaFirst("50.00", "EUR"));
        string b = CanonicalRequestHasher.Hash(new CurrencyFirst("EUR", "50.00"));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Different_values_hash_differently()
    {
        string a = CanonicalRequestHasher.Hash(new AlphaFirst("50.00", "EUR"));
        string b = CanonicalRequestHasher.Hash(new AlphaFirst("59.99", "EUR"));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_is_64_lowercase_hex_characters()
    {
        string hash = CanonicalRequestHasher.Hash(new AlphaFirst("50.00", "EUR"));
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void Hash_is_stable_across_calls()
    {
        var request = new AlphaFirst("50.00", "EUR");
        Assert.Equal(CanonicalRequestHasher.Hash(request), CanonicalRequestHasher.Hash(request));
    }
}
