namespace GymOS.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Turns a caller's free-text search into a LIKE/ILIKE pattern that matches it <b>literally</b>.
///
/// <para>Shared because getting it wrong is invisible: interpolating the raw string leaves
/// <c>%</c> and <c>_</c> as wildcards, so a search for "100% Fitness" matches every name starting
/// with "100", and a single "_" matches the entire table — the list then reports rows that do not
/// contain the search text at all. Four query classes each had their own copy of this line; only
/// one of them escaped.</para>
///
/// <para>Callers must pass <see cref="EscapeCharacter"/> as the ILIKE escape argument
/// (<c>EF.Functions.ILike(column, pattern, SearchPatterns.EscapeCharacter)</c>), or the backslashes
/// this adds are matched as literal backslashes.</para>
/// </summary>
public static class SearchPatterns
{
    /// <summary>The escape character the patterns below use.</summary>
    public const string EscapeCharacter = @"\";

    /// <summary>A "contains" pattern: the value, escaped, wrapped in wildcards.</summary>
    public static string Contains(string value) => $"%{Escape(value)}%";

    /// <summary>
    /// A "starts with" pattern: the value, escaped, followed by one wildcard.
    ///
    /// <para>Exists so a prefix filter gets the same two properties the free-text ones already have —
    /// case-insensitivity (through <c>ILIKE</c>) and literal matching. <c>string.StartsWith</c>
    /// translates to a case-<em>sensitive</em> <c>LIKE</c> with the caller's metacharacters intact,
    /// which is a filter that silently finds nothing for a capitalised prefix and silently finds
    /// everything for a <c>%</c>.</para>
    /// </summary>
    public static string StartsWith(string value) => $"{Escape(value)}%";

    /// <summary>
    /// Escapes the LIKE metacharacters. The escape character goes first — escaping it after
    /// adding others would double the ones just added.
    /// </summary>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value
            .Replace(EscapeCharacter, EscapeCharacter + EscapeCharacter, StringComparison.Ordinal)
            .Replace("%", EscapeCharacter + "%", StringComparison.Ordinal)
            .Replace("_", EscapeCharacter + "_", StringComparison.Ordinal);
    }
}
