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

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollectionDefinition : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
