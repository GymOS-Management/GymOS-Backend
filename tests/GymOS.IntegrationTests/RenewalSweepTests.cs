using GymOS.BuildingBlocks.Infrastructure.Time;
using GymOS.Platform.Application.Invoices;
using GymOS.Platform.Application.Subscriptions;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Invoices;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.Platform.Infrastructure.Invoices;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.Platform.Infrastructure.Persistence.Queries;
using GymOS.Platform.Infrastructure.Persistence.Repositories;
using GymOS.SharedKernel.Currencies;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The renewal sweep end to end against real PostgreSQL: a subscription assigned in the past
/// renews through the real repositories, unit of work, number sequence, and invoice tables — one
/// transaction per subscription, exactly as the job runs it.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class RenewalSweepTests : IAsyncLifetime
{
    private static readonly SystemClock Clock = new();

    private readonly PostgresFixture _postgres;
    private string _connectionString = null!;

    public RenewalSweepTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _connectionString = await _postgres.CreateIsolatedDatabaseAsync("renewal_sweep");
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name!))
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AppDbContext(options);
    }

    private static SubscriptionRenewalService Service(AppDbContext context) => new(
        new SubscriptionRepository(context),
        new PlanRepository(context),
        new InvoiceIssuer(
            new InvoiceRepository(context), new InvoiceNumberGenerator(context), Clock),
        new UnitOfWork(context));

    /// <summary>A gym on a monthly plan whose period began in the past — due when backdated enough.</summary>
    private async Task<Guid> SeedSubscriptionAssignedAtAsync(DateTimeOffset assignedAt)
    {
        await using AppDbContext context = CreateContext();

        Gym gym = Gym.Provision(
            $"gym-{Guid.NewGuid():N}",
            "Renewal Test Gym",
            new GymOwnerContact("Owner", "owner@example.test", null),
            GymLocale.Of("CY", "Asia/Nicosia", "EUR"));

        SubscriptionPlan plan = SubscriptionPlan.Create(
            $"R{Guid.NewGuid():N}"[..12].ToUpperInvariant(), "Renewal Plan", "", 0, PlanLimits.Unlimited);
        plan.SetPrice(BillingCycle.Monthly, Money.Of(49.99m, Currency.Euro));

        GymSubscription subscription = GymSubscription.Assign(
            gym.Id, plan, SubscriptionTerms.From(plan.Prices.Single()), assignedAt);

        context.Add(gym);
        context.Add(plan);
        context.Add(subscription);
        await context.SaveChangesAsync();

        return subscription.Id;
    }

    [Fact]
    public async Task ADueSubscriptionRenewsOnceAndBillsTheNewPeriod_ThenTheSweepGoesQuiet()
    {
        // Assigned five weeks ago: exactly one period boundary has passed. Whole seconds, because
        // timestamptz keeps microseconds while DateTimeOffset carries 100ns ticks — comparing a
        // computed value against a round-tripped one must not hinge on the truncated tail.
        DateTimeOffset now = Clock.UtcNow;
        DateTimeOffset assignedAt = now.AddDays(-35).AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
        Guid subscriptionId = await SeedSubscriptionAssignedAtAsync(assignedAt);

        // The candidate query finds it...
        await using (AppDbContext context = CreateContext())
        {
            IReadOnlyList<Guid> due = await new SubscriptionQueries(context)
                .ListDueForRenewalAsync(Clock.UtcNow, CancellationToken.None);
            Assert.Contains(subscriptionId, due);
        }

        // ...one step renews it...
        await using (AppDbContext context = CreateContext())
        {
            RenewalAction action = await Service(context)
                .ProcessAsync(subscriptionId, Clock.UtcNow, CancellationToken.None);
            Assert.Equal(RenewalAction.Renewed, action);
        }

        // ...and the state that comes back proves the whole chain hit the database.
        await using (AppDbContext verify = CreateContext())
        {
            GymSubscription renewed = await verify.Set<GymSubscription>()
                .SingleAsync(subscription => subscription.Id == subscriptionId);

            Assert.Equal(assignedAt.AddMonths(1), renewed.CurrentPeriodStartUtc);
            Assert.Equal(assignedAt.AddMonths(2), renewed.CurrentPeriodEndUtc);
            Assert.True(renewed.CurrentPeriodEndUtc > Clock.UtcNow); // no longer due

            Invoice invoice = await verify.Set<Invoice>()
                .Include(candidate => candidate.Lines)
                .SingleAsync(candidate => candidate.SubscriptionId == subscriptionId);
            Assert.Matches(@"^\d{4}-\d{6}$", invoice.InvoiceNumber);
            Assert.Equal(InvoiceStatus.Issued, invoice.Status);
            Assert.Equal(Money.Of(49.99m, Currency.Euro), invoice.Total);

            // The sweep is quiet again: renewing consumed the very condition that selected it.
            IReadOnlyList<Guid> due = await new SubscriptionQueries(verify)
                .ListDueForRenewalAsync(Clock.UtcNow, CancellationToken.None);
            Assert.DoesNotContain(subscriptionId, due);
        }
    }

    [Fact]
    public async Task ASubscriptionInsideItsPeriod_IsNotACandidate()
    {
        Guid subscriptionId = await SeedSubscriptionAssignedAtAsync(Clock.UtcNow.AddDays(-3));

        await using AppDbContext context = CreateContext();
        IReadOnlyList<Guid> due = await new SubscriptionQueries(context)
            .ListDueForRenewalAsync(Clock.UtcNow, CancellationToken.None);

        Assert.DoesNotContain(subscriptionId, due);
    }
}
