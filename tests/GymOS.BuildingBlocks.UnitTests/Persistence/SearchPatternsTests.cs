using GymOS.BuildingBlocks.Infrastructure.Persistence;
using Xunit;

namespace GymOS.BuildingBlocks.UnitTests.Persistence;

/// <summary>
/// One definition of "contains this text", shared by every list endpoint's search. Unescaped, a
/// caller's <c>%</c> or <c>_</c> is a wildcard, so the list silently returns rows that do not
/// contain the search text — four query classes each had their own copy of this line and only one
/// escaped.
/// </summary>
public sealed class SearchPatternsTests
{
    [Fact]
    public void OrdinaryTextIsWrappedInWildcards() =>
        Assert.Equal("%acme%", SearchPatterns.Contains("acme"));

    [Theory]
    [InlineData("100%", @"%100\%%")]          // a percentage in a gym's name
    [InlineData("2026_0001", @"%2026\_0001%")] // an underscore in an invoice number
    [InlineData("%", @"%\%%")]                 // the wildcard alone: matched literally, not everything
    [InlineData("_", @"%\_%")]
    public void MetacharactersAreEscaped(string search, string expected) =>
        Assert.Equal(expected, SearchPatterns.Contains(search));

    [Fact]
    public void TheEscapeCharacterItselfIsEscapedFirst() =>
        // Escaping "\" after the others would double the backslashes just added.
        Assert.Equal(@"%a\\b%", SearchPatterns.Contains(@"a\b"));

    [Fact]
    public void EscapingTwiceIsNotEscapingOnce() =>
        // Deliberately pinned: double-escaping turns a search for "%" into a search for the
        // literal "\%", so this must be applied at exactly one place — the shared helper — and
        // never again by a caller.
        Assert.Equal(@"\\\%", SearchPatterns.Escape(SearchPatterns.Escape("%")));
}
