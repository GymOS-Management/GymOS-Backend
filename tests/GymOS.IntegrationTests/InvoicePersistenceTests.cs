using GymOS.BuildingBlocks.Application.Models;
using GymOS.BuildingBlocks.Infrastructure.Time;
using GymOS.Platform.Application.Invoices.Abstractions;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Invoices;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.Platform.Infrastructure.Invoices;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.Platform.Infrastructure.Persistence.Queries;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Currencies;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The M3.3 storage contract, checked against real PostgreSQL: the per-year number function hands out
/// distinct numbers under concurrency, an <see cref="Invoice"/> and its lines survive the round trip,
/// and the single-currency CHECK holds what the aggregate promises.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class InvoicePersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Currency Eur = Currency.Euro;

    private readonly PostgresFixture _postgres;
    private string _connectionString = null!;

    public InvoicePersistenceTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _connectionString = await _postgres.CreateIsolatedDatabaseAsync("invoice_persistence");
        // Migrations moved into PostgresFixture, which applies them as the migrator before handing
        // back a connection string: the app role every host connects as owns nothing and holds no DDL.
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

    /// <summary>A saved gym + live subscription, so invoice rows have real parents to point at.</summary>
    private async Task<(Guid GymId, Guid SubscriptionId)> SeedGymWithSubscriptionAsync()
    {
        await using AppDbContext context = CreateContext();

        Gym gym = Gym.Provision(
            $"gym-{Guid.NewGuid():N}",
            "Persistence Test Gym",
            new GymOwnerContact("Owner", "owner@example.test", null),
            GymLocale.Of("CY", "Asia/Nicosia", "EUR"));

        SubscriptionPlan plan = SubscriptionPlan.Create(
            $"P{Guid.NewGuid():N}"[..12].ToUpperInvariant(), "Test Plan", "", 0, PlanLimits.Unlimited);

        // PlanPriceId is null on purpose: a real id would need a plan_prices row, and provenance is
        // not what this test is about.
        GymSubscription subscription = GymSubscription.Assign(
            gym.Id, plan, new SubscriptionTerms(null, BillingCycle.Monthly, Money.Of(49.99m, Eur)), Now);

        context.Add(gym);
        context.Add(plan);
        context.Add(subscription);
        await context.SaveChangesAsync();

        return (gym.Id, subscription.Id);
    }

    private static Invoice IssueTwoLineInvoice(Guid gymId, Guid subscriptionId, string number) =>
        Invoice.Issue(
            gymId,
            subscriptionId,
            number,
            [
                new InvoiceLineSpec("Pro — remainder of current period (20 days)", 1, Money.Of(30m, Eur)),
                new InvoiceLineSpec("Credit — unused subscription time (20 of 30 days)", 1, Money.Of(-10m, Eur)),
            ],
            0m,
            Now,
            Now.AddDays(InvoicePaymentTerms.Days));

    [Fact]
    public async Task Number_function_counts_sequentially_within_a_year()
    {
        await using AppDbContext context = CreateContext();
        var generator = new InvoiceNumberGenerator(context);

        Assert.Equal("2026-000001", await generator.NextAsync(2026, CancellationToken.None));
        Assert.Equal("2026-000002", await generator.NextAsync(2026, CancellationToken.None));
        Assert.Equal("2026-000003", await generator.NextAsync(2026, CancellationToken.None));
    }

    [Fact]
    public async Task Each_year_counts_from_one_independently()
    {
        await using AppDbContext context = CreateContext();
        var generator = new InvoiceNumberGenerator(context);

        Assert.Equal("2026-000001", await generator.NextAsync(2026, CancellationToken.None));
        Assert.Equal("2026-000002", await generator.NextAsync(2026, CancellationToken.None));
        Assert.Equal("2027-000001", await generator.NextAsync(2027, CancellationToken.None));
        Assert.Equal("2026-000003", await generator.NextAsync(2026, CancellationToken.None));
    }

    [Fact]
    public async Task Parallel_callers_get_distinct_numbers()
    {
        // Deliberately cold: no warm-up call, so the eight racers also exercise the
        // create-sequence-on-first-use path the function guards against.
        string[] numbers = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(async _ =>
            {
                await using AppDbContext context = CreateContext();
                return await new InvoiceNumberGenerator(context).NextAsync(2026, CancellationToken.None);
            }));

        Assert.Equal(8, numbers.Distinct().Count());
        Assert.All(numbers, number => Assert.Matches(@"^2026-\d{6}$", number));
    }

    [Fact]
    public async Task An_invoice_and_its_lines_survive_the_round_trip()
    {
        (Guid gymId, Guid subscriptionId) = await SeedGymWithSubscriptionAsync();

        Invoice issued = IssueTwoLineInvoice(gymId, subscriptionId, "2026-000042");
        await using (AppDbContext write = CreateContext())
        {
            write.Add(issued);
            await write.SaveChangesAsync();
        }

        await using AppDbContext read = CreateContext();
        Invoice loaded = await read.Set<Invoice>()
            .Include(invoice => invoice.Lines)
            .SingleAsync(invoice => invoice.Id == issued.Id);

        Assert.Equal("2026-000042", loaded.InvoiceNumber);
        Assert.Equal(InvoiceStatus.Issued, loaded.Status);
        Assert.Equal(Money.Of(20m, Eur), loaded.Subtotal);
        Assert.Equal(Money.Of(0m, Eur), loaded.TaxAmount);
        Assert.Equal(Money.Of(20m, Eur), loaded.Total);
        Assert.Equal(2, loaded.Lines.Count);
        Assert.Contains(loaded.Lines, line => line.Amount == Money.Of(-10m, Eur));

        // Cascade is the database's, not EF's: deleting the invoice takes its lines with it.
        read.Remove(loaded);
        await read.SaveChangesAsync();

        await using AppDbContext after = CreateContext();
        Assert.Equal(0, await after.Set<InvoiceLine>().CountAsync(line => line.InvoiceId == issued.Id));
    }

    [Fact]
    public async Task The_single_currency_check_rejects_a_mismatched_row()
    {
        (Guid gymId, Guid subscriptionId) = await SeedGymWithSubscriptionAsync();

        await using AppDbContext context = CreateContext();

        // Hand-written SQL because the aggregate cannot produce this row — that is the point: the
        // CHECK is what holds the invariant against anything that is not the aggregate.
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlAsync($"""
                INSERT INTO platform.invoices
                    (id, gym_id, subscription_id, invoice_number, status, tax_rate,
                     issued_at_utc, due_at_utc, created_at_utc, updated_at_utc,
                     subtotal_amount, subtotal_currency, tax_amount, tax_currency, total_amount, total_currency)
                VALUES
                    ({Guid.NewGuid()}, {gymId}, {subscriptionId}, '2026-000099', 2, 0,
                     {Now}, {Now}, {Now}, {Now},
                     10, 'EUR', 0, 'EUR', 10, 'USD')
                """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("ck_invoices_single_currency", exception.ConstraintName);
    }

    [Fact]
    public async Task The_invoice_number_is_unique_by_index()
    {
        await using AppDbContext context = CreateContext();

        List<string> indexes = await context.Database
            .SqlQueryRaw<string>(
                """
                SELECT indexname AS "Value" FROM pg_indexes
                WHERE schemaname = 'platform' AND tablename = 'invoices'
                """)
            .ToListAsync();

        Assert.Contains("ux_invoices_invoice_number", indexes);
    }

    [Fact]
    public async Task A_subscription_with_a_pending_change_survives_the_round_trip()
    {
        (Guid _, Guid subscriptionId) = await SeedGymWithSubscriptionAsync();

        SubscriptionPlan starter = SubscriptionPlan.Create(
            $"S{Guid.NewGuid():N}"[..12].ToUpperInvariant(), "Starter", "", 0, PlanLimits.Unlimited);

        await using (AppDbContext write = CreateContext())
        {
            write.Add(starter);
            GymSubscription subscription = await write.Set<GymSubscription>()
                .SingleAsync(candidate => candidate.Id == subscriptionId);
            subscription.SchedulePlanChange(
                starter,
                new SubscriptionTerms(null, BillingCycle.Monthly, Money.Of(19.99m, Eur)),
                subscription.CurrentPeriodEndUtc);
            await write.SaveChangesAsync();
        }

        await using AppDbContext read = CreateContext();
        GymSubscription reloaded = await read.Set<GymSubscription>()
            .SingleAsync(candidate => candidate.Id == subscriptionId);

        PendingPlanChange pending = Assert.IsType<PendingPlanChange>(reloaded.PendingChange);
        Assert.Equal(starter.Id, pending.PlanId);
        Assert.Null(pending.PlanPriceId);
        Assert.Equal(BillingCycle.Monthly, pending.BillingCycle);
        Assert.Equal(19.99m, pending.Amount);
        Assert.Equal("EUR", pending.CurrencyCode);
    }

    [Fact]
    public async Task The_gym_invoice_list_is_complete_beyond_one_page()
    {
        (Guid gymId, Guid subscriptionId) = await SeedGymWithSubscriptionAsync();

        // One more than MaxPageSize (200): a single-page implementation silently truncates here.
        const int Count = 201;
        await using (AppDbContext write = CreateContext())
        {
            for (int i = 1; i <= Count; i++)
            {
                write.Add(IssueTwoLineInvoice(gymId, subscriptionId, $"2026-{100000 + i:D6}"));
            }

            await write.SaveChangesAsync();
        }

        await using AppDbContext read = CreateContext();
        var queries = new InvoiceQueries(read, new SystemClock());

        IReadOnlyList<InvoiceListItem> all = await queries.ListForGymAsync(gymId, CancellationToken.None);

        Assert.Equal(Count, all.Count);
        Assert.Equal(Count, all.Select(item => item.InvoiceNumber).Distinct().Count());
    }

    [Fact]
    public async Task Search_matches_like_metacharacters_literally()
    {
        (Guid gymId, Guid subscriptionId) = await SeedGymWithSubscriptionAsync();
        await using (AppDbContext write = CreateContext())
        {
            write.Add(IssueTwoLineInvoice(gymId, subscriptionId, "2026-000001"));
            write.Add(IssueTwoLineInvoice(gymId, subscriptionId, "2026-010101"));
            await write.SaveChangesAsync();
        }

        await using AppDbContext read = CreateContext();
        var queries = new InvoiceQueries(read, new SystemClock());

        // '_' and '%' are LIKE wildcards; unescaped, "0_0" would match both rows and "%" all of them.
        Assert.Empty((await ListSearchingAsync(queries, "0_0")).Items);
        Assert.Empty((await ListSearchingAsync(queries, "%")).Items);
        Assert.Single((await ListSearchingAsync(queries, "01010")).Items);
    }

    private static Task<PagedResult<InvoiceListItem>> ListSearchingAsync(
        InvoiceQueries queries, string search) =>
        queries.ListAsync(
            new InvoiceListFilter(new PageRequest(1, 50, null, null, search), null, null, null),
            CancellationToken.None);

    [Fact]
    public async Task A_subscription_without_a_pending_change_reads_back_null()
    {
        (Guid _, Guid subscriptionId) = await SeedGymWithSubscriptionAsync();

        await using AppDbContext read = CreateContext();
        GymSubscription reloaded = await read.Set<GymSubscription>()
            .SingleAsync(candidate => candidate.Id == subscriptionId);

        Assert.Null(reloaded.PendingChange);
    }
}
