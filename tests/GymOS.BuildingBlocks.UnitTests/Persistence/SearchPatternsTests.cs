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
    [InlineData("100%", "%100[%]%")]           // a percentage in a gym's name
    [InlineData("2026_0001", "%2026[_]0001%")] // an underscore in an invoice number
    [InlineData("%", "%[%]%")]                 // the wildcard alone: matched literally, not everything
    [InlineData("_", "%[_]%")]
    [InlineData("[", "%[[]%")]                 // opens a set, so it is a metacharacter too
    public void MetacharactersAreEscaped(string search, string expected) =>
        Assert.Equal(expected, SearchPatterns.Contains(search));

    [Fact]
    public void ABareClosingBracketIsLeftAlone() =>
        // Outside a set, "]" is an ordinary literal to SQL Server. Pinned so nobody "fixes" the
        // asymmetry with "[" and starts emitting patterns that are harder to read for no gain.
        Assert.Equal("%a]b%", SearchPatterns.Contains("a]b"));

    [Fact]
    public void TheOpeningBracketIsEscapedFirst() =>
        // Searching for the literal text "[%" gives "[[]" (a set matching "[") followed by "[%]"
        // (a set matching "%") — two literals, no wildcard left alive.
        //
        // "[" is the one metacharacter that appears in the replacements, so it has to go first. Were
        // "%" escaped first, its replacement "[%]" would then be re-escaped into "[[]%]" — a set
        // matching "[", followed by a LIVE wildcard, which is the exact bug this helper exists to
        // prevent. Under PostgreSQL the same hazard applied to the backslash, for the same reason.
        Assert.Equal("%[[][%]%", SearchPatterns.Contains("[%"));

    [Fact]
    public void EscapingTwiceIsNotEscapingOnce()
    {
        // Deliberately pinned: escaping an already-escaped pattern turns a search for "%" into a
        // search for the literal text "[%]", so this must be applied at exactly one place — the
        // shared helper — and never again by a caller.
        Assert.Equal("[%]", SearchPatterns.Escape("%"));
        Assert.Equal("[[][%]]", SearchPatterns.Escape(SearchPatterns.Escape("%")));
    }
}
