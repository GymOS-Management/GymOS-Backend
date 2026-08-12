using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.DependencyInjection;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.DependencyInjection;
using GymOS.Platform.Application.Features.Abstractions;
using GymOS.Platform.Application.Gyms.Abstractions;
using GymOS.Platform.Application.Invoices.Abstractions;
using GymOS.Platform.Application.Subscriptions;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Application.UnitTests.Fakes;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Invoices;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Currencies;
using GymOS.SharedKernel.Results;
using GymOS.TestingSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GymOS.Platform.Application.UnitTests;

/// <summary>
/// Invoices are created as a consequence, never on request: assigning without a trial bills the first
/// period, converting a trial bills the first period, and a plan change bills the difference — or
/// schedules itself for the period end when the difference favours the gym (the net-sign policy).
/// </summary>
public sealed class InvoiceIssuingTests
{
    private const string Manage = PlatformPermissions.SubscriptionsManage;

    private sealed class Context
    {
        private readonly ServiceProvider _provider;

        public Context(params string[] permissions)
        {
            PlanQueries = new FakePlanQueries(Plans);
            SubscriptionQueries = new FakeSubscriptionQueries(Subscriptions, Plans);

            var services = new ServiceCollection();
            services.AddCqrsDispatcher();
            services.AddAuthorizationBehavior();
            services.AddPlatformApplication();

            services.AddSingleton<IClock>(Clock);
            services.AddSingleton<IGymRepository>(Gyms);
            services.AddSingleton<IGymQueries>(new FakeGymQueries(Gyms));
            services.AddSingleton<IPlanRepository>(Plans);
            services.AddSingleton<IPlanQueries>(PlanQueries);
            services.AddSingleton<ISubscriptionRepository>(Subscriptions);
            services.AddSingleton<ISubscriptionQueries>(SubscriptionQueries);
            services.AddSingleton<IInvoiceRepository>(Invoices);
            services.AddSingleton<IInvoiceNumberGenerator>(new FakeInvoiceNumberGenerator());

            // M4.1: every subscription-changing handler withdraws exceptions its change made
            // redundant. These tests pin invoicing behaviour, not feature resolution, so a no-op
            // fake is enough to satisfy the constructor.
            services.AddSingleton<IFeatureExceptionMaintainer>(FeatureExceptions);

            services.AddSingleton<IUnitOfWork>(UnitOfWork);
            services.AddSingleton<ICurrentUser>(CurrentUser);
            services.AddSingleton<IPermissionEvaluator>(Permissions);
            services.AddSingleton<IAuditTrail>(new FakeAuditTrail());
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

            _provider = services.BuildServiceProvider();

            CurrentUser.UserId = Guid.NewGuid();
            foreach (string permission in permissions)
            {
                Permissions.Granted.Add(permission);
            }
        }

        public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero));

        public FakeGymRepository Gyms { get; } = new();

        public FakePlanRepository Plans { get; } = new();

        public FakeSubscriptionRepository Subscriptions { get; } = new();

        public FakeInvoiceRepository Invoices { get; } = new();

        public FakePlanQueries PlanQueries { get; }

        public FakeSubscriptionQueries SubscriptionQueries { get; }

        public FakeUnitOfWork UnitOfWork { get; } = new();

        public FakeFeatureExceptionMaintainer FeatureExceptions { get; } = new();

        public FakeCurrentUser CurrentUser { get; } = new();

        public FakePermissionEvaluator Permissions { get; } = new();

        public IDispatcher Dispatcher => _provider.GetRequiredService<IDispatcher>();

        public Gym GivenGym(string slug = "acme-gym")
        {
            Gym gym = Gym.Provision(
                slug,
                "Acme Gym",
                new GymOwnerContact("Ada", "ada@acme.test", null),
                new GymLocale("CY", "Asia/Nicosia", "EUR"));

            gym.Activate();
            Gyms.Seed(gym);
            return gym;
        }

        public SubscriptionPlan GivenPlan(string code = "PRO", int trialDays = 0, decimal amount = 49.99m)
        {
            SubscriptionPlan plan = SubscriptionPlan.Create(code, code, "", trialDays, PlanLimits.Unlimited);
            plan.SetPrice(BillingCycle.Monthly, Money.Of(amount, Currency.Euro));
            Plans.Seed(plan);
            return plan;
        }

        public Task<Result<SubscriptionDetail>> AssignAsync(
            Guid gymId, Guid planId, string cycle = "Monthly") =>
            Dispatcher.SendAsync(
                new AssignSubscriptionCommand(gymId, planId, cycle, "EUR", null),
                CancellationToken.None);
    }

    [Fact]
    public async Task AssigningWithoutATrial_IssuesTheFirstPeriodInvoice()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan plan = context.GivenPlan(amount: 49.99m);

        Result<SubscriptionDetail> result = await context.AssignAsync(gym.Id, plan.Id);

        Assert.True(result.IsSuccess);
        Invoice invoice = Assert.Single(context.Invoices.Invoices);
        GymSubscription subscription = Assert.Single(context.Subscriptions.Subscriptions);

        Assert.Equal("2026-000001", invoice.InvoiceNumber);
        Assert.Equal(gym.Id, invoice.GymId);
        Assert.Equal(subscription.Id, invoice.SubscriptionId);
        Assert.Equal(InvoiceStatus.Issued, invoice.Status);

        InvoiceLine line = Assert.Single(invoice.Lines);
        Assert.Equal("PRO — 2026-08-06 to 2026-09-06", line.Description);
        Assert.Equal(Money.Of(49.99m, Currency.Euro), line.UnitPrice);

        Assert.Equal(Money.Of(49.99m, Currency.Euro), invoice.Total);
        Assert.True(invoice.TaxAmount.IsZero);
        Assert.Equal(context.Clock.UtcNow, invoice.IssuedAtUtc);
        Assert.Equal(context.Clock.UtcNow.AddDays(InvoicePaymentTerms.Days), invoice.DueAtUtc);

        // Subscription and invoice commit together or not at all.
        Assert.Equal(1, context.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task AssigningWithATrial_BillsNothing()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan plan = context.GivenPlan(trialDays: 14);

        Result<SubscriptionDetail> result = await context.AssignAsync(gym.Id, plan.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal("Trialing", result.Value.Status);
        Assert.Empty(context.Invoices.Invoices);
    }

    [Fact]
    public async Task ConvertingATrial_IssuesTheFirstPeriodInvoice()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan plan = context.GivenPlan(trialDays: 14, amount: 49.99m);
        await context.AssignAsync(gym.Id, plan.Id);

        Result<SubscriptionDetail> result = await context.Dispatcher.SendAsync(
            new ConvertTrialCommand(gym.Id, null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Active", result.Value.Status);
        Invoice invoice = Assert.Single(context.Invoices.Invoices);
        Assert.Equal(Money.Of(49.99m, Currency.Euro), invoice.Total);
        Assert.Contains(plan.Name, Assert.Single(invoice.Lines).Description);
    }

    [Fact]
    public async Task UpgradingMidPeriod_ChangesThePlanAndBillsTheDifference()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan basic = context.GivenPlan("BASIC", amount: 10m);
        SubscriptionPlan pro = context.GivenPlan("PRO", amount: 20m);
        await context.AssignAsync(gym.Id, basic.Id);
        context.Clock.Advance(TimeSpan.FromDays(10));

        Result<SubscriptionDetail> result = await context.AssignAsync(gym.Id, pro.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(pro.Id, result.Value.PlanId);
        Assert.Null(result.Value.PendingChange);

        Assert.Equal(2, context.Invoices.Invoices.Count);
        Invoice proration = context.Invoices.Invoices[1];
        Assert.Equal(2, proration.Lines.Count);
        Assert.Single(proration.Lines, line => line.Amount.IsNegative);
        Assert.True(proration.Total.Amount > 0m);

        decimal lineSum = proration.Lines.Sum(line => line.Amount.Amount);
        Assert.Equal(lineSum, proration.Total.Amount);
    }

    [Fact]
    public async Task DowngradingMidPeriod_SchedulesTheChangeAndBillsNothing()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan pro = context.GivenPlan("PRO", amount: 20m);
        SubscriptionPlan basic = context.GivenPlan("BASIC", amount: 10m);
        await context.AssignAsync(gym.Id, pro.Id);
        context.Clock.Advance(TimeSpan.FromDays(10));

        Result<SubscriptionDetail> result = await context.AssignAsync(gym.Id, basic.Id);

        Assert.True(result.IsSuccess);

        // Today's plan is untouched — the gym paid to the period end.
        Assert.Equal(pro.Id, result.Value.PlanId);
        GymSubscription subscription = Assert.Single(context.Subscriptions.Subscriptions);
        PendingPlanChange pending = Assert.IsType<PendingPlanChange>(subscription.PendingChange);
        Assert.Equal(basic.Id, pending.PlanId);
        Assert.Equal(subscription.CurrentPeriodEndUtc, pending.ChangeAtUtc);

        // And the read model shows it, so the operator can see what is scheduled.
        PendingPlanChangeDetail detail = Assert.IsType<PendingPlanChangeDetail>(result.Value.PendingChange);
        Assert.Equal(basic.Id, detail.PlanId);
        Assert.Equal(10m, detail.Amount);
        Assert.Equal("EUR", detail.Currency);

        Assert.Single(context.Invoices.Invoices); // only the original assignment invoice
    }

    [Fact]
    public async Task RequestingAThirdPlan_ReplacesThePendingChange()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan pro = context.GivenPlan("PRO", amount: 20m);
        SubscriptionPlan basic = context.GivenPlan("BASIC", amount: 10m);
        SubscriptionPlan starter = context.GivenPlan("STARTER", amount: 5m);
        await context.AssignAsync(gym.Id, pro.Id);
        context.Clock.Advance(TimeSpan.FromDays(10));
        await context.AssignAsync(gym.Id, basic.Id);

        Result<SubscriptionDetail> result = await context.AssignAsync(gym.Id, starter.Id);

        Assert.True(result.IsSuccess);
        GymSubscription subscription = Assert.Single(context.Subscriptions.Subscriptions);
        Assert.Equal(starter.Id, subscription.PendingChange!.PlanId);
    }

    [Fact]
    public async Task RequestingTheCurrentPlanAgain_CancelsAPendingChange()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan pro = context.GivenPlan("PRO", amount: 20m);
        SubscriptionPlan basic = context.GivenPlan("BASIC", amount: 10m);
        await context.AssignAsync(gym.Id, pro.Id);
        context.Clock.Advance(TimeSpan.FromDays(10));
        await context.AssignAsync(gym.Id, basic.Id);

        Result<SubscriptionDetail> result = await context.AssignAsync(gym.Id, pro.Id);

        Assert.True(result.IsSuccess);
        GymSubscription subscription = Assert.Single(context.Subscriptions.Subscriptions);
        Assert.Null(subscription.PendingChange);
        Assert.Null(result.Value.PendingChange);
        Assert.Single(context.Invoices.Invoices);
    }

    [Fact]
    public async Task RequestingTheCurrentPlanWithNothingPending_SucceedsAsANoOp()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan pro = context.GivenPlan("PRO", amount: 20m);
        await context.AssignAsync(gym.Id, pro.Id);

        // This is a PUT: a client whose response was lost to a timeout retries the identical
        // request, and the retry must succeed. Nothing changes, nothing is billed.
        Result<SubscriptionDetail> result = await context.AssignAsync(gym.Id, pro.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(pro.Id, result.Value.PlanId);
        Assert.Null(result.Value.PendingChange);
        Assert.Single(context.Invoices.Invoices);
    }

    [Fact]
    public async Task ChangingPlanDuringATrial_SwapsThePlanAndBillsNothing()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan basic = context.GivenPlan("BASIC", trialDays: 14, amount: 10m);
        SubscriptionPlan pro = context.GivenPlan("PRO", amount: 20m);
        await context.AssignAsync(gym.Id, basic.Id);
        context.Clock.Advance(TimeSpan.FromDays(5));

        Result<SubscriptionDetail> result = await context.AssignAsync(gym.Id, pro.Id);

        // Nothing has been paid, so there is nothing to prorate: the trial continues on the new
        // plan, and conversion bills that plan's full first period.
        Assert.True(result.IsSuccess);
        Assert.Equal(pro.Id, result.Value.PlanId);
        Assert.Equal("Trialing", result.Value.Status);
        Assert.Empty(context.Invoices.Invoices);

        await context.Dispatcher.SendAsync(new ConvertTrialCommand(gym.Id, null), CancellationToken.None);
        Invoice invoice = Assert.Single(context.Invoices.Invoices);
        Assert.Equal(Money.Of(20m, Currency.Euro), invoice.Total);
    }

    [Fact]
    public async Task UpgradingAfterASheduledDowngrade_ClearsThePending()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan pro = context.GivenPlan("PRO", amount: 20m);
        SubscriptionPlan basic = context.GivenPlan("BASIC", amount: 10m);
        SubscriptionPlan elite = context.GivenPlan("ELITE", amount: 40m);
        await context.AssignAsync(gym.Id, pro.Id);
        context.Clock.Advance(TimeSpan.FromDays(10));
        await context.AssignAsync(gym.Id, basic.Id); // scheduled downgrade

        Result<SubscriptionDetail> result = await context.AssignAsync(gym.Id, elite.Id);

        // The paid upgrade supersedes the scheduled downgrade — leaving it queued would let the
        // renewal job revert a change the gym just paid a proration invoice for.
        Assert.True(result.IsSuccess);
        Assert.Equal(elite.Id, result.Value.PlanId);
        Assert.Null(result.Value.PendingChange);
        GymSubscription subscription = Assert.Single(context.Subscriptions.Subscriptions);
        Assert.Null(subscription.PendingChange);
        Assert.Equal(2, context.Invoices.Invoices.Count); // assignment + upgrade proration
    }

    [Fact]
    public async Task ChangingBillingCycle_IsScheduledRatherThanProrated()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan monthly = context.GivenPlan("MONTHLY", amount: 50m);
        SubscriptionPlan yearly = context.GivenPlan("YEARLY", amount: 999m);
        yearly.SetPrice(BillingCycle.Yearly, Money.Of(480m, Currency.Euro));
        await context.AssignAsync(gym.Id, monthly.Id);
        context.Clock.Advance(TimeSpan.FromDays(10));

        Result<SubscriptionDetail> result = await context.AssignAsync(gym.Id, yearly.Id, "Yearly");

        // Cross-cycle proration is ill-defined — allocating an annual price over a monthly period
        // charges a year's money for weeks of service — so the cycle changes at the boundary.
        Assert.True(result.IsSuccess);
        Assert.Equal(monthly.Id, result.Value.PlanId);
        PendingPlanChangeDetail pending = Assert.IsType<PendingPlanChangeDetail>(result.Value.PendingChange);
        Assert.Equal(yearly.Id, pending.PlanId);
        Assert.Equal("Yearly", pending.BillingCycle);
        Assert.Single(context.Invoices.Invoices); // only the original assignment invoice
    }

    [Fact]
    public async Task ChangingPlanAfterThePeriodEnded_AppliesImmediatelyAndBillsNothing()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan pro = context.GivenPlan("PRO", amount: 20m);
        SubscriptionPlan basic = context.GivenPlan("BASIC", amount: 10m);
        await context.AssignAsync(gym.Id, pro.Id);
        context.Clock.Advance(TimeSpan.FromDays(45)); // past the monthly period end

        Result<SubscriptionDetail> result = await context.AssignAsync(gym.Id, basic.Id);

        // The paid period is over: there is no remainder to price, and scheduling would park the
        // change on a date in the past that no job applies. The API saying success must mean it.
        Assert.True(result.IsSuccess);
        Assert.Equal(basic.Id, result.Value.PlanId);
        Assert.Null(result.Value.PendingChange);
        Assert.Single(context.Invoices.Invoices);
    }

    [Fact]
    public async Task ChangingPlanWhilePastDue_IsScheduledRatherThanProrated()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan pro = context.GivenPlan("PRO", amount: 20m);
        SubscriptionPlan elite = context.GivenPlan("ELITE", amount: 40m);
        await context.AssignAsync(gym.Id, pro.Id);
        await context.Dispatcher.SendAsync(
            new SetSubscriptionPaymentStateCommand(gym.Id, PastDue: true, null), CancellationToken.None);
        context.Clock.Advance(TimeSpan.FromDays(10));

        Result<SubscriptionDetail> result = await context.AssignAsync(gym.Id, elite.Id);

        // The current period is not settled: crediting unpaid time would be wrong, and stacking a
        // proration charge on an unpaid invoice would be worse. The change waits at the boundary.
        Assert.True(result.IsSuccess);
        Assert.Equal(pro.Id, result.Value.PlanId);
        Assert.NotNull(result.Value.PendingChange);
        Assert.Single(context.Invoices.Invoices);
    }

    [Fact]
    public async Task AnEqualPriceChange_ChangesThePlanWithoutBilling()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan monthlyA = context.GivenPlan("ALPHA", amount: 15m);
        SubscriptionPlan monthlyB = context.GivenPlan("BETA", amount: 15m);
        await context.AssignAsync(gym.Id, monthlyA.Id);
        context.Clock.Advance(TimeSpan.FromDays(10));

        Result<SubscriptionDetail> result = await context.AssignAsync(gym.Id, monthlyB.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(monthlyB.Id, result.Value.PlanId);
        GymSubscription subscription = Assert.Single(context.Subscriptions.Subscriptions);
        Assert.Null(subscription.PendingChange);
        Assert.Single(context.Invoices.Invoices); // nothing to bill on a net of zero
    }

    [Fact]
    public async Task EveryCommandCommitsExactlyOnce()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan basic = context.GivenPlan("BASIC", amount: 10m);
        SubscriptionPlan pro = context.GivenPlan("PRO", amount: 20m);

        await context.AssignAsync(gym.Id, basic.Id);
        Assert.Equal(1, context.UnitOfWork.SaveCount);

        context.Clock.Advance(TimeSpan.FromDays(10));
        await context.AssignAsync(gym.Id, pro.Id);
        Assert.Equal(2, context.UnitOfWork.SaveCount);
    }
}
