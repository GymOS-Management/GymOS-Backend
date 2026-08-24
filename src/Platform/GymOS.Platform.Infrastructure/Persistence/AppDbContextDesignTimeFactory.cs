using GymOS.Platform.Infrastructure.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GymOS.Platform.Infrastructure.Persistence;

/// <summary>
/// Used exclusively by <c>dotnet ef</c> at design time so migrations never bootstrap
/// the host. Connection: the <c>ConnectionStrings__Database</c> environment variable when set,
/// otherwise the docker-compose development database. Never used at runtime.
/// </summary>
/// <remarks>
/// The provider options must match <see cref="PlatformInfrastructureServiceCollectionExtensions"/>
/// exactly — in particular the migrations history table's schema. A design-time factory that left it
/// in <c>dbo</c> would scaffold against a history table the running host never reads, and every
/// migration would then look pending to the host and applied to <c>dotnet ef</c>.
/// </remarks>
public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <summary>
    /// The one variable the whole system names its database with: docker-compose sets it for the
    /// <c>migrator</c> and <c>api</c> services, deploy-uat.yml and deploy-production.yml set it for the
    /// migrate job, and the host reads the same value as <c>ConnectionStrings:Database</c> through
    /// standard configuration precedence (docs/Infrastructure/ENVIRONMENTS.md §6).
    /// </summary>
    /// <remarks>
    /// Design time used to read a private <c>GYMOS_DATABASE</c> instead — a name nothing else in the
    /// repository set, documented nowhere, and left behind by the PostgreSQL-to-SQL Server migration.
    /// Because it was always unset, every <c>dotnet ef</c> command fell silently through to the
    /// localhost string below and timed out against a sidecar that need not even be running, whatever
    /// database the operator believed they were pointing at.
    /// </remarks>
    private const string ConnectionStringVariable = "ConnectionStrings__Database";

    /// <summary>
    /// The docker-compose development database, as the migrator principal — the one that owns the
    /// schema, since design-time commands issue DDL. Deliberately public, deliberately weak, and
    /// deliberately local: it matches docker-compose.override.yml, and the production password is
    /// required from the environment (see docker-compose.prod.yml).
    /// </summary>
    private const string DevelopmentConnectionString =
        "Server=localhost,1433;Database=GymOS_Local;User Id=gymos_migrator;Password=Gymos_dev_migrator_password1;" +
        "Encrypt=True;TrustServerCertificate=True;Current Language=us_english";

    public AppDbContext CreateDbContext(string[] args)
    {
        string connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable)
            ?? DevelopmentConnectionString;

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString, sqlServer => sqlServer
                .MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name!)
                .MigrationsHistoryTable(
                    PlatformInfrastructureServiceCollectionExtensions.HistoryTableName,
                    PlatformInfrastructureServiceCollectionExtensions.PlatformSchema))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options);
    }
}
