using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GymOS.BuildingBlocks.Infrastructure.Auditing;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.Platform.Infrastructure.Persistence.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// Monthly range partitioning of <c>platform.audit_logs</c> (ARCHITECTURE.md §2.6, AD-7).
///
/// These assertions read <c>pg_catalog</c> rather than trusting the migration ran: the whole
/// point of partitioning before production is that the physical layout is right, and "the
/// migration applied without error" does not prove a table is partitioned or that rows route
/// to the month they belong to.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class AuditLogPartitioningTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    /// <summary>Kept for the one statement in this suite that needs the migrator: DETACH PARTITION.</summary>
    private string _connectionString = null!;

    public AuditLogPartitioningTests(SqlServerFixture postgres) => _sqlServer = postgres;

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

        // relkind 'p' = partitioned table; partstrat 'r' = RANGE.
        var layout = await QuerySingleAsync<PartitionLayout>(
            dbContext,
            """
            SELECT c.relkind::text AS kind,
                   p.partstrat::text AS strategy,
                   (SELECT a.attname
                      FROM pg_attribute a
                     WHERE a.attrelid = c.oid
                       AND a.attnum = p.partattrs[0]) AS key_column
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
              JOIN pg_partitioned_table p ON p.partrelid = c.oid
             WHERE n.nspname = 'platform' AND c.relname = 'audit_logs'
            """);

        Assert.Equal("p", layout.Kind);
        Assert.Equal("r", layout.Strategy);
        Assert.Equal("created_at_utc", layout.KeyColumn);
    }

    [Fact]
    public async Task PartitionsExistForTheCurrentMonthAndTheConfiguredLookahead()
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var monthlyPartitions = await QueryListAsync<string>(
            dbContext,
            """
            SELECT c.relname AS "Value"
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'platform'
               AND c.relkind = 'r'
               AND c.relname LIKE 'audit_logs_2%'
             ORDER BY c.relname
            """);

        // Default lookahead is 3 months, so the current month plus three.
        Assert.Equal(4, monthlyPartitions.Count);

        var defaultPartition = await QueryListAsync<string>(
            dbContext,
            """
            SELECT c.relname AS "Value"
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'platform' AND c.relkind = 'r' AND c.relname = 'audit_logs_default'
            """);

        // The safety net: audit writes are inside business transactions, so a month with no
        // partition must never turn into a rejected write.
        Assert.Single(defaultPartition);
    }

    [Fact]
    public async Task WrittenRowsLandInTheirOwnMonthsPartition()
    {
        // Produced through the API so this exercises the interceptor's real write path.
        await SignInAsync();

        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var placements = await QueryListAsync<RowPlacement>(
            dbContext,
            """
            SELECT (l.tableoid::regclass)::text AS partition,
                   to_char(l.created_at_utc AT TIME ZONE 'UTC', 'YYYY_MM') AS month
              FROM platform.audit_logs l
            """);

        Assert.NotEmpty(placements);

        // Every row is in the partition named for its own month, and none fell through to
        // DEFAULT — which is what makes DETACH a safe retention mechanism later.
        Assert.All(placements, placement =>
            Assert.Equal($"platform.audit_logs_{placement.Month}", placement.Partition));
    }

    [Fact]
    public async Task ARowDatedOutsideEveryPartitionLandsInDefaultRatherThanFailing()
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Ten years out: no partition covers it, and no maintenance run would have created one.
        dbContext.Set<AuditLog>().Add(new AuditLog
        {
            Action = "test.far_future",
            CreatedAtUtc = new DateTimeOffset(2036, 1, 15, 12, 0, 0, TimeSpan.Zero),
        });

        await dbContext.SaveChangesAsync();

        string partition = await QuerySingleAsync<string>(
            dbContext,
            """
            SELECT (l.tableoid::regclass)::text AS "Value"
              FROM platform.audit_logs l
             WHERE l.action = 'test.far_future'
            """);

        Assert.Equal("platform.audit_logs_default", partition);
    }

    [Fact]
    public async Task PartitionMaintenanceIsIdempotent()
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        var maintainer = scope.ServiceProvider.GetRequiredService<AuditLogPartitionMaintainer>();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        int before = await CountMonthlyPartitionsAsync(dbContext);

        // Startup already ran it once; running it repeatedly must add nothing, because every
        // instance of a scaled-out deployment will do exactly this on every boot.
        await maintainer.EnsurePartitionsAsync(CancellationToken.None);
        await maintainer.EnsurePartitionsAsync(CancellationToken.None);

        Assert.Equal(before, await CountMonthlyPartitionsAsync(dbContext));
    }

    [Fact]
    public async Task IndexesAreInheritedByEveryPartition()
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var indexCounts = await QueryListAsync<int>(
            dbContext,
            """
            SELECT count(*)::int AS "Value"
              FROM pg_class partition_table
              JOIN pg_namespace n ON n.oid = partition_table.relnamespace
              JOIN pg_index i ON i.indrelid = partition_table.oid
             WHERE n.nspname = 'platform'
               AND partition_table.relkind = 'r'
               AND partition_table.relname LIKE 'audit_logs_%'
             GROUP BY partition_table.oid
            """);

        // Declared once on the parent, so a partition created next year needs no index DDL:
        // 4 secondary indexes + the primary key on each.
        Assert.NotEmpty(indexCounts);
        Assert.All(indexCounts, count => Assert.Equal(5, count));
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

        // Lookup by id alone still works: id leads the composite primary key, so each
        // partition's index serves the probe without the caller supplying a date.
        using HttpResponseMessage detail = await _client.GetAsync(
            new Uri($"/api/v1/audit-logs/{first.GetProperty("id").GetGuid()}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
    }

    [Fact]
    public async Task DetachingAPartitionRemovesItsRowsWithoutADelete()
    {
        // Proves the retention mechanism AD-7 relies on: the future job detaches an expired
        // month instead of issuing DELETEs against an append-only table.
        await SignInAsync();

        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        string currentPartition = await QuerySingleAsync<string>(
            dbContext,
            """
            SELECT (l.tableoid::regclass)::text AS "Value"
              FROM platform.audit_logs l
             LIMIT 1
            """);

        int before = await dbContext.Set<AuditLog>().CountAsync();
        Assert.True(before > 0);

        // DDL cannot take parameters, so the identifier is validated instead of parameterized.
        // It came from pg_catalog, but checking the shape keeps that assumption explicit.
        Assert.Matches(@"^platform\.audit_logs_\d{4}_\d{2}$", currentPartition);

        // DETACH requires ownership of the parent, so it goes over a migrator connection rather than
        // through dbContext — which connects as the app role, owns nothing, and is refused with
        // `42501: must be owner of table audit_logs`. That refusal is the arrangement working, not an
        // obstacle: in the deployment this statement belongs to the retention job's DDL path, never to
        // the role serving requests.
        await using (var migrator = new Microsoft.Data.SqlClient.SqlConnection(SqlServerFixture.AsMigrator(_connectionString)))
        {
            await migrator.OpenAsync();

            await using var detach = migrator.CreateCommand();
            detach.CommandText = "ALTER TABLE platform.audit_logs DETACH PARTITION " + currentPartition;
            await detach.ExecuteNonQueryAsync();
        }

#pragma warning disable EF1002, EF1003 // Validated identifier above; DDL admits no parameter here.

        Assert.Equal(0, await dbContext.Set<AuditLog>().CountAsync());

        // Detached, not destroyed: the rows are still there to be archived before dropping.
        int archived = await QuerySingleAsync<int>(
            dbContext,
            "SELECT count(*)::int AS \"Value\" FROM " + currentPartition);
#pragma warning restore EF1002, EF1003

        Assert.Equal(before, archived);
    }

    private async Task SignInAsync()
    {
        using HttpResponseMessage signIn = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        AuthApi.Authorize(_client, await AuthApi.ReadAccessTokenAsync(signIn));
    }

    private static Task<int> CountMonthlyPartitionsAsync(AppDbContext dbContext) =>
        QuerySingleAsync<int>(
            dbContext,
            """
            SELECT count(*)::int AS "Value"
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'platform' AND c.relkind = 'r' AND c.relname LIKE 'audit_logs_2%'
            """);

    private static Task<T> QuerySingleAsync<T>(AppDbContext dbContext, string sql) =>
        dbContext.Database.SqlQueryRaw<T>(sql).SingleAsync();

    private static async Task<List<T>> QueryListAsync<T>(AppDbContext dbContext, string sql) =>
        await dbContext.Database.SqlQueryRaw<T>(sql).ToListAsync();

    private sealed record PartitionLayout(string Kind, string Strategy, string KeyColumn);

    private sealed record RowPlacement(string Partition, string Month);
}
