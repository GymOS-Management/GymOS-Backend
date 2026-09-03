using System.Globalization;
using System.Text;
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Invoices;
using GymOS.Platform.Application.Invoices.Abstractions;
using GymOS.Platform.Domain.Analytics;
using GymOS.Platform.Domain.Features;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Invoices;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel.Currencies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace GymOS.Platform.Infrastructure.Persistence.Seeding.DemoData;

/// <summary>What <see cref="DemoDataSeeder.SeedAsync"/> did.</summary>
public enum DemoDataSeedOutcome
{
    /// <summary>The portfolio was empty and has been seeded.</summary>
    Seeded,

    /// <summary>Gyms or plans already exist; nothing was written.</summary>
    SkippedPortfolioNotEmpty,
}

/// <summary>
/// Fills an <b>empty</b> portfolio with the demo dataset — plans, gyms, subscriptions, invoices,
/// feature overrides and ninety days of platform stats — so the consoles have something true to show.
/// Design: <c>docs/superpowers/specs/2026-09-03-demo-data-seeder-design.md</c>.
///
/// <para>Every object is created through its aggregate's factory and lifecycle methods, and every
/// invoice number comes from the same stored procedure the API calls. The seeder never <c>INSERT</c>s
/// past an invariant; the one thing it does behind the domain's back is backdating audit timestamps,
/// and §5.2 of the design says why that is metadata rather than a rule.</para>
///
/// <para>Run as a host mode (<c>Seed:DemoData=true</c>) by <c>Program.cs</c>, after the boot-time
/// seeders, as the application principal. Refuses Production by environment name and by database
/// name, throws on pending migrations, and does nothing at all when any gym or plan exists — see
/// <see cref="SeedAsync"/>.</para>
/// </summary>
public sealed partial class DemoDataSeeder
{
    /// <summary>How many days of history precede today. Long enough for every analytics range.</summary>
    internal const int HistoryDays = 90;

    private static readonly CompositeFormat PeriodDescription =
        CompositeFormat.Parse("{0} — {1:yyyy-MM-dd} to {2:yyyy-MM-dd}");

    private readonly AppDbContext _dbContext;
    private readonly IInvoiceNumberGenerator _invoiceNumbers;
    private readonly IClock _clock;
    private readonly ILogger<DemoDataSeeder> _logger;

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Demo data seeding skipped: the portfolio is not empty ({Gyms} gyms, {Plans} plans). " +
        "The seeder only fills an empty database; nothing was written.")]
    private static partial void LogSkippedNotEmpty(ILogger logger, int gyms, int plans);

    [LoggerMessage(Level = LogLevel.Information, Message =
        "Demo data seeded: {Plans} plans, {Gyms} gyms ({Living} living), {Subscriptions} subscriptions, " +
        "{Invoices} invoices, {Overrides} feature overrides, {StatsDays} days of platform stats.")]
    private static partial void LogSeeded(
        ILogger logger, int plans, int gyms, int living, int subscriptions, int invoices, int overrides, int statsDays);

    public DemoDataSeeder(
        AppDbContext dbContext,
        IInvoiceNumberGenerator invoiceNumbers,
        IClock clock,
        ILogger<DemoDataSeeder> logger)
    {
        _dbContext = dbContext;
        _invoiceNumbers = invoiceNumbers;
        _clock = clock;
        _logger = logger;
    }

    /// <param name="environmentName">
    /// The host's environment name, passed in rather than resolved here so the refusal is testable
    /// and so this assembly does not take a hosting dependency for one string.
    /// </param>
    public async Task<DemoDataSeedOutcome> SeedAsync(string environmentName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        RefuseProduction(environmentName);

        // Throws rather than skips: the boot-time seeders skip politely because they run on every
        // start, but this one runs because an operator asked, and "finished" against a schema that is
        // behind would be a lie.
        if ((await _dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
        {
            throw new InvalidOperationException(
                "Refusing to seed demo data: the database has pending migrations. Run the migrator first.");
        }

        int existingGyms = await _dbContext.Set<Gym>().CountAsync(cancellationToken);
        int existingPlans = await _dbContext.Set<SubscriptionPlan>().CountAsync(cancellationToken);
        if (existingGyms > 0 || existingPlans > 0)
        {
            LogSkippedNotEmpty(_logger, existingGyms, existingPlans);
            return DemoDataSeedOutcome.SkippedPortfolioNotEmpty;
        }

        DateTimeOffset now = _clock.UtcNow;
        DemoPortfolio portfolio = DemoPortfolio.Build(now);

        Dictionary<string, Feature> features = await _dbContext.Set<Feature>()
            .ToDictionaryAsync(feature => feature.Key, StringComparer.Ordinal, cancellationToken);

        // One transaction: a failure part-way leaves an empty portfolio, which is the only state the
        // next run knows how to fill.
        await using IDbContextTransaction transaction =
            await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        Dictionary<string, SubscriptionPlan> plans = SeedPlans(portfolio, features);

        var gyms = new List<Gym>();
        int subscriptions = 0;
        int invoices = 0;
        int overrides = 0;

        foreach (DemoGym spec in portfolio.Gyms)
        {
            Gym gym = Gym.Provision(
                spec.Slug,
                spec.Name,
                new GymOwnerContact(spec.OwnerName, spec.OwnerEmail, spec.Phone),
                GymLocale.Of("CY", "Asia/Nicosia", DemoPortfolio.Currency.Code));

            if (spec.ActivatedAt is not null)
            {
                gym.Activate();
            }

            _dbContext.Set<Gym>().Add(gym);
            gyms.Add(gym);

            if (spec.Subscription is { } subscriptionSpec)
            {
                invoices += await SeedSubscriptionAsync(
                    gym, subscriptionSpec, plans[subscriptionSpec.PlanCode], now, cancellationToken);
                subscriptions++;
            }

            // Suspension and deletion after the subscription, as they happen in life: the
            // subscription's own terminal state is recorded above with its own instant.
            if (spec.SuspendedAt is not null)
            {
                gym.Suspend(spec.SuspensionReason!);
            }

            if (spec.DeletedAt is { } deletedAt)
            {
                gym.Delete(deletedAt);
            }

            foreach (DemoOverride overrideSpec in spec.Overrides)
            {
                Feature feature = FeatureFor(features, overrideSpec.FeatureKey);
                _dbContext.Set<GymFeatureOverride>().Add(GymFeatureOverride.Grant(
                    gym.Id,
                    feature.Id,
                    feature.Key,
                    overrideSpec.IsEnabled,
                    overrideSpec.Reason,
                    setByUserId: null,
                    spec.ActivatedAt ?? spec.ProvisionedAt));
                overrides++;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        await BackdateTimestampsAsync(portfolio, gyms, cancellationToken);

        int statsDays = SeedStats(portfolio, now);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        int living = gyms.Count(gym => !gym.IsDeleted);
        LogSeeded(
            _logger,
            plans.Count,
            gyms.Count,
            living,
            subscriptions,
            invoices,
            overrides,
            statsDays);

        return DemoDataSeedOutcome.Seeded;
    }

    /// <summary>
    /// Production is refused by both names it goes by. No override exists on purpose: a demo
    /// portfolio in a production tenant list is not a mistake one more flag should be able to make.
    /// </summary>
    private void RefuseProduction(string environmentName)
    {
        string database = _dbContext.Database.GetDbConnection().Database;

        if (environmentName.Equals("Production", StringComparison.OrdinalIgnoreCase)
            || DatabaseEnvironmentGuard.ProductionDatabase.Equals(database, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to seed demo data: environment '{environmentName}', database '{database}'. " +
                "Demo data is for Development and UAT only and is never written to Production.");
        }
    }

    private Dictionary<string, SubscriptionPlan> SeedPlans(DemoPortfolio portfolio, Dictionary<string, Feature> features)
    {
        var plans = new Dictionary<string, SubscriptionPlan>(StringComparer.Ordinal);

        foreach (DemoPlan spec in portfolio.Plans)
        {
            SubscriptionPlan plan = SubscriptionPlan.Create(
                spec.Code, spec.Name, spec.Description, spec.TrialDays, spec.Limits, spec.SortOrder);

            foreach (DemoPrice price in spec.Prices)
            {
                plan.SetPrice(price.Cycle, price.Price);
            }

            foreach (string key in spec.EnabledFeatureKeys)
            {
                plan.SetFeature(FeatureFor(features, key).Id, isEnabled: true);
            }

            _dbContext.Set<SubscriptionPlan>().Add(plan);
            plans[spec.Code] = plan;
        }

        return plans;
    }

    /// <summary>
    /// Assigns, converts, bills period by period up to the subscription's end, settles the invoices,
    /// then applies the terminal state — in the order the platform itself would have taken.
    /// </summary>
    /// <returns>The number of invoices issued.</returns>
    private async Task<int> SeedSubscriptionAsync(
        Gym gym,
        DemoSubscription spec,
        SubscriptionPlan plan,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        PlanPrice price = plan.Prices.Single(candidate => candidate.BillingCycle == spec.Cycle);
        GymSubscription subscription = GymSubscription.Assign(gym.Id, plan, SubscriptionTerms.From(price), spec.AssignedAt);

        if (spec.ConvertedAt is not null && subscription.Status == SubscriptionStatus.Trialing)
        {
            subscription.ConvertTrial();
        }

        // Where the subscription's story ends for billing purposes: its cancellation or expiry, or
        // now for one still live. Periods are renewed up to and including the one containing it.
        DateTimeOffset endsAt = spec.CanceledAt ?? spec.ExpiredAt ?? now;
        var issued = new List<Invoice>();

        if (spec.ConvertedAt is { } convertedAt)
        {
            while (true)
            {
                // The first period may have started under trial; its invoice is dated at conversion.
                DateTimeOffset issuedAt = subscription.CurrentPeriodStartUtc > convertedAt
                    ? subscription.CurrentPeriodStartUtc
                    : convertedAt;

                issued.Add(await IssueAsync(gym, subscription, plan, issuedAt, cancellationToken));

                if (!subscription.PeriodHasEnded(endsAt))
                {
                    break;
                }

                subscription.Renew();
            }

            Settle(issued, spec, endsAt, now);
        }

        if (spec.PastDueAt is not null)
        {
            subscription.MarkPastDue();
        }

        if (spec.CanceledAt is { } canceledAt)
        {
            subscription.CancelNow(spec.CancellationReason, canceledAt);
        }

        if (spec.ExpiredAt is { } expiredAt)
        {
            subscription.Expire(expiredAt);
        }

        _dbContext.Set<GymSubscription>().Add(subscription);
        return issued.Count;
    }

    private async Task<Invoice> IssueAsync(
        Gym gym,
        GymSubscription subscription,
        SubscriptionPlan plan,
        DateTimeOffset issuedAt,
        CancellationToken cancellationToken)
    {
        // The same description InvoiceIssuer writes, so demo invoices read like real ones.
        string description = string.Format(
            CultureInfo.InvariantCulture,
            PeriodDescription,
            plan.Name,
            subscription.CurrentPeriodStartUtc,
            subscription.CurrentPeriodEndUtc);

        Invoice invoice = Invoice.Issue(
            gym.Id,
            subscription.Id,
            await _invoiceNumbers.NextAsync(issuedAt.Year, cancellationToken),
            [new InvoiceLineSpec(description, 1, subscription.Price)],
            InvoiceIssuer.CurrentTaxRate,
            issuedAt,
            issuedAt.AddDays(InvoicePaymentTerms.Days));

        _dbContext.Set<Invoice>().Add(invoice);
        return invoice;
    }

    /// <summary>Design §5.1: every invoice but the last is paid; the last one tells the gym's story.</summary>
    private static void Settle(List<Invoice> issued, DemoSubscription spec, DateTimeOffset endsAt, DateTimeOffset now)
    {
        for (int index = 0; index < issued.Count - 1; index++)
        {
            Pay(issued[index], index);
        }

        Invoice last = issued[^1];

        if (spec.VoidsLastInvoice)
        {
            last.MarkVoid();
        }
        else if (spec.PastDueAt is not null)
        {
            // Past due or expired: the last invoice went unpaid, and is overdue as of where the
            // story ended (now for a past-due subscription, the expiry for an expired one).
            if (endsAt >= last.DueAtUtc)
            {
                last.MarkOverdue(endsAt);
            }
        }
        else if (spec.CanceledAt is not null || now >= last.DueAtUtc)
        {
            // Settled up on the way out, or a healthy gym whose due date has passed. A healthy gym
            // whose current invoice is not yet due keeps it open — that is the open invoice the
            // consoles show, with no special case to produce it.
            Pay(last, issued.Count - 1);
        }
    }

    private static void Pay(Invoice invoice, int index) =>
        invoice.MarkPaid(
            string.Create(CultureInfo.InvariantCulture, $"DEMO-{invoice.InvoiceNumber}"),
            invoice.IssuedAtUtc.AddDays(2 + (index % 6)));

    /// <summary>
    /// Design §5.2. <c>AuditableTimestampsInterceptor</c> stamps every insert with the clock, and the
    /// stats compiler counts new gyms by that column, so a portfolio "created today" would report
    /// thirteen new gyms this morning. Typed <c>ExecuteUpdate</c> rather than SQL text: a renamed
    /// column is a compile error, not a silent no-op.
    /// </summary>
    private async Task BackdateTimestampsAsync(DemoPortfolio portfolio, List<Gym> gyms, CancellationToken cancellationToken)
    {
        List<Guid> gymIds = gyms.ConvertAll(gym => gym.Id);

        foreach ((Gym gym, DemoGym spec) in gyms.Zip(portfolio.Gyms))
        {
            await _dbContext.Set<Gym>()
                .Where(row => row.Id == gym.Id)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(row => row.CreatedAtUtc, spec.ProvisionedAt)
                        .SetProperty(row => row.UpdatedAtUtc, spec.LastChangedAt),
                    cancellationToken);
        }

        await _dbContext.Set<GymSubscription>()
            .Where(row => gymIds.Contains(row.GymId))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(row => row.CreatedAtUtc, row => row.PeriodAnchorUtc)
                    .SetProperty(row => row.UpdatedAtUtc, row => row.CanceledAtUtc ?? row.ExpiredAtUtc ?? row.CurrentPeriodStartUtc),
                cancellationToken);

        await _dbContext.Set<Invoice>()
            .Where(row => gymIds.Contains(row.GymId))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(row => row.CreatedAtUtc, row => row.IssuedAtUtc)
                    .SetProperty(row => row.UpdatedAtUtc, row => row.PaidAtUtc ?? row.IssuedAtUtc),
                cancellationToken);

        await _dbContext.Set<SubscriptionPlan>()
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(row => row.CreatedAtUtc, portfolio.CatalogueCreatedAt)
                    .SetProperty(row => row.UpdatedAtUtc, portfolio.CatalogueCreatedAt),
                cancellationToken);
    }

    /// <summary>Design §5.3: ninety rows, the last for yesterday; the nightly job takes it from there.</summary>
    private int SeedStats(DemoPortfolio portfolio, DateTimeOffset now)
    {
        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);

        for (int daysBack = HistoryDays; daysBack >= 1; daysBack--)
        {
            DateOnly day = today.AddDays(-daysBack);
            (GymCounts counts, IReadOnlyList<Money> mrr) = DemoTimeline.Compile(portfolio, day);
            _dbContext.Set<DailyPlatformStats>().Add(DailyPlatformStats.Record(day, counts, mrr, now));
        }

        return HistoryDays;
    }

    private static Feature FeatureFor(Dictionary<string, Feature> features, string key) =>
        features.TryGetValue(key, out Feature? feature)
            ? feature
            : throw new InvalidOperationException(
                $"Demo data references feature '{key}', which is not in the catalogue. " +
                "The feature catalogue seeder must run before demo data.");
}
