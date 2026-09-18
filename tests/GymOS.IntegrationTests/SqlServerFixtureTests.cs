using Microsoft.Data.SqlClient;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The fixture's own contract for giving databases back.
///
/// <para>Until 2026-09-18 the suite created a database per test method — on the order of three
/// hundred a run, 32 MB each by construction — and dropped none of them until the container went.
/// That footprint outgrew a GitHub runner's disk (the last two classes failed on <c>CREATE
/// DATABASE</c> with the disk full, twice, at the same point). These tests pin the mechanism that
/// keeps the footprint at one class's worth: a database is dropped when its owner is done with it, the
/// drop takes the files with it, and the one database every non-isolated suite shares can never be
/// dropped by mistake.</para>
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class SqlServerFixtureTests
{
    private readonly SqlServerFixture _sqlServer;

    public SqlServerFixtureTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    [Fact]
    public async Task DroppingADatabase_RemovesItAndItsFiles()
    {
        string connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("fixture_drop");
        string name = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        Assert.True(await ExistsAsync(name), "the database should exist before it is dropped");

        await _sqlServer.DropDatabaseAsync(connectionString);

        Assert.False(await ExistsAsync(name), "the database should be gone after the drop");
        Assert.Equal(0, await FileCountAsync(name));
    }

    [Fact]
    public async Task DroppingADatabase_WithAPooledConnectionStillOpen_Succeeds()
    {
        // The realistic case: a disposed WebApplicationFactory leaves its SqlClient pool behind for
        // the process's lifetime, and a DROP that waited for those connections would never finish.
        string connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("fixture_drop_pooled");
        string name = new SqlConnectionStringBuilder(connectionString).InitialCatalog;

        await using (var pooled = new SqlConnection(connectionString))
        {
            await pooled.OpenAsync();
        } // returned to the pool, not closed

        await _sqlServer.DropDatabaseAsync(connectionString);

        Assert.False(await ExistsAsync(name));
    }

    [Fact]
    public async Task DroppingTheSharedDatabase_IsRefused()
    {
        string shared = new SqlConnectionStringBuilder(_sqlServer.ConnectionString).InitialCatalog;

        InvalidOperationException refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sqlServer.DropDatabaseAsync(_sqlServer.ConnectionString));

        Assert.Contains(shared, refusal.Message, StringComparison.Ordinal);
        Assert.True(await ExistsAsync(shared), "the shared database must survive the attempt");
    }

    [Fact]
    public async Task DroppingADatabaseTwice_IsNotAnError()
    {
        // DisposeAsync paths run on failure too, sometimes twice; a second drop must not turn a
        // failing test's cleanup into a different failure.
        string connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("fixture_drop_twice");

        await _sqlServer.DropDatabaseAsync(connectionString);
        await _sqlServer.DropDatabaseAsync(connectionString);
    }

    [Fact]
    public async Task ALease_DropsItsDatabaseWhenDisposed()
    {
        string name;
        await using (SqlServerFixture.DatabaseLease lease = await _sqlServer.LeaseDatabaseAsync("fixture_lease"))
        {
            name = new SqlConnectionStringBuilder(lease.ConnectionString).InitialCatalog;
            Assert.True(await ExistsAsync(name));
        }

        Assert.False(await ExistsAsync(name), "disposing the lease should drop the database");
    }

    [Fact]
    public async Task AnUnmigratedLease_IsUnmigrated_AndStillDropsOnDispose()
    {
        string name;
        await using (SqlServerFixture.DatabaseLease lease = await _sqlServer.LeaseUnmigratedDatabaseAsync("fixture_lease_raw"))
        {
            name = new SqlConnectionStringBuilder(lease.ConnectionString).InitialCatalog;
            Assert.Equal(0, await CountAsync(lease.ConnectionString,
                "SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID('platform')"));
        }

        Assert.False(await ExistsAsync(name));
    }

    private Task<bool> ExistsAsync(string databaseName) =>
        CountAsync(
            SqlServerFixture.Unpooled(_sqlServer.BootstrapConnectionString, "master"),
            $"SELECT COUNT(*) FROM sys.databases WHERE name = N'{databaseName}'")
        .ContinueWith(t => t.Result == 1, TaskScheduler.Default);

    private Task<int> FileCountAsync(string databaseName) =>
        CountAsync(
            SqlServerFixture.Unpooled(_sqlServer.BootstrapConnectionString, "master"),
            $"SELECT COUNT(*) FROM sys.master_files WHERE DB_NAME(database_id) = N'{databaseName}'");

    private static async Task<int> CountAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return (int)(await command.ExecuteScalarAsync())!;
    }
}
