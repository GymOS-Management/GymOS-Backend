using System.Globalization;

namespace GymOS.BuildingBlocks.Application.Abstractions;

/// <summary>
/// A resource's version as it crosses the API boundary — the value a client echoes back in
/// <c>If-Match</c> to say "I am updating what I read".
///
/// <para>Deliberately opaque: it is a string to callers, and the fact that it is currently a SQL
/// Server <c>rowversion</c> rendered in decimal is not part of the contract. A client that parsed it
/// as a number would break the day a table needs a different mechanism — and the opacity has already
/// paid for itself once, since this was PostgreSQL's <c>xmin</c> until the SQL Server migration and
/// no caller had to change.</para>
/// <para>Tokens do not survive that migration, and are not meant to: a version identifies a row in
/// one database, so an <c>If-Match</c> carrying a token minted by the old store is correctly a 412.
/// Clients re-read and retry, which is the same thing they already do after any conflict.</para>
/// </summary>
public static class RowVersion
{
    /// <summary>Renders a stored version for a response body or an <c>ETag</c>.</summary>
    public static string ToToken(ulong version) => version.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a token supplied by a caller. Returns <see langword="false"/> for anything
    /// unparseable — a malformed <c>If-Match</c> is bad input (400), not a conflict (409),
    /// and definitely not something to treat as "no precondition given".
    /// </summary>
    public static bool TryParse(string? token, out ulong version)
    {
        version = 0;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        // HTTP quotes entity-tags (RFC 9110 §8.8.3) and clients echo them verbatim, so accept
        // the quoted form and the bare value. W/ marks a weak validator, which cannot be used
        // for If-Match — rejected rather than silently honored.
        string candidate = token.Trim();

        if (candidate.StartsWith("W/", StringComparison.Ordinal))
        {
            return false;
        }

        candidate = candidate.Trim('"');

        return ulong.TryParse(candidate, NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }

    /// <summary>The strong entity-tag form for an <c>ETag</c> response header.</summary>
    public static string ToETag(string token) => $"\"{token}\"";
}
