using GymOS.Platform.Infrastructure.Persistence;
using Xunit;

namespace GymOS.Platform.Infrastructure.UnitTests;

/// <summary>
/// The environment/database pairing, which is the only thing standing between a copied connection
/// string and a host quietly reading and writing the wrong environment.
///
/// <para>Every pairing is covered in both directions rather than only the happy path: the guard's
/// value is entirely in what it refuses, so a test suite that only proved the matching pairs work
/// would pass against a guard that never threw at all.</para>
/// </summary>
public sealed class DatabaseEnvironmentGuardTests
{
    /// <summary>A password-shaped value, so the redaction test has something real to look for.</summary>
    private const string Password = "NotARealPassword1";

    private static string ConnectionTo(string database) =>
        $"Server=localhost,1433;Database={database};User Id=gymos_app;Password={Password};"
        + "Encrypt=True;TrustServerCertificate=True";

    [Theory]
    [InlineData("Development", DatabaseEnvironmentGuard.LocalDatabase)]
    [InlineData("Staging", DatabaseEnvironmentGuard.UatDatabase)]
    [InlineData("Production", DatabaseEnvironmentGuard.ProductionDatabase)]
    public void AnEnvironmentMayOpenItsOwnDatabase(string environment, string database)
    {
        DatabaseEnvironmentGuard.Validate(environment, ConnectionTo(database));
    }

    /// <summary>
    /// The six wrong pairings among the three managed databases. Production-opening-UAT and
    /// Local-opening-Production are the two the environment model names explicitly, but a rule that
    /// caught only those would leave four other ways to cross the same boundary.
    /// </summary>
    [Theory]
    [InlineData("Development", DatabaseEnvironmentGuard.UatDatabase)]
    [InlineData("Development", DatabaseEnvironmentGuard.ProductionDatabase)]
    [InlineData("Staging", DatabaseEnvironmentGuard.LocalDatabase)]
    [InlineData("Staging", DatabaseEnvironmentGuard.ProductionDatabase)]
    [InlineData("Production", DatabaseEnvironmentGuard.LocalDatabase)]
    [InlineData("Production", DatabaseEnvironmentGuard.UatDatabase)]
    public void AnEnvironmentMayNotOpenAnotherEnvironmentsDatabase(string environment, string database)
    {
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => DatabaseEnvironmentGuard.Validate(environment, ConnectionTo(database)));

        // The message has to be actionable by whoever sees the boot fail, so it names both halves of
        // the mismatch. Asserted because a guard that fails with "invalid configuration" sends the
        // reader to the wrong file.
        Assert.Contains(environment, thrown.Message, StringComparison.Ordinal);
        Assert.Contains(database, thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An environment outside the three owns nothing, so it may open none of the three. This is what
    /// stops the integration suite — whose hosts run under whatever environment a test asks for —
    /// from reaching UAT or Production even if someone hands it their connection string.
    /// </summary>
    [Theory]
    [InlineData(DatabaseEnvironmentGuard.LocalDatabase)]
    [InlineData(DatabaseEnvironmentGuard.UatDatabase)]
    [InlineData(DatabaseEnvironmentGuard.ProductionDatabase)]
    public void AnUnknownEnvironmentMayOpenNoManagedDatabase(string database)
    {
        Assert.Throws<InvalidOperationException>(
            () => DatabaseEnvironmentGuard.Validate("Testing", ConnectionTo(database)));
    }

    /// <summary>
    /// The deliberate gap, stated as a test so it cannot be closed by accident: a database that is not
    /// one of the three managed names passes under any environment. Integration tests create databases
    /// like <c>openapi_exposure_3</c> and boot Development, Staging and Production hosts against them;
    /// prescribing a database per environment instead of denying cross-environment pairings would fail
    /// every one of those, and the fix would have been a bypass switch production could also set.
    /// </summary>
    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    [InlineData("Testing")]
    public void AnUnmanagedDatabaseIsNotThisGuardsBusiness(string environment)
    {
        DatabaseEnvironmentGuard.Validate(environment, ConnectionTo("openapi_exposure_3"));
    }

    /// <summary>
    /// ASPNETCORE_ENVIRONMENT is free text that people type, and SQL Server database names are
    /// compared case-insensitively under the platform's CI collation. Neither may change the verdict.
    /// </summary>
    [Theory]
    [InlineData("development", "gymos_local")]
    [InlineData("DEVELOPMENT", "GYMOS_LOCAL")]
    [InlineData("Production", "gymos_production")]
    public void CasingChangesNothing(string environment, string database)
    {
        DatabaseEnvironmentGuard.Validate(environment, ConnectionTo(database));
    }

    [Fact]
    public void CasingDoesNotLetACrossEnvironmentPairingThrough()
    {
        Assert.Throws<InvalidOperationException>(
            () => DatabaseEnvironmentGuard.Validate("development", ConnectionTo("gymos_production")));
    }

    /// <summary>
    /// The failure a half-finished migration produces. `Microsoft.Data.SqlClient` rejects `Host` and
    /// `Username` outright, and the guard turns that into a startup error that names the cause instead
    /// of an unhandled ArgumentException from inside the driver.
    /// </summary>
    [Fact]
    public void APostgreSqlShapedConnectionStringIsRejectedWithAnExplanation()
    {
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => DatabaseEnvironmentGuard.Validate(
                "Development",
                $"Host=localhost;Port=5432;Database=gymos;Username=gymos_app;Password={Password}"));

        Assert.Contains("SQL Server", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AConnectionStringNamingNoDatabaseIsRejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => DatabaseEnvironmentGuard.Validate(
                "Development",
                $"Server=localhost,1433;User Id=gymos_app;Password={Password};Encrypt=True"));
    }

    /// <summary>
    /// The one thing a configuration guard must never do. Its whole job is to complain about a
    /// connection string, and the obvious way to write that message is to include the string — which
    /// puts a live database password into a log line, an exception report, and whatever aggregates
    /// them. Every pairing that can throw is checked, not just one.
    /// </summary>
    [Theory]
    [InlineData("Development", DatabaseEnvironmentGuard.UatDatabase)]
    [InlineData("Production", DatabaseEnvironmentGuard.UatDatabase)]
    [InlineData("Testing", DatabaseEnvironmentGuard.ProductionDatabase)]
    public void TheMessageNeverCarriesThePassword(string environment, string database)
    {
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => DatabaseEnvironmentGuard.Validate(environment, ConnectionTo(database)));

        Assert.DoesNotContain(Password, thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APostgreSqlRejectionAlsoCarriesNoPassword()
    {
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => DatabaseEnvironmentGuard.Validate(
                "Development",
                $"Host=localhost;Port=5432;Database=gymos;Username=gymos_app;Password={Password}"));

        Assert.DoesNotContain(Password, thrown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DatabaseEnvironmentGuard.LocalDatabase, true)]
    [InlineData(DatabaseEnvironmentGuard.UatDatabase, true)]
    [InlineData(DatabaseEnvironmentGuard.ProductionDatabase, true)]
    [InlineData("gymos_shared", false)]
    [InlineData("openapi_exposure_3", false)]
    public void IsManagedRecognisesExactlyTheThreeEnvironmentDatabases(string database, bool expected)
    {
        Assert.Equal(expected, DatabaseEnvironmentGuard.IsManaged(database));
    }
}
