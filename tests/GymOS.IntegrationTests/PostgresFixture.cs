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

    /// <summary>
    /// The role every test host connects as. Not a superuser and not <c>BYPASSRLS</c>.
    /// </summary>
    /// <remarks>
    /// Testcontainers' bootstrap role is a superuser, and PostgreSQL exempts a superuser from
    /// row-level security unconditionally — <c>FORCE</c> included. A suite running as one cannot
    /// observe an isolation failure, which is why R14's tenancy suite had to build a restricted role
    /// of its own before any of its assertions meant anything. Doing it here means every suite
    /// inherits that property instead of the next security-sensitive test rediscovering it.
    /// </remarks>
    private const string TestRole = "gymos_test";

    /// <summary>A password for a throwaway role inside a throwaway container; not a secret.</summary>
    private const string TestRolePassword = "gymos-test-role";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCommand("-c", "max_connections=300")
        .Build();

    /// <summary>
    /// The container's bootstrap superuser. For suites that deliberately need one — asserting that a
    /// bypassing role is refused, for instance. Everything else uses <see cref="ConnectionString"/>.
    /// </summary>
    public string BootstrapConnectionString => Pooled(_container.GetConnectionString());

    /// <summary>Connection string of the container's default database, as <see cref="TestRole"/>.</summary>
    public string ConnectionString => AsTestRole(_container.GetConnectionString());

    public async Task<string> CreateDatabaseAsync(string databaseName)
    {
        await _container.ExecScriptAsync($"CREATE DATABASE \"{databaseName}\" OWNER \"{TestRole}\";");

        // Creating the database with an OWNER is not enough on its own: `public` is owned by
        // pg_database_owner, and EF puts __EFMigrationsHistory there. R14's tenancy suite hit exactly
        // this — migrations fail on their first statement without it. It has to run *inside* the new
        // database, which ExecScriptAsync cannot target, so this opens its own connection.
        //
        // Unpooled, deliberately: every isolated database gets a unique name, so this connection string
        // is never reused and pooling it would only leave an idle connection behind — one per database
        // this method ever creates, for as long as Npgsql's idle lifetime keeps it open. Across a run
        // that creates on the order of a hundred isolated databases, that is enough on its own to reach
        // `max_connections=300` and surface as `53300` in whatever test happens to be dialing in at the
        // time. Closing the socket for real, rather than parking it, is what keeps this addition from
        // being the second contributor the type's own pooling comment already warns about.
        await using (var connection = new NpgsqlConnection(Unpooled(_container.GetConnectionString(), databaseName)))
        {
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER SCHEMA public OWNER TO \"{TestRole}\";";
            await command.ExecuteNonQueryAsync();
        }

        return AsTestRole(_container.GetConnectionString(), databaseName);
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

    private static string AsTestRole(string baseConnectionString, string? databaseName = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(Pooled(baseConnectionString, databaseName))
        {
            Username = TestRole,
            Password = TestRolePassword,
        };

        return builder.ConnectionString;
    }

    /// <summary>
    /// A connection string for a single use-and-discard connection: no pool to leave a connection
    /// idling in after <c>DisposeAsync</c>. See the call site in <see cref="CreateDatabaseAsync"/> for
    /// why that distinction matters here specifically.
    /// </summary>
    private static string Unpooled(string baseConnectionString, string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = databaseName,
            Pooling = false,
        };

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

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Once per container. Both ALTERs run here, not through a second connection, because
        // ExecScriptAsync targets the container's default database, which is the one they need to be
        // inside.
        //
        // Both are needed, not just the schema one: `ALTER SCHEMA public OWNER TO` alone (which is all
        // CreateDatabaseAsync needs for a database it *creates* `OWNER "{TestRole}"`) leaves this
        // pre-existing default database itself owned by the bootstrap superuser. The very first
        // migration calls EnsureSchema to create the `platform` schema, and CREATE SCHEMA needs
        // database-level CREATE — owning `public` does not confer that. Verified by running the whole
        // suite without this line: every host that boots against the default database (ApiEndpointTests,
        // for one) failed migrating with `42501: permission denied for database`.
        await _container.ExecScriptAsync(
            $"""
            CREATE ROLE "{TestRole}" LOGIN PASSWORD '{TestRolePassword}' NOSUPERUSER NOBYPASSRLS;

            DO $$
            BEGIN
                EXECUTE format('ALTER DATABASE %I OWNER TO %I', current_database(), '{TestRole}');
            END
            $$;

            ALTER SCHEMA public OWNER TO "{TestRole}";
            """);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollectionDefinition : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
