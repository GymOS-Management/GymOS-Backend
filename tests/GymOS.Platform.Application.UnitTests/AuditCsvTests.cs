using GymOS.Platform.Application.Auditing;
using Xunit;

namespace GymOS.Platform.Application.UnitTests;

/// <summary>
/// The CSV shape of an exported audit row (M7 spec §8), tested as pure functions — no dispatcher,
/// no database, just <see cref="AuditCsv"/> against an <see cref="AuditLogListItem"/>.
/// </summary>
public sealed class AuditCsvTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 19, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void HeaderIsTheNineColumnsInTheConstrainedOrder()
    {
        Assert.Equal(
            "id,created_at_utc,user_email,user_id,ip_address,action,entity_type,entity_id,correlation_id",
            AuditCsv.Header);
    }

    [Fact]
    public void ACommaAndAQuoteInUserEmail_AreQuotedAndDoubled()
    {
        var item = new AuditLogListItem(
            Id: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            UserId: null,
            UserEmail: "a,b\"c",
            IpAddress: null,
            Action: "auth.signed_in",
            EntityType: null,
            EntityId: null,
            CorrelationId: null,
            CreatedAtUtc: CreatedAt);

        string row = AuditCsv.FormatRow(item);

        // Raw CSV text: the field is quoted and its embedded quote is doubled.
        Assert.Contains("\"a,b\"\"c\"", row, StringComparison.Ordinal);

        string[] fields = SplitQuotedFields(row);

        // Index 2 is user_email: the comma stayed inside the quotes rather than splitting the field,
        // and the doubled quote in the raw CSV text decodes back to the original single quote.
        Assert.Equal("a,b\"c", fields[2]);
    }

    [Fact]
    public void ALeadingEqualsInAction_IsPrefixedWithAnApostrophe()
    {
        var item = new AuditLogListItem(
            Id: Guid.NewGuid(),
            UserId: null,
            UserEmail: null,
            IpAddress: null,
            Action: "=cmd|'/c calc'!A1",
            EntityType: null,
            EntityId: null,
            CorrelationId: null,
            CreatedAtUtc: CreatedAt);

        string row = AuditCsv.FormatRow(item);
        string[] fields = SplitQuotedFields(row);

        // Index 5 is action.
        Assert.StartsWith("'=", fields[5], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData('=')]
    [InlineData('+')]
    [InlineData('-')]
    [InlineData('@')]
    [InlineData('\t')]
    [InlineData('\r')]
    public void EveryFormulaLeadingCharacter_IsPrefixed(char leadingCharacter)
    {
        string escaped = AuditCsv.Escape(leadingCharacter + "rest");

        Assert.Equal($"\"'{leadingCharacter}rest\"", escaped);
    }

    [Fact]
    public void NullFields_BecomeAnEmptyQuotedField()
    {
        Assert.Equal("\"\"", AuditCsv.Escape(null));
    }

    /// <summary>
    /// <see cref="AuditCsv.FormatRow"/> itself carries no line terminator — the writer supplies
    /// exactly one CRLF per line, the way the endpoint does with a <see cref="StreamWriter"/> whose
    /// <c>NewLine</c> is <c>"\r\n"</c>. A <see cref="StringWriter"/> honours the same
    /// <c>NewLine</c>/<c>WriteLineAsync</c> contract, so it stands in for the endpoint's writer here
    /// without needing a real stream. The result must be exactly header, CRLF, row, CRLF, row, CRLF —
    /// no blank line between records.
    /// </summary>
    [Fact]
    public async Task HeaderAndRows_WrittenThroughAWriter_ProduceOneCrLfPerLineAndNoBlankLines()
    {
        var first = new AuditLogListItem(
            Id: Guid.Parse("44444444-4444-4444-4444-444444444444"),
            UserId: null,
            UserEmail: null,
            IpAddress: null,
            Action: "auth.signed_in",
            EntityType: null,
            EntityId: null,
            CorrelationId: null,
            CreatedAtUtc: CreatedAt);

        var second = new AuditLogListItem(
            Id: Guid.Parse("55555555-5555-5555-5555-555555555555"),
            UserId: null,
            UserEmail: null,
            IpAddress: null,
            Action: "auth.signed_out",
            EntityType: null,
            EntityId: null,
            CorrelationId: null,
            CreatedAtUtc: CreatedAt);

        using var writer = new StringWriter { NewLine = "\r\n" };
        await writer.WriteLineAsync(AuditCsv.Header);
        await writer.WriteLineAsync(AuditCsv.FormatRow(first));
        await writer.WriteLineAsync(AuditCsv.FormatRow(second));

        string expected =
            AuditCsv.Header + "\r\n" +
            AuditCsv.FormatRow(first) + "\r\n" +
            AuditCsv.FormatRow(second) + "\r\n";

        Assert.Equal(expected, writer.ToString());
    }

    [Fact]
    public void CreatedAtUtc_RoundTripsAsIso8601()
    {
        var item = new AuditLogListItem(
            Id: Guid.NewGuid(),
            UserId: null,
            UserEmail: null,
            IpAddress: null,
            Action: "auth.signed_in",
            EntityType: null,
            EntityId: null,
            CorrelationId: null,
            CreatedAtUtc: CreatedAt);

        string row = AuditCsv.FormatRow(item);
        string[] fields = SplitQuotedFields(row);

        // Index 1 is created_at_utc — the second column, right after id.
        Assert.Equal(CreatedAt, DateTimeOffset.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void IdAndOtherGuids_AreFormattedWithDashesAndNoBraces()
    {
        var id = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var correlationId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        var item = new AuditLogListItem(
            Id: id,
            UserId: null,
            UserEmail: null,
            IpAddress: null,
            Action: "auth.signed_in",
            EntityType: null,
            EntityId: null,
            CorrelationId: correlationId,
            CreatedAtUtc: CreatedAt);

        string row = AuditCsv.FormatRow(item);
        string[] fields = SplitQuotedFields(row);

        Assert.Equal(id.ToString("D"), fields[0]);
        Assert.Equal(correlationId.ToString("D"), fields[8]);
    }

    /// <summary>
    /// Splits an always-quoted, no-embedded-comma-outside-quotes CSV row (no line terminator, since
    /// <see cref="AuditCsv.FormatRow"/> no longer appends one) into its raw field values (quotes
    /// stripped, doubled quotes collapsed back to one) — just enough of a CSV reader to assert on
    /// individual columns without pulling in a library for it.
    /// </summary>
    private static string[] SplitQuotedFields(string row)
    {
        string trimmed = row.TrimEnd('\r', '\n');
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool insideQuotes = false;
        int i = 0;

        while (i < trimmed.Length)
        {
            char c = trimmed[i];

            if (insideQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < trimmed.Length && trimmed[i + 1] == '"')
                    {
                        current.Append('"');
                        i += 2;
                        continue;
                    }

                    insideQuotes = false;
                    i++;
                    continue;
                }

                current.Append(c);
                i++;
                continue;
            }

            if (c == '"')
            {
                insideQuotes = true;
                i++;
                continue;
            }

            if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
                i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        fields.Add(current.ToString());
        return [.. fields];
    }
}
