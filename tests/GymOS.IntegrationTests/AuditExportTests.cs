using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GymOS.BuildingBlocks.Infrastructure.Auditing;
using GymOS.Platform.Application.Auditing;
using GymOS.Platform.Infrastructure.Persistence;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The audited CSV export over real HTTP (M7 spec §8): the streamed body, the required and bounded
/// range, the <c>audit.export</c> permission boundary, and the fact that the export records itself
/// with the real row count once the stream has finished.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class AuditExportTests : IAsyncLifetime
{
    private const int RowCount = 1200;
    private const int SecondsStep = 100;

    /// <summary>
    /// A fixed instant, not <c>DateTimeOffset.UtcNow</c> (banned, R2/AD-40) — the seeded rows and the
    /// request's <c>from</c>/<c>to</c> are both built from this so the window matches the data exactly.
    /// </summary>
    private static readonly DateTimeOffset SeedStart = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset SeedEnd = SeedStart.AddSeconds((RowCount - 1) * SecondsStep);

    private const int QuotedEmailRowIndex = 3;
    private const string QuotedEmail = "quoted,user\"email@gymos.test";

    private const int FormulaActionRowIndex = 7;
    private const string FormulaAction = "=cmd|'/c calc'!A1";

    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public AuditExportTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        string connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("audit_export");
        _host = AuthenticationHost.Create(connectionString);
        _client = _host.CreateClient();

        using HttpResponseMessage ready = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        using HttpResponseMessage signIn = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);
        AuthApi.Authorize(_client, await AuthApi.ReadAccessTokenAsync(signIn));

        await SeedRowsAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
        await _sqlServer.DropDatabaseAsync(_host.ConnectionString);
    }

    [Fact]
    public async Task TheExportStreamsEveryMatchingRowOldestFirst()
    {
        using HttpResponseMessage response = await ExportAsync(SeedStart, SeedEnd);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);

        ContentDispositionHeaderValue? disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition!.DispositionType);
        Assert.False(string.IsNullOrWhiteSpace(disposition.FileName));

        string body = await response.Content.ReadAsStringAsync();

        // AuditCsv.FormatRow carries no line terminator; the writer supplies exactly one CRLF per
        // line (header and each row alike) via WriteLineAsync with NewLine = "\r\n". Splitting on
        // "\r\n" without RemoveEmptyEntries therefore yields exactly 1,201 lines (a header plus
        // 1,200 rows) plus the trailing empty string after the very last CRLF — 1,202 entries, and
        // no blank line anywhere in between.
        string[] lines = body.Split("\r\n");
        Assert.Equal(RowCount + 2, lines.Length);
        Assert.Equal(AuditCsv.Header, lines[0]);
        Assert.Equal(string.Empty, lines[^1]);
        Assert.DoesNotContain(lines[..^1], line => line.Length == 0);

        string[] dataLines = lines[1..^1];
        Assert.Equal(RowCount, dataLines.Length);

        // Ascending: the first data row is the oldest seeded timestamp, the last is the newest.
        DateTimeOffset previous = DateTimeOffset.MinValue;
        foreach (string line in dataLines)
        {
            DateTimeOffset createdAt = ExtractCreatedAtUtc(line);
            Assert.True(createdAt >= previous, "Rows must stream in ascending created_at_utc order.");
            previous = createdAt;
        }

        Assert.Equal(SeedStart, ExtractCreatedAtUtc(dataLines[0]));
        Assert.Equal(SeedEnd, ExtractCreatedAtUtc(dataLines[^1]));

        // The comma-and-quote user_email survived quoted, with its embedded quote doubled — not
        // split into two fields.
        Assert.Contains("\"quoted,user\"\"email@gymos.test\"", body, StringComparison.Ordinal);

        // The formula-leading action was guarded with a leading apostrophe before being quoted.
        Assert.Contains("\"'=cmd|'/c calc'!A1\"", body, StringComparison.Ordinal);

        // Nine columns only — no old_values/new_values anywhere in the header or the data.
        Assert.DoesNotContain("old_values", body, StringComparison.Ordinal);
        Assert.DoesNotContain("new_values", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SupportAgentIs403()
    {
        using HttpClient supportAgent = await PlatformApiHelpers.SignedInAsync(
            _host, _client, "SupportAgent", "support.agent.audit.export@gymos.test");

        using HttpResponseMessage response = await ExportAsync(supportAgent, SeedStart, SeedEnd);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A400DayRangeIs400()
    {
        using HttpResponseMessage response = await ExportAsync(SeedStart, SeedStart.AddDays(400));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("audit.export_range", await ReadProblemCodeAsync(response));
    }

    [Fact]
    public async Task MissingFromIs400()
    {
        using HttpResponseMessage response = await _client.GetAsync(new Uri(
            $"/api/v1/audit-logs/export?to={Uri.EscapeDataString(SeedEnd.ToString("o"))}",
            UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("audit.export_range", await ReadProblemCodeAsync(response));
    }

    [Fact]
    public async Task TheExportIsAudited()
    {
        using HttpResponseMessage export = await ExportAsync(SeedStart, SeedEnd);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        await export.Content.ReadAsStringAsync(); // Drain the stream so the audit command runs.

        using HttpResponseMessage list = await _client.GetAsync(
            new Uri("/api/v1/audit-logs?action=audit.exported", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        using JsonDocument listBody = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        JsonElement items = listBody.RootElement.GetProperty("items");
        Assert.True(items.GetArrayLength() > 0);

        Guid entryId = items[0].GetProperty("id").GetGuid();

        using HttpResponseMessage detail = await _client.GetAsync(
            new Uri($"/api/v1/audit-logs/{entryId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);

        using JsonDocument detailBody = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        using JsonDocument newValues = JsonDocument.Parse(
            detailBody.RootElement.GetProperty("newValues").GetString()!);

        Assert.Equal("1200", newValues.RootElement.GetProperty("rowCount").GetString());
        Assert.Equal(
            SeedStart.ToString("o"),
            newValues.RootElement.GetProperty("fromUtc").GetString());
        Assert.Equal(
            SeedEnd.ToString("o"),
            newValues.RootElement.GetProperty("toUtc").GetString());
    }

    private Task<HttpResponseMessage> ExportAsync(DateTimeOffset from, DateTimeOffset to) =>
        ExportAsync(_client, from, to);

    private static Task<HttpResponseMessage> ExportAsync(HttpClient client, DateTimeOffset from, DateTimeOffset to) =>
        client.GetAsync(new Uri(
            $"/api/v1/audit-logs/export?from={Uri.EscapeDataString(from.ToString("o"))}" +
            $"&to={Uri.EscapeDataString(to.ToString("o"))}",
            UriKind.Relative));

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out JsonElement code) ? code.GetString() : null;
    }

    /// <summary>
    /// <c>created_at_utc</c> is column index 1 (id, then the timestamp — M7 spec §8's fixed order),
    /// so extracting it means parsing the row's fields properly rather than trusting it to sit at a
    /// fixed offset from either end of the line.
    /// </summary>
    private static DateTimeOffset ExtractCreatedAtUtc(string line) =>
        DateTimeOffset.Parse(SplitFields(line)[1], CultureInfo.InvariantCulture);

    /// <summary>
    /// Splits an always-quoted, RFC-4180-ish CSV line into its raw field values (quotes stripped,
    /// doubled quotes collapsed back to one) — just enough of a quote-aware reader to pull a column
    /// out by index regardless of where it sits, including the rows with a comma or a quote embedded
    /// in <c>user_email</c>.
    /// </summary>
    private static string[] SplitFields(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool insideQuotes = false;
        int i = 0;

        while (i < line.Length)
        {
            char c = line[i];

            if (insideQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
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

    /// <summary>One <c>AddRange</c> and one <c>SaveChangesAsync</c> for all 1,200 rows.</summary>
    private async Task SeedRowsAsync()
    {
        await using AppDbContext dbContext = _host.CreateDbContext();

        var rows = new List<AuditLog>(RowCount);
        for (int i = 0; i < RowCount; i++)
        {
            rows.Add(new AuditLog
            {
                Action = i == FormulaActionRowIndex ? FormulaAction : $"export.seed_{i:D4}",
                UserEmail = i == QuotedEmailRowIndex ? QuotedEmail : $"seed{i:D4}@gymos.test",
                CreatedAtUtc = SeedStart.AddSeconds(i * SecondsStep),
            });
        }

        dbContext.Set<AuditLog>().AddRange(rows);
        await dbContext.SaveChangesAsync();
    }
}
