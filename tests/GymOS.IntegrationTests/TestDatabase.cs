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

    /// <summary>
    /// Renders one index's definition as a single string, in the spirit of PostgreSQL's
    /// <c>pg_indexes.indexdef</c>: uniqueness, name, table, key columns in order, and the filter.
    /// </summary>
    /// <remarks>
    /// <para>Several suites assert that a hand-written index still exists and still says what the
    /// migration said — the guard against an index silently ceasing to enforce its invariant. Under
    /// PostgreSQL each of them read <c>indexdef</c>, a ready-made <c>CREATE INDEX</c> string. SQL
    /// Server's catalog has no equivalent column, so the string is assembled here from
    /// <c>sys.indexes</c> and <c>sys.index_columns</c> — once, rather than four times with four
    /// slightly different queries.</para>
    /// <para>Key columns only: <c>is_included_column = 0</c> excludes INCLUDE columns, which are
    /// payload rather than part of what the index enforces. Ordered by <c>key_ordinal</c>, because
    /// column order is the property some of these tests are actually about.</para>
    /// </remarks>
    public const string IndexDefinitionSql =
        """
        SELECT CONCAT(
                   CASE WHEN i.is_unique = 1 THEN 'UNIQUE ' ELSE '' END,
                   'INDEX ', i.name,
                   ' ON ', SCHEMA_NAME(t.schema_id), '.', t.name,
                   ' (',
                   (SELECT STRING_AGG(c.name, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal)
                      FROM sys.index_columns ic
                      JOIN sys.columns c
                        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                     WHERE ic.object_id = i.object_id
                       AND ic.index_id = i.index_id
                       AND ic.is_included_column = 0),
                   ')',
                   ISNULL(' WHERE ' + i.filter_definition, ''))
        FROM sys.indexes i
        JOIN sys.tables t ON t.object_id = i.object_id
        WHERE SCHEMA_NAME(t.schema_id) = @schema
          AND t.name = @table
          AND i.name = @index
        """;
}
