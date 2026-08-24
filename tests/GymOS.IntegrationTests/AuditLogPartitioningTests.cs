using System.Globalization;
using System.Net;
using System.Text.Json;
using GymOS.BuildingBlocks.Infrastructure.Auditing;
using GymOS.BuildingBlocks.Infrastructure.Time;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.Platform.Infrastructure.Persistence.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// Monthly range partitioning of <c>platform.audit_logs</c> (ARCHITECTURE.md §2.6, AD-7).
///
/// These assertions read the catalog views rather than trusting the migration ran: the whole point
/// of partitioning before production is that the physical layout is right, and "the migration
/// applied without error" does not prove a table is partitioned or that rows route to the month they
/// belong to.
/// </summary>
/// <remarks>
/// <para><b>What partitioning means here differs from the PostgreSQL version, and the tests differ
/// with it.</b> PostgreSQL partitions are separate tables with their own names, so those assertions
/// counted rows in <c>pg_class</c> named <c>audit_logs_2026_08</c> and asked which table a row's
/// <c>tableoid</c> pointed at. SQL Server has one table cut into numbered partitions by a partition
/// function, so the same questions are asked of <c>sys.partition_range_values</c> and the
/// <c>$PARTITION</c> operator instead. The properties being proved are unchanged: range-partitioned
/// on <c>created_at_utc</c>, boundaries exist ahead of need, rows land in their own month, a write is
/// never refused for want of a partition, maintenance is idempotent, every index is aligned, and a
/// month can be retired without a row-by-row DELETE.</para>
/// <para><b>There is no DEFAULT partition, and its absence is the improvement.</b> PostgreSQL refuses
/// an INSERT whose month has no partition, so the original migration had to create a DEFAULT one as a
/// safety net — audit rows are written inside the transaction of the operation they describe, so a
/// refused audit insert would fail that operation. A SQL Server range function with N boundaries
/// always defines N+1 partitions covering the whole domain, so the trailing partition absorbs any
/// future row and the safety net is structural.
/// <see cref="ARowDatedBeyondEveryBoundaryIsStillAccepted"/> is what used to be the DEFAULT-partition
/// test.</para>
/// </remarks>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class AuditLogPartitioningTests : IAsyncLifetime
{
    /// <summary>The partition function the RuntimeDatabaseProgrammability migration creates.</summary>
    private const string PartitionFunction = "pf_audit_logs_monthly";

    /// <summary>DateTime.UtcNow is banned (R2 / AD-40); the clock is injected even in a test.</summary>
    private static readonly SystemClock Clock = new();

    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    /// <summary>Kept for the statements in this suite that need DDL: the SWITCH retention test.</summary>
    private string _connectionString = null!;

    public AuditLogPartitioningTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        _connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("audit_partitions");
        _host = AuthenticationHost.Create(_connectionString);
        _client = _host.CreateClient();

        using HttpResponseMessage ready = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task TheAuditTableIsRangePartitionedOnCreatedAt()
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // boundary_value_on_right = 1 is RANGE RIGHT: each boundary is the first instant of its
        // month and that month's rows sit to its right. RANGE LEFT would put midnight on the first
        // into the PREVIOUS month's partition, which for a table written by machines is not a corner
        // case. partition_ordinal = 1 identifies the column the function is applied to.
        var layout = await QuerySingleAsync<PartitionLayout>(
            dbContext,
            """
            SELECT pf.name                                     AS function_name,
                   pf.type_desc                                AS strategy,
                   CONVERT(int, pf.boundary_value_on_right)    AS boundary_on_right,
                   c.name                                      AS key_column
            FROM sys.tables t
            JOIN sys.schemas s        ON s.schema_id = t.schema_id
            JOIN sys.indexes i        ON i.object_id = t.object_id AND i.index_id = 1
            JOIN sys.partition_schemes ps ON ps.data_space_id = i.data_space_id
            JOIN sys.partition_functions pf ON pf.function_id = ps.function_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id
                                     AND ic.index_id = i.index_id
                                     AND ic.partition_ordinal = 1
            JOIN sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE s.name = 'platform' AND t.name = 'audit_logs'
            """);

        Assert.Equal(PartitionFunction, layout.FunctionName);
        Assert.Equal("RANGE", layout.Strategy);
        Assert.Equal(1, layout.BoundaryOnRight);
        Assert.Equal("created_at_utc", layout.KeyColumn);
    }

    [Fact]
    public async Task BoundariesExistForTheCurrentMonthAndTheConfiguredLookahead()
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        List<string> boundaries = await QueryListAsync<string>(
            dbContext,
            $"""
            SELECT CONVERT(char(7), CONVERT(datetimeoffset(7), prv.value), 126) AS [Value]
            FROM sys.partition_range_values prv
            JOIN sys.partition_functions pf ON pf.function_id = prv.function_id
            WHERE pf.name = '{PartitionFunction}'
            ORDER BY prv.boundary_id
            """);

        // Default lookahead is 3 months, so the current month plus three — the same four the
        // PostgreSQL suite counted as tables.
        Assert.Equal(4, boundaries.Count);

        DateTime utcNow = Clock.UtcNow.UtcDateTime;
        DateTime firstOfThisMonth = new(utcNow.Year, utcNow.Month, 1);

        Assert.Equal(
            Enumerable.Range(0, 4)
                .Select(offset => firstOfThisMonth.AddMonths(offset).ToString("yyyy-MM", CultureInfo.InvariantCulture))
                .ToList(),
            boundaries);

        // Four boundaries define five partitions: one per month above, plus the trailing one that
        // makes a far-future write land somewhere rather than be refused. There is deliberately no
        // DEFAULT partition to look for — see the remarks on this class.
        Assert.Equal(5, await CountPartitionsAsync(dbContext));
    }

    [Fact]
    public async Task WrittenRowsLandInTheirOwnMonthsPartition()
    {
        // Produced through the API so this exercises the interceptor's real write path.
        await SignInAsync();

        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Each row's partition, next to the partition the first instant of that row's own month maps
        // to. Equal for every row is exactly "rows route to the month they belong to" — the property
        // the PostgreSQL version got by comparing a partition's NAME to the row's month, which has no
        // counterpart here because SQL Server partitions are numbered rather than named.
        var placements = await QueryListAsync<RowPlacement>(
            dbContext,
            $"""
            SELECT $PARTITION.{PartitionFunction}(l.created_at_utc) AS actual_partition,
                   $PARTITION.{PartitionFunction}(
                       TODATETIMEOFFSET(
                           CONVERT(datetime2(7),
                                   DATEFROMPARTS(YEAR(l.created_at_utc), MONTH(l.created_at_utc), 1)), 0))
                                                                    AS month_partition
            FROM platform.audit_logs l
            """);

        Assert.NotEmpty(placements);
        Assert.All(placements, placement =>
            Assert.Equal(placement.MonthPartition, placement.ActualPartition));

        // And none of them fell into the trailing partition, which is what would happen if
        // maintenance had never run. Written rows are for the current month, so they belong in the
        // first of the five.
        Assert.All(placements, placement => Assert.NotEqual(5, placement.ActualPartition));
    }

    [Fact]
    public async Task ARowDatedBeyondEveryBoundaryIsStillAccepted()
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Ten years out: no boundary covers it, and no maintenance run would have created one.
        //
        // Under PostgreSQL this row would have been REFUSED without a DEFAULT partition to catch it,
        // and the test asserted it landed there. Here the trailing partition covers everything past
        // the last boundary, so the write simply succeeds — which is the property that mattered all
        // along, since an audit insert is inside the transaction of the operation it describes and a
        // refusal would fail that operation.
        dbContext.Set<AuditLog>().Add(new AuditLog
        {
            Action = "test.far_future",
            CreatedAtUtc = new DateTimeOffset(2036, 1, 15, 12, 0, 0, TimeSpan.Zero),
        });

        await dbContext.SaveChangesAsync();

        int partition = await QuerySingleAsync<int>(
            dbContext,
            $"""
            SELECT $PARTITION.{PartitionFunction}(l.created_at_utc) AS [Value]
            FROM platform.audit_logs l
            WHERE l.action = 'test.far_future'
            """);

        // The last of the five, not one of the four months.
        Assert.Equal(await CountPartitionsAsync(dbContext), partition);
    }

    [Fact]
    public async Task PartitionMaintenanceIsIdempotent()
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        var maintainer = scope.ServiceProvider.GetRequiredService<AuditLogPartitionMaintainer>();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        int before = await CountBoundariesAsync(dbContext);

        // Startup already ran it once; running it repeatedly must add nothing, because every
        // instance of a scaled-out deployment will do exactly this on every boot.
        await maintainer.EnsurePartitionsAsync(CancellationToken.None);
        await maintainer.EnsurePartitionsAsync(CancellationToken.None);

        Assert.Equal(before, await CountBoundariesAsync(dbContext));
    }

    [Fact]
    public async Task EveryIndexIsAlignedToThePartitionScheme()
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The SQL Server counterpart of "indexes are inherited by every partition". PostgreSQL
        // creates a matching index on each partition automatically, so that suite counted five
        // indexes on each partition table. Here there is one index object per index, cut into
        // partitions alongside the table — provided it was built ON the partition scheme. An index
        // left on PRIMARY still works and still answers queries, which is why this needs asserting:
        // it silently makes the whole table the unit of retention again, because a month cannot be
        // switched out while an unaligned index spans it.
        List<string> unaligned = await QueryListAsync<string>(
            dbContext,
            """
            SELECT i.name AS [Value]
            FROM sys.indexes i
            JOIN sys.tables t  ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = 'platform'
              AND t.name = 'audit_logs'
              AND i.name IS NOT NULL
              AND i.data_space_id NOT IN (SELECT data_space_id FROM sys.partition_schemes)
            """);

        Assert.Empty(unaligned);

        // And that there are as many as the model declares: the clustered primary key plus the four
        // secondary indexes AuditLogConfiguration asks for.
        int indexes = await QuerySingleAsync<int>(
            dbContext,
            """
            SELECT COUNT(*) AS [Value]
            FROM sys.indexes i
            JOIN sys.tables t  ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = 'platform' AND t.name = 'audit_logs' AND i.name IS NOT NULL
            """);

        Assert.Equal(5, indexes);
    }

    [Fact]
    public async Task TheAuditApiIsUnchangedByPartitioning()
    {
        await SignInAsync();

        using HttpResponseMessage list = await _client.GetAsync(
            new Uri("/api/v1/audit-logs?pageSize=5", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        JsonElement first = body.RootElement.GetProperty("items").EnumerateArray().First();

        // Lookup by id alone still works: id leads the composite primary key, so the index serves the
        // probe without the caller supplying a date.
        using HttpResponseMessage detail = await _client.GetAsync(
            new Uri($"/api/v1/audit-logs/{first.GetProperty("id").GetGuid()}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
    }

    [Fact]
    public async Task SwitchingAPartitionOutRemovesItsRowsWithoutADelete()
    {
        // Proves the retention mechanism AD-7 relies on: the future job retires an expired month
        // instead of issuing DELETEs against an append-only table.
        //
        // SWITCH, not DETACH. PostgreSQL detaches a partition and it becomes a standalone table;
        // SQL Server moves a partition's rows into an existing, empty, identically-shaped table in
        // one metadata operation. Same cost (no rows are read or written), same outcome (the month
        // leaves the live table intact and archivable), different spelling.
        await SignInAsync();

        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        int partition = await QuerySingleAsync<int>(
            dbContext,
            $"SELECT TOP 1 $PARTITION.{PartitionFunction}(l.created_at_utc) AS [Value] FROM platform.audit_logs l");

        int before = await dbContext.Set<AuditLog>().CountAsync();
        Assert.True(before > 0);

        // The staging table has to match the source exactly — same columns, same clustered key, same
        // aligned nonclustered indexes — or SQL Server refuses the switch. That strictness is the
        // reason this is a metadata operation rather than a copy, and it is why the archive table is
        // created here rather than assumed.
        //
        // It is NOT on the partition scheme: a single partition switches out to an ordinary table on
        // the same filegroup, which is PRIMARY for every partition of this scheme.
        //
        // DDL, so it goes over a migrator connection rather than through dbContext — which connects
        // as the app principal, owns nothing, and is refused. That refusal is the arrangement
        // working, not an obstacle: in the deployment this statement belongs to the retention job's
        // DDL path, never to the principal serving requests.
        await using (var migrator = new Microsoft.Data.SqlClient.SqlConnection(
            SqlServerFixture.AsMigrator(_connectionString)))
        {
            await migrator.OpenAsync();

            await using (var create = migrator.CreateCommand())
            {
                create.CommandText =
                    """
                    CREATE TABLE platform.audit_logs_archive (
                        id             uniqueidentifier   NOT NULL,
                        created_at_utc datetimeoffset(7)  NOT NULL,
                        user_id        uniqueidentifier   NULL,
                        user_email     nvarchar(320)      NULL,
                        ip_address     nvarchar(45)       NULL,
                        user_agent     nvarchar(512)      NULL,
                        action         nvarchar(100)      NOT NULL,
                        entity_type    nvarchar(100)      NULL,
                        entity_id      uniqueidentifier   NULL,
                        old_values     nvarchar(max)      NULL,
                        new_values     nvarchar(max)      NULL,
                        correlation_id uniqueidentifier   NULL,
                        CONSTRAINT pk_audit_logs_archive PRIMARY KEY CLUSTERED (id, created_at_utc)
                    ) ON [PRIMARY];

                    CREATE INDEX ix_audit_logs_archive_created_at_utc
                        ON platform.audit_logs_archive (created_at_utc DESC) ON [PRIMARY];
                    CREATE INDEX ix_audit_logs_archive_entity_type_entity_id
                        ON platform.audit_logs_archive (entity_type, entity_id, created_at_utc) ON [PRIMARY];
                    CREATE INDEX ix_audit_logs_archive_user_id
                        ON platform.audit_logs_archive (user_id, created_at_utc) ON [PRIMARY];
                    CREATE INDEX ix_audit_logs_archive_action
                        ON platform.audit_logs_archive (action, created_at_utc) ON [PRIMARY];
                    """;

                await create.ExecuteNonQueryAsync();
            }

            await using var switchOut = migrator.CreateCommand();

            // The partition number came from $PARTITION, so it is an integer the server produced —
            // but it is still interpolated into DDL, which admits no parameter. Bounded explicitly.
            Assert.InRange(partition, 1, 5);

            switchOut.CommandText =
                $"ALTER TABLE platform.audit_logs SWITCH PARTITION {partition} TO platform.audit_logs_archive;";

            await switchOut.ExecuteNonQueryAsync();
        }

        Assert.Equal(0, await dbContext.Set<AuditLog>().CountAsync());

        // Switched, not destroyed: the rows are still there to be archived before dropping.
        int archived = await QuerySingleAsync<int>(
            dbContext,
            "SELECT COUNT(*) AS [Value] FROM platform.audit_logs_archive");

        Assert.Equal(before, archived);
    }

    private async Task SignInAsync()
    {
        using HttpResponseMessage signIn = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        AuthApi.Authorize(_client, await AuthApi.ReadAccessTokenAsync(signIn));
    }

    /// <summary>How many boundaries the partition function currently defines.</summary>
    private static Task<int> CountBoundariesAsync(AppDbContext dbContext) =>
        QuerySingleAsync<int>(
            dbContext,
            $"""
            SELECT COUNT(*) AS [Value]
            FROM sys.partition_range_values prv
            JOIN sys.partition_functions pf ON pf.function_id = prv.function_id
            WHERE pf.name = '{PartitionFunction}'
            """);

    /// <summary>How many partitions the table is actually cut into — always boundaries + 1.</summary>
    private static Task<int> CountPartitionsAsync(AppDbContext dbContext) =>
        QuerySingleAsync<int>(
            dbContext,
            """
            SELECT COUNT(*) AS [Value]
            FROM sys.partitions p
            JOIN sys.tables t  ON t.object_id = p.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = 'platform' AND t.name = 'audit_logs' AND p.index_id = 1
            """);

    private static Task<T> QuerySingleAsync<T>(AppDbContext dbContext, string sql) =>
        dbContext.Database.SqlQueryRaw<T>(sql).SingleAsync();

    private static async Task<List<T>> QueryListAsync<T>(AppDbContext dbContext, string sql) =>
        await dbContext.Database.SqlQueryRaw<T>(sql).ToListAsync();

    private sealed record PartitionLayout(string FunctionName, string Strategy, int BoundaryOnRight, string KeyColumn);

    private sealed record RowPlacement(int ActualPartition, int MonthPartition);
}
