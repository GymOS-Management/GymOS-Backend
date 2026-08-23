using GymOS.Platform.Infrastructure.DependencyInjection;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace GymOS.IntegrationTests;

/// <summary>
/// The SQL Server provider options every context in this suite is built with.
/// </summary>
/// <remarks>
/// <para>One place, because these options are not decoration. The migrations history table lives in
/// the <c>platform</c> schema rather than <c>dbo</c> (see
/// <see cref="PlatformInfrastructureServiceCollectionExtensions"/>), and a context configured
/// without that would read a <em>different</em> history table from the one the host under test
/// writes. The failure that produces is quiet and misleading: the database is fully migrated, the
/// host reports every migration as pending, R14's validator takes its skip path, and the seeders
/// all decline to run — so a suite would exercise an empty database while looking like it had set
/// one up.</para>
/// <para>The PostgreSQL fixture had no equivalent because it left the history table at its default,
/// so every <c>UseNpgsql</c> call site could safely spell out just the migrations assembly. That is
/// why this is a new file rather than a renamed one.</para>
/// </remarks>
internal static class TestDatabase
{
    /// <summary>
    /// Configures a <c>UseSqlServer</c> call to match <c>AddPlatformPersistence</c>.
    /// </summary>
    public static void Options(SqlServerDbContextOptionsBuilder sqlServer)
    {
        ArgumentNullException.ThrowIfNull(sqlServer);

        sqlServer
            .MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name!)
            .MigrationsHistoryTable(
                PlatformInfrastructureServiceCollectionExtensions.HistoryTableName,
                PlatformInfrastructureServiceCollectionExtensions.PlatformSchema);
    }
}
