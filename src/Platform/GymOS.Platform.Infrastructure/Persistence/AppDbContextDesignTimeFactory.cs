using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GymOS.Platform.Infrastructure.Persistence;

/// <summary>
/// Used exclusively by <c>dotnet ef</c> at design time so migrations never bootstrap
/// the host. Connection: <c>GYMOS_DATABASE</c> environment variable when set,
/// otherwise the docker-compose development database. Never used at runtime.
/// </summary>
public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        string connectionString = Environment.GetEnvironmentVariable("GYMOS_DATABASE")
            ?? "Host=localhost;Port=5432;Database=gymos;Username=gymos;Password=gymos_dev_password";

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name!))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options);
    }
}
