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
    /// <summary>
    /// Each test host gets a small pool, and the server allows more clients than the default.
    ///
    /// <para>Both halves are needed, and the suite outgrew the defaults rather than misusing them. Every
    /// <c>WebApplicationFactory</c> host opens its own Npgsql pool, whose default ceiling is 100
    /// connections — for a suite that boots a fresh host per test class (and a second one whenever it
    /// needs an administrator in a given role), that adds up to far more than PostgreSQL's default
    /// <c>max_connections</c> of 100. The symptom is
    /// <c>53300: sorry, too many clients already</c> arriving in whichever test happens to run when the
    /// limit is reached — so it reads as a flaky failure in unrelated code.</para>
    ///
    /// <para>A five-connection pool is ample: these hosts serve one request at a time.</para>
    /// </summary>
    private const int MaxPoolSizePerHost = 5;

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCommand("-c", "max_connections=300")
        .Build();

    /// <summary>Connection string of the container's default database.</summary>
    public string ConnectionString => Pooled(_container.GetConnectionString());

    public async Task<string> CreateDatabaseAsync(string databaseName)
    {
        await _container.ExecScriptAsync($"CREATE DATABASE {databaseName};");
        return Pooled(_container.GetConnectionString(), databaseName);
    }

    private static string Pooled(string baseConnectionString, string? databaseName = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            MaxPoolSize = MaxPoolSizePerHost,
        };

        if (databaseName is not null)
        {
            builder.Database = databaseName;
        }

        return builder.ConnectionString;
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
