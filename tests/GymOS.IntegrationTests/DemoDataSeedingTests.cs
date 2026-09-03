using System.Net;
using System.Text.Json;
using GymOS.Platform.Domain.Analytics;
using GymOS.Platform.Domain.Features;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Invoices;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The demo-data host mode (<c>Seed:DemoData</c>) against real SQL Server: it fills an empty
/// portfolio with the shape the design promises, the consoles can then read it, it writes nothing
/// the second time, and Production is refused before anything is read.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class DemoDataSeedingTests
{
    private static readonly Uri DashboardSummary = new("/api/v1/dashboard/summary", UriKind.Relative);

    private readonly SqlServerFixture _sqlServer;

    public DemoDataSeedingTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    [Fact]
    public async Task SeedDemoData_FillsAnEmptyPortfolio_WithEveryStateTheDesignPromises()
    {
        string connectionString = await _sqlServer.CreateDatabaseAsync("demo_seed");

        await SeedAsync(connectionString);

        await using AppDbContext context = CreateContext(connectionString);

        List<DailyPlatformStats> stats = await context.Set<DailyPlatformStats>()
            .Include(row => row.Mrr)
            .AsNoTracking()
            .OrderBy(row => row.StatDate)
            .ToListAsync();
        Assert.Equal(90, stats.Count);

        // The seed's own "now": every stats row is stamped with the instant the portfolio was built
        // against, which is the clock the rest of these assertions must be judged by (AD-40).
        DateTimeOffset now = stats[^1].CreatedAtUtc;

        List<Gym> gyms = await context.Set<Gym>().AsNoTracking().ToListAsync();
        Assert.Equal(13, gyms.Count);
        Assert.All(gyms, gym => Assert.StartsWith("demo-", gym.Slug, StringComparison.Ordinal));
        Assert.Equal(10, gyms.Count(gym => gym.Status == GymStatus.Active));
        Assert.Equal(1, gyms.Count(gym => gym.Status == GymStatus.Suspended));
        Assert.Equal(1, gyms.Count(gym => gym.Status == GymStatus.Provisioning));
        Assert.Equal(1, gyms.Count(gym => gym.Status == GymStatus.Deleted));

        // Backdated (design §5.2): the portfolio was not all created this morning.
        Assert.True(gyms.Min(gym => gym.CreatedAtUtc) < now.AddDays(-150));

        Assert.Equal(3, await context.Set<SubscriptionPlan>().CountAsync());

        List<GymSubscription> subscriptions = await context.Set<GymSubscription>().AsNoTracking().ToListAsync();
        Assert.Equal(12, subscriptions.Count);
        Assert.Equal(1, subscriptions.Count(s => s.Status == SubscriptionStatus.Trialing));
        Assert.Equal(7, subscriptions.Count(s => s.Status == SubscriptionStatus.Active));
        Assert.Equal(1, subscriptions.Count(s => s.Status == SubscriptionStatus.PastDue));
        Assert.Equal(2, subscriptions.Count(s => s.Status == SubscriptionStatus.Canceled));
        Assert.Equal(1, subscriptions.Count(s => s.Status == SubscriptionStatus.Expired));

        // Every live subscription's current period contains now: the periods were renewed forward.
        Assert.All(
            subscriptions.Where(s => s.IsLive),
            s => Assert.True(s.CurrentPeriodStartUtc <= now && now < s.CurrentPeriodEndUtc, s.Id.ToString()));

        List<Invoice> invoices = await context.Set<Invoice>().AsNoTracking().ToListAsync();
        Assert.NotEmpty(invoices);
        Assert.Contains(invoices, invoice => invoice.Status == InvoiceStatus.Paid);
        Assert.Contains(invoices, invoice => invoice.Status == InvoiceStatus.Issued);
        Assert.Contains(invoices, invoice => invoice.Status == InvoiceStatus.Overdue);
        Assert.Contains(invoices, invoice => invoice.Status == InvoiceStatus.Void);
        Assert.All(invoices, invoice => Assert.True(invoice.IssuedAtUtc <= now));
        Assert.All(invoices.Where(i => i.PaidAtUtc is not null), invoice => Assert.True(invoice.PaidAtUtc <= now));

        // Numbers came from the real sequence: distinct, and in the platform's format.
        Assert.Equal(invoices.Count, invoices.Select(invoice => invoice.InvoiceNumber).Distinct(StringComparer.Ordinal).Count());
        Assert.All(invoices, invoice => Assert.Matches(@"^\d{4}-\d{6}$", invoice.InvoiceNumber));

        Assert.Equal(2, await context.Set<GymFeatureOverride>().CountAsync());

        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        Assert.Equal(today.AddDays(-1), stats[^1].StatDate);
        Assert.Equal(today.AddDays(-90), stats[0].StatDate);

        // Yesterday's row is today's living population: the history and the rows agree.
        DailyPlatformStats yesterday = stats[^1];
        Assert.Equal(12, yesterday.TotalGyms);
        Assert.Equal(10, yesterday.ActiveGyms);
        Assert.Equal(1, yesterday.SuspendedGyms);
        Assert.Equal(1, yesterday.ProvisioningGyms);
        Assert.Single(yesterday.Mrr);
        Assert.Equal("EUR", yesterday.Mrr.Single().Amount.Currency.Code);

        // The two churn events inside the window are in the series, on their days, not lost.
        Assert.Equal(2, stats.Sum(row => row.CanceledGyms));
        Assert.Equal(1, stats.Sum(row => row.ExpiredGyms));
        Assert.True(stats.Sum(row => row.NewGyms) >= 4, "gyms provisioned inside the 90-day window must appear as new");
    }

    [Fact]
    public async Task AfterSeeding_TheDashboardHasSomethingToShow()
    {
        string connectionString = await _sqlServer.CreateDatabaseAsync("demo_seed_dashboard");
        await SeedAsync(connectionString);

        // A normal host on the seeded database — the shape a developer or a UAT prospect sees.
        await using AuthenticationHost host = AuthenticationHost.Create(connectionString);
        using HttpClient client = host.CreateClient();

        using HttpResponseMessage ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        using HttpResponseMessage signIn = await AuthApi.LoginAsync(
            client, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);
        AuthApi.Authorize(client, await AuthApi.ReadAccessTokenAsync(signIn));

        using HttpResponseMessage summary = await client.GetAsync(DashboardSummary);
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await summary.Content.ReadAsStringAsync());
        Assert.Equal(12, body.RootElement.GetProperty("totalGyms").GetInt32());
    }

    [Fact]
    public async Task SeedDemoData_WritesNothing_WhenThePortfolioIsNotEmpty()
    {
        string connectionString = await _sqlServer.CreateDatabaseAsync("demo_seed_twice");

        await SeedAsync(connectionString);
        await SeedAsync(connectionString);

        await using AppDbContext context = CreateContext(connectionString);
        Assert.Equal(13, await context.Set<Gym>().CountAsync());
        Assert.Equal(3, await context.Set<SubscriptionPlan>().CountAsync());
        Assert.Equal(90, await context.Set<DailyPlatformStats>().CountAsync());
    }

    [Fact]
    public async Task SeedDemoData_RefusesAProductionHost_BeforeWritingAnything()
    {
        string connectionString = await _sqlServer.CreateDatabaseAsync("demo_seed_production");

        // The environment guard lets a Production host open an unmanaged test database, so the
        // refusal under test here is the seeder's own, not the guard's.
        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("ConnectionStrings:Database", connectionString);
                builder.UseSetting("Database:MigrateOnStartup", "false");
                builder.UseSetting("Jobs:Enabled", "false");
                builder.UseSetting("Security:Jwt:SigningKey", AuthenticationHost.SigningKey);
                builder.UseSetting("Seed:DemoData", "true");
            });

            _ = factory.Services;
        });

        Assert.Contains(
            Unwrap(thrown),
            exception => exception is InvalidOperationException
                && exception.Message.Contains("demo data", StringComparison.OrdinalIgnoreCase)
                && exception.Message.Contains("Production", StringComparison.Ordinal));

        await using AppDbContext context = CreateContext(connectionString);
        Assert.Equal(0, await context.Set<Gym>().CountAsync());
        Assert.Equal(0, await context.Set<SubscriptionPlan>().CountAsync());
    }

    /// <summary>Boots the host in demo-seed mode; accessing Services is what runs Program's startup.</summary>
    private static async Task SeedAsync(string connectionString)
    {
        await using AuthenticationHost host = AuthenticationHost.Create(connectionString, settings =>
        {
            settings["Seed:DemoData"] = "true";
            settings["Jobs:Enabled"] = "false";
        });

        _ = host.Services;
    }

    private static AppDbContext CreateContext(string connectionString)
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString, TestDatabase.Options)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options);
    }

    private static IEnumerable<Exception> Unwrap(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;

            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions.SelectMany(Unwrap))
                {
                    yield return inner;
                }
            }
        }
    }
}
