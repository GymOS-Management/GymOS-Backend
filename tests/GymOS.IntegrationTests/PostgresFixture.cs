using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// One PostgreSQL 17 container shared by the whole integration collection.
/// Suites that need isolation get their own database inside the container via
/// <see cref="CreateDatabaseAsync"/>.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    /// <summary>Connection string of the container's default database.</summary>
    public string ConnectionString => _container.GetConnectionString();

    public async Task<string> CreateDatabaseAsync(string databaseName)
    {
        await _container.ExecScriptAsync($"CREATE DATABASE {databaseName};");
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = databaseName,
        }.ConnectionString;
    }

    /// <summary>
    /// A database nobody else will touch, for suites whose assertions depend on the rows
    /// present.
    ///
    /// xUnit constructs a fresh test-class instance per test method, so a fixed database name
    /// silently shares state across every method in the class — and a test that counts rows
    /// then passes or fails on execution order. The suffix makes the isolation real rather
    /// than assumed.
    /// </summary>
    public Task<string> CreateIsolatedDatabaseAsync(string prefix) =>
        CreateDatabaseAsync($"{prefix}_{Interlocked.Increment(ref _databaseSequence)}");

    private int _databaseSequence;

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollectionDefinition : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
