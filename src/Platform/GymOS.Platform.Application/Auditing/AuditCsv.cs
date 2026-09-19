using System.Globalization;

namespace GymOS.Platform.Application.Auditing;

/// <summary>
/// The CSV shape of an audited row (M7 spec §8). The same nine fields as
/// <see cref="AuditLogListItem"/>, but not in its constructor order — the spec fixes the column
/// order as <c>id, created_at_utc, user_email, user_id, ip_address, action, entity_type, entity_id,
/// correlation_id</c>: the timestamp second, so a spreadsheet sorts by it at a glance, and the email
/// before the id, since a human reads the email. <c>oldValues</c>/<c>newValues</c> are deliberately
/// not among them; a caller who needs a diff reads it per entry through
/// <c>GET /audit-logs/{id}</c> instead of pulling every payload into a file.
///
/// Every field is quoted and an embedded quote is doubled, per RFC 4180. A field whose first
/// character could be read as a spreadsheet formula (<c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab,
/// or a carriage return) is prefixed with <c>'</c> before quoting: the export leaves the platform as
/// a file a human is expected to open in a spreadsheet, and a crafted user email or action must not
/// execute there.
///
/// <para><b>Assumes no field can contain an embedded line break.</b> <see cref="FormatRow"/> writes no
/// line terminator of its own and relies on the caller's <see cref="System.IO.StreamWriter"/> to write
/// exactly one CRLF per record; <see cref="Escape"/> does not guard a CR or LF appearing anywhere but
/// the first character of a field. That is safe only because every field here is one the platform
/// itself controls the shape of: ids and timestamps are formatted values, the IP address is a parsed
/// <c>IPAddress</c>, <c>action</c> and <c>entity_type</c> are constants from the platform's own
/// catalog, and <c>user_email</c> is validated on the way in. None of them is free-text a caller could
/// use to smuggle a line break into the middle of a row.</para>
/// </summary>
public static class AuditCsv
{
    /// <summary>366 days — a full year including a leap day, inclusive of both ends.</summary>
    public const int MaxRangeDays = 366;

    // Order fixed by M7 spec §8: created_at_utc second, so a spreadsheet sorts by it at a glance;
    // user_email before user_id, since a human reads the email.
    public const string Header =
        "id,created_at_utc,user_email,user_id,ip_address,action,entity_type,entity_id,correlation_id";

    private static readonly char[] FormulaLeadingCharacters = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>
    /// One CSV record for <paramref name="item"/>, columns in the header's order — no line
    /// terminator: the caller terminates lines; a <see cref="System.IO.StreamWriter"/> with
    /// <c>NewLine = "\r\n"</c> writes RFC 4180 records. <c>created_at_utc</c> is ISO-8601
    /// round-trip (<c>"o"</c>) and every id is <c>"D"</c>.
    /// </summary>
    public static string FormatRow(AuditLogListItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        string?[] fields =
        [
            item.Id.ToString("D", CultureInfo.InvariantCulture),
            item.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture),
            item.UserEmail,
            item.UserId?.ToString("D", CultureInfo.InvariantCulture),
            item.IpAddress,
            item.Action,
            item.EntityType,
            item.EntityId?.ToString("D", CultureInfo.InvariantCulture),
            item.CorrelationId?.ToString("D", CultureInfo.InvariantCulture),
        ];

        return string.Join(",", fields.Select(Escape));
    }

    /// <summary>
    /// Always quotes; doubles an embedded quote; prefixes a leading formula-guard character with
    /// <c>'</c> before quoting, so the guard survives the very quote a spreadsheet strips on open.
    /// A <see langword="null"/> field becomes an empty quoted field, <c>""</c>.
    /// </summary>
    public static string Escape(string? field)
    {
        string value = field ?? string.Empty;

        if (value.Length > 0 && FormulaLeadingCharacters.Contains(value[0]))
        {
            value = "'" + value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
