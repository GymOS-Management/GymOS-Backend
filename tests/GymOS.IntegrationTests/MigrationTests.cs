using GymOS.BuildingBlocks.Infrastructure.Outbox;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GymOS.IntegrationTests;

[Collection(SqlServerCollectionDefinition.Name)]
public sealed class MigrationTests
{
    private readonly SqlServerFixture _sqlServer;

    public MigrationTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    [Fact]
    public async Task Migrations_ApplyCleanly_AndAreIdempotent()
    {
        // The one suite that must migrate for itself, because applying migrations *is* its subject.
        // It therefore asks for a database the fixture has deliberately left empty, and connects as the
        // migrator — the app role every other suite uses owns nothing and holds no DDL.
        await using SqlServerFixture.DatabaseLease appConnectionStringLease = await _sqlServer.LeaseUnmigratedDatabaseAsync("migration_tests");
        string appConnectionString = appConnectionStringLease.ConnectionString;

        DbContextOptions<AppDbContext> migratorOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(SqlServerFixture.AsMigrator(appConnectionString), TestDatabase.Options)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var context = new AppDbContext(migratorOptions))
        {
            await context.Database.MigrateAsync();
            await context.Database.MigrateAsync(); // second run must be a no-op
        }

        // Read back as the app role: the tables must be reachable by the role that actually serves
        // requests, which is a stronger statement than the migrator being able to see what it created.
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(appConnectionString, TestDatabase.Options)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var context = new AppDbContext(options))
        {
            // Both outbox tables exist and are queryable through the model.
            Assert.False(await context.Set<OutboxMessage>().AnyAsync());
            Assert.False(await context.Set<OutboxDelivery>().AnyAsync());
        }
    }
}
