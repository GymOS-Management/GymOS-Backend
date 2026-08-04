using GymOS.BuildingBlocks.Infrastructure.Outbox;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GymOS.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class MigrationTests
{
    private readonly PostgresFixture _postgres;

    public MigrationTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Migrations_ApplyCleanly_AndAreIdempotent()
    {
        string connectionString = await _postgres.CreateDatabaseAsync("migration_tests");
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name!))
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var context = new AppDbContext(options))
        {
            await context.Database.MigrateAsync();
            await context.Database.MigrateAsync(); // second run must be a no-op
        }

        await using (var context = new AppDbContext(options))
        {
            // Both outbox tables exist and are queryable through the model.
            Assert.False(await context.Set<OutboxMessage>().AnyAsync());
            Assert.False(await context.Set<OutboxDelivery>().AnyAsync());
        }
    }
}
