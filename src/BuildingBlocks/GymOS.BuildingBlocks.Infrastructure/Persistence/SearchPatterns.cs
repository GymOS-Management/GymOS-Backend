namespace GymOS.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Turns a caller's free-text search into a LIKE pattern that matches it <b>literally</b>.
///
/// <para>Shared because getting it wrong is invisible: interpolating the raw string leaves
/// <c>%</c> and <c>_</c> as wildcards, so a search for "100% Fitness" matches every name starting
/// with "100", and a single "_" matches the entire table — the list then reports rows that do not
/// contain the search text at all. Four query classes each had their own copy of this line; only
/// one of them escaped.</para>
///
/// <para>Callers use <c>EF.Functions.Like(column, pattern)</c> with no <c>ESCAPE</c> argument — see
/// the escaping note below for why there is none to pass.</para>
///
/// <para><b>Case-insensitivity is the collation's job here, not the operator's.</b> Under PostgreSQL
/// these searches used <c>ILIKE</c>, because <c>LIKE</c> there is case-sensitive and the cluster was
/// initialised with <c>lc-collate=C</c>. SQL Server has no <c>ILIKE</c>; case-sensitivity is a
/// property of the column's collation instead, and the database is created
/// <c>Latin1_General_100_CI_AS</c> (see the initial migration), so a plain <c>LIKE</c> is already
/// case-insensitive. Anything that changes the database or column collation to a <c>_CS_</c> variant
/// silently makes every search on this page case-sensitive, which is why the collation is asserted by
/// a test rather than left as a deployment detail.</para>
/// </summary>
public static class SearchPatterns
{
    /// <summary>A "contains" pattern: the value, escaped, wrapped in wildcards.</summary>
    public static string Contains(string value) => $"%{Escape(value)}%";

    /// <summary>
    /// A "starts with" pattern: the value, escaped, followed by one wildcard.
    ///
    /// <para>Exists so a prefix filter gets the same two properties the free-text ones already have —
    /// case-insensitivity (through the column's collation) and literal matching.
    /// <c>string.StartsWith</c> translates to a <c>LIKE</c> with the caller's metacharacters intact,
    /// which is a filter that silently finds everything for a <c>%</c>.</para>
    /// </summary>
    public static string StartsWith(string value) => $"{Escape(value)}%";

    /// <summary>
    /// Escapes the LIKE metacharacters by bracketing them, which is what removes the need for an
    /// <c>ESCAPE</c> clause.
    /// </summary>
    /// <remarks>
    /// <para><b>Brackets rather than a backslash, because the operator changed.</b> The PostgreSQL
    /// version prefixed each metacharacter with <c>\</c> and every call site passed that character as
    /// <c>ILIKE</c>'s third argument. <c>EF.Functions.Like</c> on SQL Server has no such overload — an
    /// <c>ESCAPE</c> clause cannot be expressed through it at all — so a backslash-escaped pattern
    /// would reach the server with its backslashes intact and be matched as literal backslashes, and
    /// the metacharacters it was supposed to neutralise would still be wildcards. Bracketing needs no
    /// clause: <c>[%]</c>, <c>[_]</c> and <c>[[]</c> are single-character sets that match exactly the
    /// character inside.</para>
    /// <para><b><c>[</c> is escaped first, and must be.</b> It is the one metacharacter that appears
    /// in the replacements, so escaping it after the others would re-escape the brackets they just
    /// added and turn <c>[%]</c> into <c>[[]%]</c> — a set matching <c>[</c>, followed by a live
    /// wildcard. Same hazard the backslash version had, same fix: the character that appears in its
    /// own replacement goes first.</para>
    /// <para><b><c>]</c> is deliberately not escaped.</b> Outside a set a closing bracket is an
    /// ordinary literal to SQL Server, and bracketing it would be harmless but noisy. It cannot start
    /// a set, so it cannot change what a pattern matches.</para>
    /// </remarks>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value
            .Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("%", "[%]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal);
    }
}
