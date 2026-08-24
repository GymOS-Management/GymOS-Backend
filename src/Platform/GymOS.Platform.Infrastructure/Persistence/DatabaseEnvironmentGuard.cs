using Microsoft.Data.SqlClient;

namespace GymOS.Platform.Infrastructure.Persistence;

/// <summary>
/// Refuses to boot a host whose environment and database disagree: Development owns
/// <c>GymOS_Local</c>, Staging owns <c>GymOS_UAT</c>, Production owns <c>GymOS_Production</c>, and no
/// host may open a database belonging to one of the other two.
/// </summary>
/// <remarks>
/// <para><b>Why this exists at all.</b> The three environments are distinguished by a connection
/// string, and a connection string is a value that gets copied. Pasting the UAT string into a local
/// `.env` to reproduce a bug, or leaving it in a deployment variable that Production then inherits,
/// is a single silent step away from writing to the wrong environment — and the failure is invisible,
/// because the application works perfectly. It is simply working on the wrong data. Nothing in
/// configuration precedence catches that; a name comparison does.</para>
///
/// <para><b>It denies cross-environment pairings rather than prescribing a database per
/// environment</b>, and the distinction is deliberate. The rule is: <i>if the target database is one
/// of the three managed names, it must be the one this environment owns.</i> A database whose name is
/// not one of the three — an integration test's generated database, a developer's scratch copy — is
/// none of this guard's business and passes.</para>
///
/// <para>Prescribing instead of denying would be stricter and would break the suite: the integration
/// tests boot the real <c>Program</c> through <c>WebApplicationFactory</c>, and
/// <c>OpenApiExposureTests</c> deliberately boots hosts as <c>Development</c>, <c>Staging</c> <b>and</b>
/// <c>Production</c> to pin what an operator gets by doing nothing — each against a Testcontainers
/// database called something like <c>openapi_exposure_3</c>. A rule saying "a Production host must open
/// GymOS_Production" would fail every one of them, and the fix would be to weaken the guard with a
/// bypass switch that production could also set. Denying the pairings that are actually dangerous
/// needs no bypass, so there is none to misuse.</para>
///
/// <para>What that leaves uncovered is a host pointed at an unmanaged database it should not be using,
/// which is a typo rather than a cross-environment leak: it cannot reach UAT or Production data,
/// because reaching those means naming them. Integration tests are additionally stopped from ever
/// naming them by <c>SqlServerFixture</c>, which refuses a managed name outright — the guard covers
/// deployments, the fixture covers the suite.</para>
///
/// <para><b>It never logs or echoes the connection string.</b> Every message names the environment,
/// the database it found, and the database it expected — three values that are already in source
/// control. The password is in the same string and must not travel with them, so the string itself is
/// never interpolated into a message, an exception, or a log line.</para>
///
/// <para><b>Static and pure, so it is testable without a host.</b> It reads no configuration of its
/// own and holds no state: <c>Program.cs</c> hands it the environment name and the connection string.
/// That is what lets <c>DatabaseEnvironmentGuardTests</c> cover all nine environment/database
/// pairings as plain unit tests rather than by standing up nine hosts.</para>
/// </remarks>
public static class DatabaseEnvironmentGuard
{
    /// <summary>The local per-developer database, created by <c>db-init</c> in Docker Compose.</summary>
    public const string LocalDatabase = "GymOS_Local";

    /// <summary>The shared UAT database the Development/Staging deployment uses.</summary>
    public const string UatDatabase = "GymOS_UAT";

    /// <summary>The Production database, and the reason this class is not merely tidiness.</summary>
    public const string ProductionDatabase = "GymOS_Production";

    /// <summary>
    /// The three managed names, in one place so <c>SqlServerFixture</c> and the architecture tests can
    /// ask "is this a managed database?" without restating the list and drifting from it.
    /// </summary>
    public static readonly IReadOnlyList<string> ManagedDatabases =
        [LocalDatabase, UatDatabase, ProductionDatabase];

    /// <summary>
    /// Throws if <paramref name="environmentName"/> and the database named by
    /// <paramref name="connectionString"/> belong to different environments.
    /// </summary>
    /// <param name="environmentName">
    /// <c>IHostEnvironment.EnvironmentName</c>. An environment that is not one of the three known
    /// names — the integration suite's hosts included — owns no managed database, so it may open none
    /// of them.
    /// </param>
    /// <param name="connectionString">
    /// The resolved connection string. Read for its database name and nothing else, and never echoed.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The pairing is wrong, or the connection string cannot be parsed or names no database. Fatal by
    /// design: <c>Program.cs</c> logs a startup exception as Fatal and rethrows, so the host dies and
    /// the container restarts rather than serving traffic against the wrong environment.
    /// </exception>
    public static void Validate(string environmentName, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        string database = ReadDatabaseName(connectionString);

        // Not a managed database, so not this guard's business. A generated test database and a
        // developer's scratch copy both land here, and neither can reach another environment's data.
        if (!IsManaged(database))
        {
            return;
        }

        string? owned = OwnedDatabase(environmentName);

        if (owned is not null && database.Equals(owned, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // The message names the environment, what it found, and what it expected — and deliberately
        // not how it was configured, because the how is a connection string with a password in it.
        throw new InvalidOperationException(
            owned is null
                ? $"Refusing to start: the '{environmentName}' environment owns no GymOS database, but the "
                    + $"connection string targets '{database}', which belongs to another environment. "
                    + $"Managed databases are {LocalDatabase} (Development), {UatDatabase} (Staging) and "
                    + $"{ProductionDatabase} (Production); an environment outside those three may open "
                    + "none of them. Point this host at its own database."
                : $"Refusing to start: the '{environmentName}' environment must use '{owned}', but the "
                    + $"connection string targets '{database}'. These are different environments, and "
                    + "starting would read and write the wrong one. Correct the connection string for "
                    + $"this environment — see docs/Infrastructure/ENVIRONMENTS.md.");
    }

    /// <summary>Whether <paramref name="database"/> is one of the three environment databases.</summary>
    public static bool IsManaged(string? database) =>
        database is not null
        && ManagedDatabases.Any(managed => managed.Equals(database, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The database an environment owns, or <see langword="null"/> for any other environment name.
    ///
    /// <para>The mapping lives here, in code, and not in configuration — on purpose. A guard whose
    /// expectations can be moved by the same environment variables it is checking guards nothing.</para>
    /// </summary>
    public static string? OwnedDatabase(string environmentName)
    {
        // Matched case-insensitively because ASPNETCORE_ENVIRONMENT is a free-text value that people
        // type; "development" and "Development" must not behave differently.
        if (environmentName.Equals("Development", StringComparison.OrdinalIgnoreCase))
        {
            return LocalDatabase;
        }

        if (environmentName.Equals("Staging", StringComparison.OrdinalIgnoreCase))
        {
            return UatDatabase;
        }

        if (environmentName.Equals("Production", StringComparison.OrdinalIgnoreCase))
        {
            return ProductionDatabase;
        }

        return null;
    }

    /// <summary>
    /// The database name from a connection string, by the driver's own parser rather than by looking
    /// for "Database=".
    /// </summary>
    /// <remarks>
    /// <c>SqlConnectionStringBuilder</c> is used because it accepts every spelling the driver does —
    /// <c>Database</c>, <c>Initial Catalog</c>, differing case and whitespace — so this cannot pass a
    /// string the driver would then interpret differently. It also rejects a PostgreSQL-shaped string
    /// outright ("Keyword not supported: 'host'"), which is exactly the failure a half-finished
    /// migration produces, and turns it into a startup error naming the problem.
    /// </remarks>
    private static string ReadDatabaseName(string connectionString)
    {
        SqlConnectionStringBuilder builder;

        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException exception)
        {
            // exception.Message names the offending keyword, never a value, so it is safe to surface.
            throw new InvalidOperationException(
                "Refusing to start: the database connection string could not be parsed as a SQL Server "
                    + $"connection string ({exception.Message}). A PostgreSQL-style string using 'Host' or "
                    + "'Username' produces exactly this error — the platform runs on SQL Server "
                    + "(ADR-54), so the string needs 'Server' and 'User Id'.",
                exception);
        }

        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
        {
            throw new InvalidOperationException(
                "Refusing to start: the database connection string names no database, so which "
                    + "environment it would open cannot be determined. Set Database (or Initial Catalog) "
                    + $"to one of {LocalDatabase}, {UatDatabase} or {ProductionDatabase}.");
        }

        return builder.InitialCatalog;
    }
}
