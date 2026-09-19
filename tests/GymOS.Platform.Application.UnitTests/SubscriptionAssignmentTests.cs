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
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel.Currencies;
using GymOS.SharedKernel.Results;
using GymOS.TestingSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GymOS.Platform.Application.UnitTests;

/// <summary>
/// Assigning and changing subscriptions. The interesting cases are the refusals — what stops a gym being
/// billed terms nobody agreed, and what stops an upgrade being recorded as churn.
/// </summary>
public sealed class SubscriptionAssignmentTests
{
    private const string Manage = PlatformPermissions.SubscriptionsManage;
    private const string Read = PlatformPermissions.BillingRead;

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

            // Assignment and conversion issue invoices since M3.3, so the handlers under test
            // resolve the issuer's seams even where a test never looks at an invoice.
            services.AddSingleton<IInvoiceRepository>(new FakeInvoiceRepository());
            services.AddSingleton<IInvoiceNumberGenerator>(new FakeInvoiceNumberGenerator());

            // M4.1: every subscription-changing handler withdraws exceptions its change made
            // redundant. These tests pin subscription behaviour, not feature resolution, so a
            // no-op fake is enough to satisfy the constructor.
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

        public FakePlanQueries PlanQueries { get; }

        public FakeSubscriptionQueries SubscriptionQueries { get; }

        public FakeUnitOfWork UnitOfWork { get; } = new();

        public FakeFeatureExceptionMaintainer FeatureExceptions { get; } = new();

        public FakeCurrentUser CurrentUser { get; } = new();

        public FakePermissionEvaluator Permissions { get; } = new();

        public IDispatcher Dispatcher => _provider.GetRequiredService<IDispatcher>();

        public Gym GivenGym(string slug = "acme-gym", bool deleted = false)
        {
            Gym gym = Gym.Provision(
                slug,
                "Acme Gym",
                new GymOwnerContact("Ada", "ada@acme.test", null),
                new GymLocale("CY", "Asia/Nicosia", "EUR"));

            gym.Activate();

            if (deleted)
            {
                gym.Delete(Clock.UtcNow);
            }

            Gyms.Seed(gym);
            return gym;
        }

        public SubscriptionPlan GivenPlan(
            string code = "PRO",
            int trialDays = 0,
            decimal amount = 49.99m,
            bool active = true)
        {
            SubscriptionPlan plan = SubscriptionPlan.Create(
                code, code, "", trialDays, PlanLimits.Unlimited);

            plan.SetPrice(BillingCycle.Monthly, Money.Of(amount, Currency.Euro));

            if (!active)
            {
                plan.Deactivate();
            }

            Plans.Seed(plan);
            return plan;
        }
    }

    [Fact]
    public async Task AssigningAPlan_StartsASubscriptionAtTheAgreedTerms()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan plan = context.GivenPlan(amount: 49.99m);

        Result<SubscriptionDetail> result = await context.Dispatcher.SendAsync(
            new AssignSubscriptionCommand(gym.Id, plan.Id, "Monthly", "EUR", null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Active", result.Value.Status);
        Assert.Equal(49.99m, result.Value.Amount);
        Assert.Equal("EUR", result.Value.Currency);
        Assert.Single(context.Subscriptions.Subscriptions);
    }

    [Fact]
    public async Task AssigningAPlan_DecidesRedundancyAgainstALockedPlanInsideOneTransaction()
    {
        // What counts as a "redundant" override is decided against this plan's features, and the
        // withdraw runs in the same use case — so the plan is read under UPDLOCK inside a transaction
        // that spans both (the SetGymFeatures precedent). An unlocked read let a concurrent plan feature
        // edit change the answer between the read and the withdraw (M5 whole-branch review).
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan plan = context.GivenPlan(amount: 49.99m);

        Result<SubscriptionDetail> result = await context.Dispatcher.SendAsync(
            new AssignSubscriptionCommand(gym.Id, plan.Id, "Monthly", "EUR", null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, context.UnitOfWork.TransactionCount);
        Assert.Equal(1, context.UnitOfWork.CommitCount);
        Assert.Equal(plan.Id, Assert.Single(context.Plans.LockedReads));
        Assert.Equal(plan.Id, Assert.Single(context.FeatureExceptions.CalledWithLivePlanIds));
    }

    [Fact]
    public async Task AssigningAPlanWithATrial_StartsTrialing()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan plan = context.GivenPlan(trialDays: 14);

        Result<SubscriptionDetail> result = await context.Dispatcher.SendAsync(
            new AssignSubscriptionCommand(gym.Id, plan.Id, "Monthly", "EUR", null),
            CancellationToken.None);

        Assert.Equal("Trialing", result.Value.Status);
        Assert.NotNull(result.Value.TrialEndsAtUtc);
    }

    [Fact]
    public async Task TheAgreedPriceIsCopied_NotFollowed()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan plan = context.GivenPlan(amount: 49.99m);

        await context.Dispatcher.SendAsync(
            new AssignSubscriptionCommand(gym.Id, plan.Id, "Monthly", "EUR", null),
            CancellationToken.None);

        // The plan is repriced. §2.3: renewals honour the price at assignment.
        plan.SetPrice(BillingCycle.Monthly, Money.Of(79.99m, Currency.Euro));

        GymSubscription subscription = Assert.Single(context.Subscriptions.Subscriptions);
        Assert.Equal(49.99m, subscription.Price.Amount);
    }

    [Fact]
    public async Task ChangingPlan_UpdatesTheOneSubscription()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan starter = context.GivenPlan("STARTER", amount: 19.99m);
        SubscriptionPlan pro = context.GivenPlan("PRO_X", amount: 99.99m);

        await context.Dispatcher.SendAsync(
            new AssignSubscriptionCommand(gym.Id, starter.Id, "Monthly", "EUR", null),
            CancellationToken.None);

        Result<SubscriptionDetail> result = await context.Dispatcher.SendAsync(
            new AssignSubscriptionCommand(gym.Id, pro.Id, "Monthly", "EUR", null),
            CancellationToken.None);

        // One row: recording an upgrade as a cancellation would inflate the churn figure.
        Assert.Single(context.Subscriptions.Subscriptions);
        Assert.Equal(pro.Id, result.Value.PlanId);
        Assert.Equal(99.99m, result.Value.Amount);
    }

    [Theory]
    [InlineData("Yearly", "EUR")]
    [InlineData("Monthly", "GBP")]
    public async Task TermsThePlanDoesNotOffer_AreRefusedRatherThanApproximated(
        string cycle,
        string currency)
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan plan = context.GivenPlan();

        Result<SubscriptionDetail> result = await context.Dispatcher.SendAsync(
            new AssignSubscriptionCommand(gym.Id, plan.Id, cycle, currency, null),
            CancellationToken.None);

        // Picking "the nearest price" would bill an amount nobody agreed to.
        Assert.Equal("subscriptions.price_not_offered", result.Error!.Code);
        Assert.Empty(context.Subscriptions.Subscriptions);
    }

    [Fact]
    public async Task AWithdrawnPlan_CannotBeNewlyAssigned()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan plan = context.GivenPlan(active: false);

        Result<SubscriptionDetail> result = await context.Dispatcher.SendAsync(
            new AssignSubscriptionCommand(gym.Id, plan.Id, "Monthly", "EUR", null),
            CancellationToken.None);

        Assert.Equal(ErrorKind.Conflict, result.Error!.Kind);
        Assert.Equal("subscriptions.plan_inactive", result.Error.Code);
    }

    [Fact]
    public async Task AGymAlreadyOnAWithdrawnPlan_CanChangeItsTerms()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan plan = context.GivenPlan();
        plan.SetPrice(BillingCycle.Yearly, Money.Of(499m, Currency.Euro));

        await context.Dispatcher.SendAsync(
            new AssignSubscriptionCommand(gym.Id, plan.Id, "Monthly", "EUR", null),
            CancellationToken.None);

        plan.Deactivate();

        // §2.3: inactive plans stay attached to existing gyms. Withdrawing from sale must not strand a
        // customer who cannot then be adjusted. The adjustment is a real one (monthly → yearly) since
        // M3.3: re-sending the terms a gym already has is the change_to_same_plan refusal instead.
        Result<SubscriptionDetail> result = await context.Dispatcher.SendAsync(
            new AssignSubscriptionCommand(gym.Id, plan.Id, "Yearly", "EUR", null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ADeletedGym_CannotBeSubscribed()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym(deleted: true);
        SubscriptionPlan plan = context.GivenPlan();

        Result<SubscriptionDetail> result = await context.Dispatcher.SendAsync(
            new AssignSubscriptionCommand(gym.Id, plan.Id, "Monthly", "EUR", null),
            CancellationToken.None);

        Assert.Equal("subscriptions.gym_deleted", result.Error!.Code);
    }

    [Fact]
    public async Task AnUnknownGymOrPlan_IsNotFound()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();

        Result<SubscriptionDetail> noGym = await context.Dispatcher.SendAsync(
            new AssignSubscriptionCommand(Guid.NewGuid(), Guid.NewGuid(), "Monthly", "EUR", null),
            CancellationToken.None);
        Result<SubscriptionDetail> noPlan = await context.Dispatcher.SendAsync(
            new AssignSubscriptionCommand(gym.Id, Guid.NewGuid(), "Monthly", "EUR", null),
            CancellationToken.None);

        Assert.Equal("gyms.not_found", noGym.Error!.Code);
        Assert.Equal("plans.not_found", noPlan.Error!.Code);
    }

    [Fact]
    public async Task SchedulingCancellation_LeavesTheSubscriptionLive()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan plan = context.GivenPlan();
        await context.Dispatcher.SendAsync(
            new AssignSubscriptionCommand(gym.Id, plan.Id, "Monthly", "EUR", null),
            CancellationToken.None);

        Result<SubscriptionDetail> result = await context.Dispatcher.SendAsync(
            new CancelSubscriptionCommand(gym.Id, "Too expensive", Immediately: false, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Active", result.Value.Status);
        Assert.True(result.Value.CancelAtPeriodEnd);
        Assert.Null(result.Value.CanceledAtUtc);
    }

    [Fact]
    public async Task SchedulingCancellationTwice_IsAConflict()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan plan = context.GivenPlan();
        await context.Dispatcher.SendAsync(
            new AssignSubscriptionCommand(gym.Id, plan.Id, "Monthly", "EUR", null),
            CancellationToken.None);
        await context.Dispatcher.SendAsync(
            new CancelSubscriptionCommand(gym.Id, null, Immediately: false, null),
            CancellationToken.None);

        Result<SubscriptionDetail> result = await context.Dispatcher.SendAsync(
            new CancelSubscriptionCommand(gym.Id, null, Immediately: false, null),
            CancellationToken.None);

        Assert.Equal("subscriptions.cancellation_already_scheduled", result.Error!.Code);
    }

    [Fact]
    public async Task CancellingImmediately_EndsIt()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan plan = context.GivenPlan();
        await context.Dispatcher.SendAsync(
            new AssignSubscriptionCommand(gym.Id, plan.Id, "Monthly", "EUR", null),
            CancellationToken.None);

        Result<SubscriptionDetail> result = await context.Dispatcher.SendAsync(
            new CancelSubscriptionCommand(gym.Id, "Closed", Immediately: true, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Canceled", result.Value.Status);
        Assert.NotNull(result.Value.CanceledAtUtc);

        // No live subscription remains, so a following assignment starts a new one.
        Assert.Null(await context.SubscriptionQueries.GetLiveForGymAsync(gym.Id, CancellationToken.None));
    }

    [Fact]
    public async Task OperatingOnAGymWithNoSubscription_IsNotFound()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();

        Result<SubscriptionDetail> result = await context.Dispatcher.SendAsync(
            new CancelSubscriptionCommand(gym.Id, null, Immediately: false, null),
            CancellationToken.None);

        Assert.Equal(ErrorKind.NotFound, result.Error!.Kind);
        Assert.Equal("subscriptions.not_found", result.Error.Code);
    }

    [Fact]
    public async Task ConvertingATrial_StartsBilling()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan plan = context.GivenPlan(trialDays: 14);
        await context.Dispatcher.SendAsync(
            new AssignSubscriptionCommand(gym.Id, plan.Id, "Monthly", "EUR", null),
            CancellationToken.None);

        Result<SubscriptionDetail> converted = await context.Dispatcher.SendAsync(
            new ConvertTrialCommand(gym.Id, null),
            CancellationToken.None);
        Result<SubscriptionDetail> again = await context.Dispatcher.SendAsync(
            new ConvertTrialCommand(gym.Id, null),
            CancellationToken.None);

        Assert.Equal("Active", converted.Value.Status);
        Assert.Equal("subscriptions.not_trialing", again.Error!.Code);
    }

    [Fact]
    public async Task PaymentStateMovesBetweenActiveAndPastDue()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan plan = context.GivenPlan();
        await context.Dispatcher.SendAsync(
            new AssignSubscriptionCommand(gym.Id, plan.Id, "Monthly", "EUR", null),
            CancellationToken.None);

        Result<SubscriptionDetail> late = await context.Dispatcher.SendAsync(
            new SetSubscriptionPaymentStateCommand(gym.Id, PastDue: true, null),
            CancellationToken.None);
        Result<SubscriptionDetail> lateAgain = await context.Dispatcher.SendAsync(
            new SetSubscriptionPaymentStateCommand(gym.Id, PastDue: true, null),
            CancellationToken.None);
        Result<SubscriptionDetail> paid = await context.Dispatcher.SendAsync(
            new SetSubscriptionPaymentStateCommand(gym.Id, PastDue: false, null),
            CancellationToken.None);

        Assert.Equal("PastDue", late.Value.Status);
        Assert.Equal("subscriptions.already_past_due", lateAgain.Error!.Code);
        Assert.Equal("Active", paid.Value.Status);
    }

    [Fact]
    public async Task AssigningWithoutTheManagePermission_IsRefused()
    {
        var context = new Context(Read);
        Gym gym = context.GivenGym();
        SubscriptionPlan plan = context.GivenPlan();

        // Reading the catalogue does not imply deciding what one customer pays.
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            context.Dispatcher.SendAsync(
                new AssignSubscriptionCommand(gym.Id, plan.Id, "Monthly", "EUR", null),
                CancellationToken.None));

        Assert.Empty(context.Subscriptions.Subscriptions);
    }

    [Fact]
    public async Task ReadingWithoutBillingRead_IsRefused()
    {
        var context = new Context();

        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            context.Dispatcher.QueryAsync(
                new GetGymSubscriptionQuery(Guid.NewGuid()), CancellationToken.None));
    }

    [Fact]
    public async Task AMalformedVersionTokenIsRejected()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan plan = context.GivenPlan();
        await context.Dispatcher.SendAsync(
            new AssignSubscriptionCommand(gym.Id, plan.Id, "Monthly", "EUR", null),
            CancellationToken.None);

        Result<SubscriptionDetail> result = await context.Dispatcher.SendAsync(
            new CancelSubscriptionCommand(gym.Id, null, Immediately: false, "W/\"7\""),
            CancellationToken.None);

        Assert.Equal("concurrency.malformed_version", result.Error!.Code);
    }
}
