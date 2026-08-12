using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.DependencyInjection;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Models;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.DependencyInjection;
using GymOS.Platform.Application.Subscriptions;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Application.UnitTests.Fakes;
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
/// The plan catalogue's use cases. The interesting cases are the refusals and the price semantics â€”
/// what stops a price edit from silently repricing a live subscription, and what stops a plan from being
/// destroyed when billing history points at it.
/// </summary>
public sealed class PlanCatalogueTests
{
    private const string Read = PlatformPermissions.BillingRead;
    private const string Manage = PlatformPermissions.BillingManage;

    /// <summary>
    /// The version <c>FakePlanQueries</c> reports, i.e. what a caller would have just read. The
    /// price endpoints require <c>If-Match</c> — they overwrite a price the caller read, and
    /// <c>SetPrice</c> is an upsert with no state machine to refuse a stale interleaving.
    /// </summary>
    private const string CurrentVersion = "1";

    private sealed class Context
    {
        private readonly ServiceProvider _provider;

        public Context(params string[] permissions)
        {
            PlanQueries = new FakePlanQueries(Plans);

            var services = new ServiceCollection();
            services.AddCqrsDispatcher();
            services.AddAuthorizationBehavior();
            services.AddPlatformApplication();

            services.AddSingleton<IClock>(Clock);
            services.AddSingleton<IPlanRepository>(Plans);
            services.AddSingleton<IPlanQueries>(PlanQueries);
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

        public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));

        public FakePlanRepository Plans { get; } = new();

        public FakePlanQueries PlanQueries { get; }

        public FakeUnitOfWork UnitOfWork { get; } = new();

        public FakeCurrentUser CurrentUser { get; } = new();

        public FakePermissionEvaluator Permissions { get; } = new();

        public IDispatcher Dispatcher => _provider.GetRequiredService<IDispatcher>();

        public SubscriptionPlan GivenPlan(string code = "PRO", bool active = true)
        {
            SubscriptionPlan plan = SubscriptionPlan.Create(
                code, "Pro", "For established gyms", 14, new PlanLimits(500, 10, 20, 10_240));

            if (!active)
            {
                plan.Deactivate();
            }

            Plans.Seed(plan);
            return plan;
        }
    }

    private static CreatePlanCommand NewPlan(string code = "STARTER") =>
        new(code, "Starter", "For new gyms", 14, 100, 3, 5, 1_024, 1);

    // â”€â”€ Creation â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task CreatedPlan_StartsActivePublicAndPriceless()
    {
        var context = new Context(Manage);

        Result<PlanDetail> result = await context.Dispatcher.SendAsync(
            NewPlan(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsActive);
        Assert.True(result.Value.IsPublic);
        Assert.Empty(result.Value.Prices);
        Assert.Equal("STARTER", Assert.Single(context.Plans.Plans).Code);
    }

    [Fact]
    public async Task CreatingADuplicateCode_IsAConflict()
    {
        var context = new Context(Manage);
        context.GivenPlan("PRO");

        Result<PlanDetail> result = await context.Dispatcher.SendAsync(
            NewPlan("pro"),
            CancellationToken.None);

        // Matched case-insensitively, because the code is upper-cased before the check.
        Assert.Equal(ErrorKind.Conflict, result.Error!.Kind);
        Assert.Equal("plans.code_taken", result.Error.Code);
        Assert.Single(context.Plans.Plans);
    }

    [Fact]
    public async Task CreatingWithAnInvalidCode_IsAValidationFailure_NotAnException()
    {
        var context = new Context(Manage);

        Result<PlanDetail> result = await context.Dispatcher.SendAsync(
            NewPlan("not a code"),
            CancellationToken.None);

        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Equal("plans.code_invalid", result.Error.Code);
        Assert.Empty(context.Plans.Plans);
    }

    [Fact]
    public async Task CreatingWithoutTheManagePermission_IsRefused()
    {
        var context = new Context(Read);

        // Reading the catalogue does not imply deciding what the platform charges.
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            context.Dispatcher.SendAsync(NewPlan(), CancellationToken.None));

        Assert.Empty(context.Plans.Plans);
    }

    // â”€â”€ Prices â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task SettingAPrice_AddsItAndAdvancesThePlansVersion()
    {
        var context = new Context(Manage);
        SubscriptionPlan plan = context.GivenPlan();

        Result<PlanDetail> result = await context.Dispatcher.SendAsync(
            new SetPlanPriceCommand(plan.Id, "Monthly", "EUR", 49.99m, CurrentVersion),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        PlanPriceDetail price = Assert.Single(result.Value.Prices);
        Assert.Equal("Monthly", price.BillingCycle);
        Assert.Equal(1, price.BillingCycleMonths);
        Assert.Equal("EUR", price.Currency);
        Assert.Equal(49.99m, price.Amount);

        // Without this the plan's row version never moves and two concurrent price edits both succeed.
        Assert.Contains(plan.Id, context.Plans.AggregateChanges);
    }

    [Fact]
    public async Task SettingAPriceTwice_ChangesItRatherThanAddingASecond()
    {
        var context = new Context(Manage);
        SubscriptionPlan plan = context.GivenPlan();

        await context.Dispatcher.SendAsync(
            new SetPlanPriceCommand(plan.Id, "Monthly", "EUR", 49.99m, CurrentVersion),
            CancellationToken.None);

        Result<PlanDetail> result = await context.Dispatcher.SendAsync(
            new SetPlanPriceCommand(plan.Id, "monthly", "eur", 59.99m, CurrentVersion),
            CancellationToken.None);

        // Cycle and currency are matched case-insensitively; the row is the same one.
        Assert.Equal(59.99m, Assert.Single(result.Value.Prices).Amount);
    }

    [Fact]
    public async Task SettingAPrice_RoundsToTheCurrencysMinorUnits()
    {
        var context = new Context(Manage);
        SubscriptionPlan plan = context.GivenPlan();

        Result<PlanDetail> eur = await context.Dispatcher.SendAsync(
            new SetPlanPriceCommand(plan.Id, "Monthly", "EUR", 49.999m, CurrentVersion),
            CancellationToken.None);
        Assert.Equal(50.00m, Assert.Single(eur.Value.Prices).Amount);

        // JPY has no minor unit at all, which a hard-coded two-decimal rounding would get wrong.
        Result<PlanDetail> jpy = await context.Dispatcher.SendAsync(
            new SetPlanPriceCommand(plan.Id, "Monthly", "JPY", 4999.6m, CurrentVersion),
            CancellationToken.None);
        Assert.Equal(5000m, jpy.Value.Prices.Single(price => price.Currency == "JPY").Amount);
    }

    [Theory]
    [InlineData("Fortnightly")]
    [InlineData("")]
    [InlineData("13")]
    public async Task SettingAPriceWithAnUnknownCycle_IsRefusedRatherThanGuessed(string cycle)
    {
        var context = new Context(Manage);
        SubscriptionPlan plan = context.GivenPlan();

        Result<PlanDetail> result = await context.Dispatcher.SendAsync(
            new SetPlanPriceCommand(plan.Id, cycle, "EUR", 49.99m, CurrentVersion),
            CancellationToken.None);

        // Billing a gym annually for a monthly plan is not a mistake worth being lenient about.
        Assert.Equal("plans.billing_cycle_invalid", result.Error!.Code);
        Assert.Empty(plan.Prices);
    }

    [Fact]
    public async Task SettingAPriceWithAnUnknownCurrency_IsRefused()
    {
        var context = new Context(Manage);
        SubscriptionPlan plan = context.GivenPlan();

        Result<PlanDetail> result = await context.Dispatcher.SendAsync(
            new SetPlanPriceCommand(plan.Id, "Monthly", "EUROS", 49.99m, CurrentVersion),
            CancellationToken.None);

        Assert.Equal("plans.currency_invalid", result.Error!.Code);
    }

    [Fact]
    public async Task SettingAZeroPrice_IsRefused()
    {
        var context = new Context(Manage);
        SubscriptionPlan plan = context.GivenPlan();

        Result<PlanDetail> result = await context.Dispatcher.SendAsync(
            new SetPlanPriceCommand(plan.Id, "Monthly", "EUR", 0m, CurrentVersion),
            CancellationToken.None);

        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Equal("plans.price_invalid", result.Error.Code);
    }

    [Fact]
    public async Task RemovingAPrice_TakesItOutOfTheCatalogue()
    {
        var context = new Context(Manage);
        SubscriptionPlan plan = context.GivenPlan();
        PlanPrice price = plan.SetPrice(BillingCycle.Monthly, Money.Of(49.99m, Currency.Euro));

        Result<PlanDetail> result = await context.Dispatcher.SendAsync(
            new RemovePlanPriceCommand(plan.Id, price.Id, CurrentVersion),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Prices);
    }

    [Fact]
    public async Task RemovingAPriceThatIsNotThePlans_IsNotFound()
    {
        var context = new Context(Manage);
        SubscriptionPlan plan = context.GivenPlan();

        Result<PlanDetail> result = await context.Dispatcher.SendAsync(
            new RemovePlanPriceCommand(plan.Id, Guid.NewGuid(), CurrentVersion),
            CancellationToken.None);

        Assert.Equal(ErrorKind.NotFound, result.Error!.Kind);
        Assert.Equal("plans.price_not_found", result.Error.Code);
    }

    // â”€â”€ Withdrawal â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task DeactivatingAPlan_KeepsItsPricesAndItsHistory()
    {
        var context = new Context(Manage);
        SubscriptionPlan plan = context.GivenPlan();
        plan.SetPrice(BillingCycle.Monthly, Money.Of(49.99m, Currency.Euro));

        Result<PlanDetail> result = await context.Dispatcher.SendAsync(
            new DeactivatePlanCommand(plan.Id, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsActive);

        // Not a delete: gyms on this plan keep billing at the price they were assigned.
        Assert.Single(result.Value.Prices);
        Assert.Single(context.Plans.Plans);
    }

    [Fact]
    public async Task DeactivatingTwice_IsAConflict()
    {
        var context = new Context(Manage);
        SubscriptionPlan plan = context.GivenPlan(active: false);

        Result<PlanDetail> result = await context.Dispatcher.SendAsync(
            new DeactivatePlanCommand(plan.Id, null),
            CancellationToken.None);

        Assert.Equal("plans.already_inactive", result.Error!.Code);
    }

    [Fact]
    public async Task ActivatingAnActivePlan_IsAConflict()
    {
        var context = new Context(Manage);
        SubscriptionPlan plan = context.GivenPlan();

        Result<PlanDetail> result = await context.Dispatcher.SendAsync(
            new ActivatePlanCommand(plan.Id, null),
            CancellationToken.None);

        Assert.Equal("plans.already_active", result.Error!.Code);
    }

    // â”€â”€ Concurrency â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task UpdatingAPlan_RequiresAnExpectedVersion()
    {
        var context = new Context(Manage);
        SubscriptionPlan plan = context.GivenPlan();

        Result<PlanDetail> result = await context.Dispatcher.SendAsync(
            new UpdatePlanCommand(plan.Id, "Renamed", null, 14, null, null, null, null, 0, true, null),
            CancellationToken.None);

        Assert.Equal("concurrency.version_required", result.Error!.Code);
        Assert.Equal("Pro", plan.Name);
    }

    [Fact]
    public async Task UpdatingAPlan_RejectsAVersionTokenThisApiNeverIssued()
    {
        var context = new Context(Manage);
        SubscriptionPlan plan = context.GivenPlan();

        Result<PlanDetail> result = await context.Dispatcher.SendAsync(
            new UpdatePlanCommand(plan.Id, "Renamed", null, 14, null, null, null, null, 0, true, "W/\"7\""),
            CancellationToken.None);

        Assert.Equal("concurrency.malformed_version", result.Error!.Code);
    }

    [Fact]
    public async Task UpdatingAPlan_AppliesTheChangeAndKeepsTheCode()
    {
        var context = new Context(Manage);
        SubscriptionPlan plan = context.GivenPlan();

        Result<PlanDetail> result = await context.Dispatcher.SendAsync(
            new UpdatePlanCommand(plan.Id, "Pro Plus", "More", 30, null, null, null, null, 3, false, "1"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Pro Plus", result.Value.Name);
        Assert.Equal("PRO", result.Value.Code);
        Assert.Null(result.Value.MaxMembers);
        Assert.Equal(1u, context.Plans.ExpectedVersions[plan.Id]);
        Assert.Equal(1, context.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task UpdatingAnUnknownPlan_IsNotFound()
    {
        var context = new Context(Manage);

        Result<PlanDetail> result = await context.Dispatcher.SendAsync(
            new UpdatePlanCommand(Guid.NewGuid(), "X", null, 0, null, null, null, null, 0, true, "1"),
            CancellationToken.None);

        Assert.Equal(ErrorKind.NotFound, result.Error!.Kind);
        Assert.Equal("plans.not_found", result.Error.Code);
    }

    // â”€â”€ Reading â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task ListingExcludesWithdrawnPlansByDefault()
    {
        var context = new Context(Read);
        context.GivenPlan("LIVE");
        context.GivenPlan("GONE", active: false);

        Result<PagedResult<PlanListItem>> result = await context.Dispatcher.QueryAsync(
            new ListPlansQuery(new PageRequest(null, null, null, null, null), false, null),
            CancellationToken.None);

        Assert.Equal("LIVE", Assert.Single(result.Value.Items).Code);
    }

    [Fact]
    public async Task ListingCanIncludeWithdrawnPlansDeliberately()
    {
        var context = new Context(Read);
        context.GivenPlan("LIVE");
        context.GivenPlan("GONE", active: false);

        Result<PagedResult<PlanListItem>> result = await context.Dispatcher.QueryAsync(
            new ListPlansQuery(new PageRequest(null, null, null, null, null), true, null),
            CancellationToken.None);

        Assert.Equal(2, result.Value.Items.Count);
    }

    [Fact]
    public async Task GettingAWithdrawnPlan_StillReturnsIt()
    {
        var context = new Context(Read);
        SubscriptionPlan plan = context.GivenPlan(active: false);

        Result<PlanDetail> result = await context.Dispatcher.QueryAsync(
            new GetPlanQuery(plan.Id),
            CancellationToken.None);

        // An operator asking why a gym is on a plan missing from the catalogue needs to see it.
        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsActive);
    }

    [Fact]
    public async Task ReadingWithoutThePermission_IsRefused()
    {
        var context = new Context();

        // The catalogue is commercial information.
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            context.Dispatcher.QueryAsync(new GetPlanQuery(Guid.NewGuid()), CancellationToken.None));
    }
}
