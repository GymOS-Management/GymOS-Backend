using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.DependencyInjection;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.DependencyInjection;
using GymOS.Platform.Application.Features;
using GymOS.Platform.Application.Features.Abstractions;
using GymOS.Platform.Application.Gyms.Abstractions;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Application.UnitTests.Fakes;
using GymOS.Platform.Domain.Features;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel.Results;
using GymOS.TestingSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GymOS.Platform.Application.UnitTests;

/// <summary>
/// What a plan includes — and the tri-state that makes it interesting: include, exclude, or say
/// nothing, where the third is not the second.
/// </summary>
public sealed class PlanFeatureCommandTests
{
    private const string BillingManage = PlatformPermissions.BillingManage;
    private const string BillingRead = PlatformPermissions.BillingRead;
    private const string CurrentVersion = "1";

    private sealed class Context
    {
        private readonly ServiceProvider _provider;

        public Context(params string[] permissions)
        {
            PlanQueries = new FakePlanQueries(Plans) { Features = FeatureCatalogue };
            FeatureQueries = new FakeFeatureQueries(FeatureCatalogue, Plans, Subscriptions);
            FeatureCatalogue.SeedDeclaredCatalogue();

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
            services.AddSingleton<ISubscriptionQueries>(
                new FakeSubscriptionQueries(Subscriptions, Plans));
            services.AddSingleton<IFeatureRepository>(FeatureCatalogue);
            services.AddSingleton<IFeatureQueries>(FeatureQueries);
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

        public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));

        public FakeGymRepository Gyms { get; } = new();

        public FakePlanRepository Plans { get; } = new();

        public FakeSubscriptionRepository Subscriptions { get; } = new();

        public FakeFeatureRepository FeatureCatalogue { get; } = new();

        public FakePlanQueries PlanQueries { get; }

        public FakeFeatureQueries FeatureQueries { get; }

        public FakeUnitOfWork UnitOfWork { get; } = new();

        public FakeCurrentUser CurrentUser { get; } = new();

        public FakePermissionEvaluator Permissions { get; } = new();

        public IDispatcher Dispatcher => _provider.GetRequiredService<IDispatcher>();

        public SubscriptionPlan GivenPlan(string code = "PRO")
        {
            SubscriptionPlan plan = SubscriptionPlan.Create(code, code, "", 0, PlanLimits.Unlimited);
            Plans.Seed(plan);
            return plan;
        }

        public Guid FeatureId(string key) =>
            FeatureCatalogue.Features.Single(feature => feature.Key == key).Id;

        public Task<Result<PlanDetail>> SetAsync(Guid planId, params PlanFeatureRequest[] features) =>
            Dispatcher.SendAsync(
                new SetPlanFeaturesCommand(planId, features, CurrentVersion), CancellationToken.None);
    }

    [Fact]
    public async Task IncludingAFeature_RecordsThePlansOpinion()
    {
        var context = new Context(BillingManage);
        SubscriptionPlan plan = context.GivenPlan();

        Result<PlanDetail> result = await context.SetAsync(
            plan.Id, new PlanFeatureRequest(PlatformFeatures.Crm, true));

        Assert.True(result.IsSuccess);
        PlanFeature included = Assert.Single(plan.Features);
        Assert.Equal(context.FeatureId(PlatformFeatures.Crm), included.FeatureId);
        Assert.True(included.IsEnabled);

        PlanFeatureDetail reported = Assert.Single(result.Value.Features);
        Assert.Equal(PlatformFeatures.Crm, reported.Key);
        Assert.True(reported.IsEnabled);
    }

    [Fact]
    public async Task ExcludingAFeature_IsNotTheSameAsSayingNothing()
    {
        var context = new Context(BillingManage);
        SubscriptionPlan plan = context.GivenPlan();

        // Exclude: the plan HAS an opinion, and it is "no".
        await context.SetAsync(plan.Id, new PlanFeatureRequest(PlatformFeatures.Crm, false));
        PlanFeature excluded = Assert.Single(plan.Features);
        Assert.False(excluded.IsEnabled);

        // Clear: the plan has NO opinion, so gyms fall through to the platform default. The two
        // look identical to a gym today and differ the moment that default changes — which is the
        // whole reason the plan layer is nullable.
        await context.SetAsync(plan.Id, new PlanFeatureRequest(PlatformFeatures.Crm, null));
        Assert.Empty(plan.Features);
    }

    [Fact]
    public async Task OnlyTheKeysSent_AreTouched()
    {
        var context = new Context(BillingManage);
        SubscriptionPlan plan = context.GivenPlan();

        await context.SetAsync(
            plan.Id,
            new PlanFeatureRequest(PlatformFeatures.Crm, true),
            new PlanFeatureRequest(PlatformFeatures.Nutrition, true));

        // A client that knows about two features must not erase a third by omitting it.
        Result<PlanDetail> second = await context.SetAsync(
            plan.Id, new PlanFeatureRequest(PlatformFeatures.Crm, false));

        Assert.True(second.IsSuccess);
        Assert.Equal(2, plan.Features.Count);
        Assert.False(plan.Features.Single(f => f.FeatureId == context.FeatureId(PlatformFeatures.Crm)).IsEnabled);
        Assert.True(plan.Features.Single(f => f.FeatureId == context.FeatureId(PlatformFeatures.Nutrition)).IsEnabled);
    }

    [Fact]
    public async Task AnUnknownKey_RejectsTheWholeRequest()
    {
        var context = new Context(BillingManage);
        SubscriptionPlan plan = context.GivenPlan();

        Result<PlanDetail> result = await context.SetAsync(
            plan.Id,
            new PlanFeatureRequest(PlatformFeatures.Crm, true),
            new PlanFeatureRequest("teleportation", true));

        // Nothing is applied — a half-applied feature set is worse than a refused one, because the
        // caller cannot tell which half landed.
        Assert.True(result.IsFailure);
        Assert.Equal("features.unknown_key", result.Error!.Code);
        Assert.Empty(plan.Features);
        Assert.Equal(0, context.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task TheKeyMatchIsCaseInsensitive()
    {
        var context = new Context(BillingManage);
        SubscriptionPlan plan = context.GivenPlan();

        Result<PlanDetail> result = await context.SetAsync(
            plan.Id, new PlanFeatureRequest("CRM", true));

        // Keys are lower-case by declaration; a caller shouting one is not a mistake worth refusing.
        Assert.True(result.IsSuccess);
        Assert.Single(plan.Features);
    }

    [Fact]
    public async Task TheVersionIsRequired()
    {
        var context = new Context(BillingManage);
        SubscriptionPlan plan = context.GivenPlan();

        Result<PlanDetail> result = await context.Dispatcher.SendAsync(
            new SetPlanFeaturesCommand(
                plan.Id, [new PlanFeatureRequest(PlatformFeatures.Crm, true)], null),
            CancellationToken.None);

        // This overwrites what the caller read, and there is no state machine to refuse a stale
        // interleaving — the M3.1 price lesson.
        Assert.True(result.IsFailure);
        Assert.Equal("concurrency.version_required", result.Error!.Code);
        Assert.Empty(plan.Features);
    }

    [Fact]
    public async Task ThePlansVersionAdvances_SoConcurrentFeatureEditsCollide()
    {
        var context = new Context(BillingManage);
        SubscriptionPlan plan = context.GivenPlan();

        await context.SetAsync(plan.Id, new PlanFeatureRequest(PlatformFeatures.Crm, true));

        // Feature rows live in a child table, so without MarkAggregateChanged the plan's own row is
        // never written and its version never moves, leaving concurrent edits unprotected.
        Assert.Contains(plan.Id, context.Plans.AggregateChanges);
    }

    [Fact]
    public async Task AnEmptyRequest_ChangesNothingAndDoesNotBumpTheVersion()
    {
        var context = new Context(BillingManage);
        SubscriptionPlan plan = context.GivenPlan();

        Result<PlanDetail> result = await context.SetAsync(plan.Id);

        Assert.True(result.IsSuccess);
        Assert.Empty(plan.Features);
        Assert.DoesNotContain(plan.Id, context.Plans.AggregateChanges);
    }

    [Fact]
    public async Task AnUnknownPlan_IsNotFound()
    {
        var context = new Context(BillingManage);

        Result<PlanDetail> result = await context.SetAsync(
            Guid.NewGuid(), new PlanFeatureRequest(PlatformFeatures.Crm, true));

        Assert.True(result.IsFailure);
        Assert.Equal("plans.not_found", result.Error!.Code);
    }

    [Fact]
    public async Task BillingReadAlone_CannotChangeWhatThePlanIncludes()
    {
        var context = new Context(BillingRead);
        SubscriptionPlan plan = context.GivenPlan();

        // What a plan includes is priced, so editing it is billing.manage — the same authority as
        // its prices, and deliberately not the features permission a support agent holds.
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            context.SetAsync(plan.Id, new PlanFeatureRequest(PlatformFeatures.Crm, true)));

        Assert.Empty(plan.Features);
    }
}
