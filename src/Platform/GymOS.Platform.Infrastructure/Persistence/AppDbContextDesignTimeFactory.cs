using GymOS.Platform.Infrastructure.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GymOS.Platform.Infrastructure.Persistence;

/// <summary>
/// Used exclusively by <c>dotnet ef</c> at design time so migrations never bootstrap
/// the host. Connection: <c>GYMOS_DATABASE</c> environment variable when set,
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
    /// The docker-compose development database, as the migrator principal — the one that owns the
    /// schema, since design-time commands issue DDL. Deliberately public, deliberately weak, and
    /// deliberately local: it matches docker-compose.override.yml, and the production password is
    /// required from the environment (see docker-compose.prod.yml).
    /// </summary>
    private const string DevelopmentConnectionString =
        "Server=localhost,1433;Database=gymos;User Id=gymos_migrator;Password=Gymos_dev_migrator_password1;" +
        "Encrypt=True;TrustServerCertificate=True;Current Language=us_english";

    public AppDbContext CreateDbContext(string[] args)
    {
        string connectionString = Environment.GetEnvironmentVariable("GYMOS_DATABASE")
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
