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
/// One gym's exceptions to what its plan gives — and the rule that makes them self-cleaning: a row
/// exists only while it differs from the answer underneath it.
/// </summary>
public sealed class GymFeatureCommandTests
{
    private const string FeaturesManage = PlatformPermissions.FeaturesManage;
    private const string FeaturesRead = PlatformPermissions.FeaturesRead;

    /// <summary>
    /// <c>crm</c> is off by default and premium; <c>reports</c> is on by default and free. Both
    /// directions of "differs from the layer beneath" therefore have a real key to use, rather than a
    /// test-only feature that could not appear in production.
    /// </summary>
    private const string OffByDefault = PlatformFeatures.Crm;
    private const string OnByDefault = PlatformFeatures.Reports;

    private const string Reason = "Migrated from a competitor; contractually promised for 3 months.";

    private sealed class Context
    {
        private readonly ServiceProvider _provider;

        public Context(params string[] permissions)
        {
            FeatureQueries = new FakeFeatureQueries(FeatureCatalogue, Plans, Subscriptions);
            GymQueries = new FakeGymQueries(Gyms) { FeatureQueries = FeatureQueries };
            FeatureCatalogue.SeedDeclaredCatalogue();

            var services = new ServiceCollection();
            services.AddCqrsDispatcher();
            services.AddAuthorizationBehavior();
            services.AddPlatformApplication();

            services.AddSingleton<IClock>(Clock);
            services.AddSingleton<IGymRepository>(Gyms);
            services.AddSingleton<IGymQueries>(GymQueries);
            services.AddSingleton<IPlanRepository>(Plans);
            services.AddSingleton<IPlanQueries>(new FakePlanQueries(Plans) { Features = FeatureCatalogue });
            services.AddSingleton<ISubscriptionRepository>(Subscriptions);
            services.AddSingleton<ISubscriptionQueries>(new FakeSubscriptionQueries(Subscriptions, Plans));
            services.AddSingleton<IFeatureRepository>(FeatureCatalogue);
            services.AddSingleton<IFeatureQueries>(FeatureQueries);
            services.AddSingleton<IUnitOfWork>(UnitOfWork);
            services.AddSingleton<ICurrentUser>(CurrentUser);
            services.AddSingleton<IPermissionEvaluator>(Permissions);
            services.AddSingleton<IAuditTrail>(new FakeAuditTrail());
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

            _provider = services.BuildServiceProvider();

            CurrentUser.UserId = Operator;
            foreach (string permission in permissions)
            {
                Permissions.Granted.Add(permission);
            }
        }

        public static Guid Operator { get; } = Guid.Parse("99999999-9999-9999-9999-999999999999");

        public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));

        public FakeGymRepository Gyms { get; } = new();

        public FakePlanRepository Plans { get; } = new();

        public FakeSubscriptionRepository Subscriptions { get; } = new();

        public FakeFeatureRepository FeatureCatalogue { get; } = new();

        public FakeFeatureQueries FeatureQueries { get; }

        public FakeGymQueries GymQueries { get; }

        public FakeUnitOfWork UnitOfWork { get; } = new();

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
            Gyms.SeedBranding(GymBranding.CreateEmpty(gym.Id));
            return gym;
        }

        /// <summary>Puts the gym on a live plan, optionally with an opinion about one feature.</summary>
        public SubscriptionPlan GivenSubscribedGym(
            Gym gym,
            string code = "PRO",
            (string Key, bool IsEnabled)? planSays = null)
        {
            SubscriptionPlan plan = SubscriptionPlan.Create(code, code, "", 0, PlanLimits.Unlimited);
            plan.SetPrice(BillingCycle.Monthly, Money.Of(49.99m, Currency.Euro));

            if (planSays is { } opinion)
            {
                plan.SetFeature(FeatureId(opinion.Key), opinion.IsEnabled);
            }

            Plans.Seed(plan);
            Subscriptions.Seed(GymSubscription.Assign(
                gym.Id, plan, SubscriptionTerms.From(plan.Prices.Single()), Clock.UtcNow));

            return plan;
        }

        public Guid FeatureId(string key) =>
            FeatureCatalogue.Features.Single(feature => feature.Key == key).Id;

        public GymFeatureOverride? OverrideFor(Guid gymId, string key) =>
            FeatureCatalogue.Overrides.SingleOrDefault(over =>
                over.GymId == gymId && over.FeatureId == FeatureId(key));

        public Task<Result<IReadOnlyList<ResolvedFeatureDetail>>> SetAsync(
            Guid gymId,
            params GymFeatureRequest[] features) =>
            Dispatcher.SendAsync(new SetGymFeaturesCommand(gymId, features), CancellationToken.None);

        public Task<Result<IReadOnlyList<ResolvedFeatureDetail>>> GetAsync(Guid gymId) =>
            Dispatcher.QueryAsync(new GetGymFeaturesQuery(gymId), CancellationToken.None);
    }

    private static ResolvedFeatureDetail Resolved(
        Result<IReadOnlyList<ResolvedFeatureDetail>> result,
        string key) =>
        result.Value.Single(feature => feature.Key == key);

    [Fact]
    public async Task GrantingAFeatureTheGymWouldNotGet_StoresAnOverride()
    {
        var context = new Context(FeaturesManage);
        Gym gym = context.GivenGym();

        Result<IReadOnlyList<ResolvedFeatureDetail>> result = await context.SetAsync(
            gym.Id, new GymFeatureRequest(OffByDefault, true, Reason));

        Assert.True(result.IsSuccess);

        GymFeatureOverride stored = Assert.Single(context.FeatureCatalogue.Overrides);
        Assert.True(stored.IsEnabled);
        Assert.Equal(Reason, stored.Reason);
        Assert.Equal(Context.Operator, stored.SetByUserId);
        Assert.Equal(context.Clock.UtcNow, stored.SetAtUtc);

        // The response is the resolution: enabled, and explicitly *because* of the override.
        ResolvedFeatureDetail crm = Resolved(result, OffByDefault);
        Assert.True(crm.Enabled);
        Assert.Equal(nameof(FeatureSource.Override), crm.Source);
        Assert.Equal(Reason, crm.OverrideReason);
    }

    [Fact]
    public async Task AskingForWhatThePlanAlreadyGives_WithdrawsTheOverrideInsteadOfStoringIt()
    {
        var context = new Context(FeaturesManage);
        Gym gym = context.GivenGym();
        SubscriptionPlan plan = context.GivenSubscribedGym(gym);

        // The gym was granted CRM before its plan included it — the usual history for an override.
        await context.SetAsync(gym.Id, new GymFeatureRequest(OffByDefault, true, Reason));
        Assert.NotNull(context.OverrideFor(gym.Id, OffByDefault));

        // Then the plan starts including it, and the operator restates the same intent. The exception
        // is withdrawn rather than kept: left in place it would freeze this gym's answer the next time
        // the plan changes, for a reason nobody would remember.
        plan.SetFeature(context.FeatureId(OffByDefault), true);

        Result<IReadOnlyList<ResolvedFeatureDetail>> result = await context.SetAsync(
            gym.Id, new GymFeatureRequest(OffByDefault, true, Reason));

        Assert.True(result.IsSuccess);
        Assert.Null(context.OverrideFor(gym.Id, OffByDefault));

        ResolvedFeatureDetail crm = Resolved(result, OffByDefault);
        Assert.True(crm.Enabled);
        Assert.Equal(nameof(FeatureSource.Plan), crm.Source);
        Assert.Null(crm.OverrideReason);
    }

    [Fact]
    public async Task AskingForWhatTheDefaultAlreadyGives_StoresNothing()
    {
        var context = new Context(FeaturesManage);
        Gym gym = context.GivenGym();

        // `reports` is on by default and the gym has no plan opinion, so "turn reports on" is already
        // true. Storing a row for it would be a promise to keep an answer that nothing is threatening.
        Result<IReadOnlyList<ResolvedFeatureDetail>> result = await context.SetAsync(
            gym.Id, new GymFeatureRequest(OnByDefault, true, Reason));

        Assert.True(result.IsSuccess);
        Assert.Empty(context.FeatureCatalogue.Overrides);
        Assert.Equal(nameof(FeatureSource.Default), Resolved(result, OnByDefault).Source);
    }

    [Fact]
    public async Task WithdrawingAFeatureThePlanIncludes_StoresAnOverride()
    {
        var context = new Context(FeaturesManage);
        Gym gym = context.GivenGym();
        context.GivenSubscribedGym(gym, planSays: (OffByDefault, true));

        Result<IReadOnlyList<ResolvedFeatureDetail>> result = await context.SetAsync(
            gym.Id, new GymFeatureRequest(OffByDefault, false, "Abused the bulk email tool."));

        // Overrides run both ways: switching something off that the gym pays for is an exception too,
        // and the one most likely to be questioned later — hence the required reason.
        Assert.True(result.IsSuccess);
        GymFeatureOverride stored = Assert.Single(context.FeatureCatalogue.Overrides);
        Assert.False(stored.IsEnabled);

        ResolvedFeatureDetail crm = Resolved(result, OffByDefault);
        Assert.False(crm.Enabled);
        Assert.Equal(nameof(FeatureSource.Override), crm.Source);
    }

    [Fact]
    public async Task RestatingAnExistingOverride_ReplacesTheReasonAndTheAuthor()
    {
        var context = new Context(FeaturesManage);
        Gym gym = context.GivenGym();

        await context.SetAsync(gym.Id, new GymFeatureRequest(OffByDefault, true, Reason));
        GymFeatureOverride stored = Assert.Single(context.FeatureCatalogue.Overrides);

        var second = Guid.Parse("88888888-8888-8888-8888-888888888888");
        context.CurrentUser.UserId = second;
        context.Clock.Advance(TimeSpan.FromDays(30));

        Result<IReadOnlyList<ResolvedFeatureDetail>> result = await context.SetAsync(
            gym.Id, new GymFeatureRequest(OffByDefault, true, "Extended by the account manager."));

        // One row per (gym, feature): the current row answers "who decided this", and the audit trail
        // keeps who decided it before.
        Assert.True(result.IsSuccess);
        Assert.Same(stored, Assert.Single(context.FeatureCatalogue.Overrides));
        Assert.Equal("Extended by the account manager.", stored.Reason);
        Assert.Equal(second, stored.SetByUserId);
        Assert.Equal(context.Clock.UtcNow, stored.SetAtUtc);
    }

    [Fact]
    public async Task AnOverrideWithoutAReason_IsRefused()
    {
        var context = new Context(FeaturesManage);
        Gym gym = context.GivenGym();

        Result<IReadOnlyList<ResolvedFeatureDetail>> result = await context.SetAsync(
            gym.Id, new GymFeatureRequest(OffByDefault, true, "   "));

        Assert.True(result.IsFailure);
        Assert.Equal("features.override_reason_required", result.Error!.Code);
        Assert.Empty(context.FeatureCatalogue.Overrides);
        Assert.Equal(0, context.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task NoReasonIsNeeded_WhenTheRequestOnlyWithdrawsAnException()
    {
        var context = new Context(FeaturesManage);
        Gym gym = context.GivenGym();
        SubscriptionPlan plan = context.GivenSubscribedGym(gym);

        await context.SetAsync(gym.Id, new GymFeatureRequest(OffByDefault, true, Reason));
        Assert.NotNull(context.OverrideFor(gym.Id, OffByDefault));

        plan.SetFeature(context.FeatureId(OffByDefault), true);

        // Withdrawing needs no justification: the gym ends up with exactly what it pays for, which is
        // the state that needs no explaining.
        Result<IReadOnlyList<ResolvedFeatureDetail>> result = await context.SetAsync(
            gym.Id, new GymFeatureRequest(OffByDefault, true, null));

        Assert.True(result.IsSuccess);
        Assert.Empty(context.FeatureCatalogue.Overrides);
    }

    [Fact]
    public async Task OnlyTheKeysSent_AreTouched()
    {
        var context = new Context(FeaturesManage);
        Gym gym = context.GivenGym();

        await context.SetAsync(
            gym.Id,
            new GymFeatureRequest(OffByDefault, true, Reason),
            new GymFeatureRequest(OnByDefault, false, "Reporting confuses this owner."));

        Assert.Equal(2, context.FeatureCatalogue.Overrides.Count);

        // A client that knows about one feature must not withdraw an exception on another by omitting
        // it — the same rule the plan features follow, for the same reason.
        Result<IReadOnlyList<ResolvedFeatureDetail>> result = await context.SetAsync(
            gym.Id, new GymFeatureRequest(OffByDefault, true, "Renewed."));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, context.FeatureCatalogue.Overrides.Count);
        Assert.False(Resolved(result, OnByDefault).Enabled);
    }

    [Fact]
    public async Task AnUnknownKey_RejectsTheWholeRequest()
    {
        var context = new Context(FeaturesManage);
        Gym gym = context.GivenGym();

        Result<IReadOnlyList<ResolvedFeatureDetail>> result = await context.SetAsync(
            gym.Id,
            new GymFeatureRequest(OffByDefault, true, Reason),
            new GymFeatureRequest("teleportation", true, Reason));

        Assert.True(result.IsFailure);
        Assert.Equal("features.unknown_key", result.Error!.Code);
        Assert.Empty(context.FeatureCatalogue.Overrides);
        Assert.Equal(0, context.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task TheSameKeyTwice_IsRefusedRatherThanHalfApplied()
    {
        var context = new Context(FeaturesManage);
        Gym gym = context.GivenGym();

        Result<IReadOnlyList<ResolvedFeatureDetail>> result = await context.SetAsync(
            gym.Id,
            new GymFeatureRequest(OffByDefault, true, Reason),
            new GymFeatureRequest(OffByDefault, false, "Actually, no."));

        // One row per (gym, feature). Applying the last answer would report success for a request
        // that contradicted itself, and the unique index would refuse it as an unexplained 409.
        Assert.True(result.IsFailure);
        Assert.Equal("features.duplicate_key", result.Error!.Code);
        Assert.Empty(context.FeatureCatalogue.Overrides);
        Assert.Equal(0, context.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task ADeletedGym_CannotBeGrantedAnything()
    {
        var context = new Context(FeaturesManage);
        Gym gym = context.GivenGym();
        gym.Delete(context.Clock.UtcNow);

        Result<IReadOnlyList<ResolvedFeatureDetail>> result = await context.SetAsync(
            gym.Id, new GymFeatureRequest(OffByDefault, true, Reason));

        Assert.True(result.IsFailure);
        Assert.Equal("gyms.deleted", result.Error!.Code);
        Assert.Empty(context.FeatureCatalogue.Overrides);
    }

    [Fact]
    public async Task ASuspendedGym_CanStillBeConfigured()
    {
        var context = new Context(FeaturesManage);
        Gym gym = context.GivenGym();
        gym.Suspend("Non-payment");

        // Deliberately allowed: an operator often adjusts what a gym gets *while* it is suspended,
        // before letting it back in.
        Result<IReadOnlyList<ResolvedFeatureDetail>> result = await context.SetAsync(
            gym.Id, new GymFeatureRequest(OffByDefault, true, Reason));

        Assert.True(result.IsSuccess);
        Assert.Single(context.FeatureCatalogue.Overrides);
    }

    [Fact]
    public async Task AnUnknownGym_IsNotFound_OnBothTheReadAndTheWrite()
    {
        var context = new Context(FeaturesManage, FeaturesRead);
        var missing = Guid.NewGuid();

        Result<IReadOnlyList<ResolvedFeatureDetail>> write = await context.SetAsync(
            missing, new GymFeatureRequest(OffByDefault, true, Reason));
        Result<IReadOnlyList<ResolvedFeatureDetail>> read = await context.GetAsync(missing);

        // The read matters as much as the write: answering a wrong id with a catalogue of defaults
        // looks like a valid gym that happens to have no exceptions.
        Assert.True(write.IsFailure);
        Assert.Equal("gyms.not_found", write.Error!.Code);
        Assert.True(read.IsFailure);
        Assert.Equal("gyms.not_found", read.Error!.Code);
    }

    [Fact]
    public async Task TheReadReportsEveryFeature_WithTheLayerThatDecidedEachOne()
    {
        var context = new Context(FeaturesManage, FeaturesRead);
        Gym gym = context.GivenGym();
        context.GivenSubscribedGym(gym, planSays: (PlatformFeatures.Nutrition, true));
        await context.SetAsync(gym.Id, new GymFeatureRequest(OffByDefault, true, Reason));

        Result<IReadOnlyList<ResolvedFeatureDetail>> result = await context.GetAsync(gym.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(PlatformFeatures.All.Count, result.Value.Count);
        Assert.Equal(nameof(FeatureSource.Override), Resolved(result, OffByDefault).Source);
        Assert.Equal(nameof(FeatureSource.Plan), Resolved(result, PlatformFeatures.Nutrition).Source);
        Assert.Equal(nameof(FeatureSource.Default), Resolved(result, OnByDefault).Source);
    }

    [Fact]
    public async Task TwoGyms_ResolveTheSameFeatureDifferently_ForDifferentReasons()
    {
        var context = new Context(FeaturesManage, FeaturesRead);

        Gym granted = context.GivenGym("granted-gym");
        Gym included = context.GivenGym("included-gym");
        Gym plain = context.GivenGym("plain-gym");

        context.GivenSubscribedGym(included, "PRO", (OffByDefault, true));
        await context.SetAsync(granted.Id, new GymFeatureRequest(OffByDefault, true, Reason));

        // The point of recording the source: all three answers below are "CRM is on for one gym and
        // off for another", and only the source explains which lever to pull to change it.
        Assert.Equal(
            nameof(FeatureSource.Override),
            Resolved(await context.GetAsync(granted.Id), OffByDefault).Source);
        Assert.Equal(
            nameof(FeatureSource.Plan),
            Resolved(await context.GetAsync(included.Id), OffByDefault).Source);

        ResolvedFeatureDetail unlucky = Resolved(await context.GetAsync(plain.Id), OffByDefault);
        Assert.False(unlucky.Enabled);
        Assert.Equal(nameof(FeatureSource.Default), unlucky.Source);
    }

    [Fact]
    public async Task TheGymDetail_CarriesTheSameResolution()
    {
        var context = new Context(FeaturesManage);
        Gym gym = context.GivenGym();
        await context.SetAsync(gym.Id, new GymFeatureRequest(OffByDefault, true, Reason));

        Gyms.Abstractions.GymDetail? detail = await context.GymQueries.GetAsync(
            gym.Id, CancellationToken.None);

        // §3.3: the detail view carries resolved feature flags. One resolution, two places it is
        // reported — never two implementations of it.
        Assert.NotNull(detail);
        Assert.Equal(PlatformFeatures.All.Count, detail.Features.Count);
        ResolvedFeatureDetail crm = detail.Features.Single(feature => feature.Key == OffByDefault);
        Assert.True(crm.Enabled);
        Assert.Equal(nameof(FeatureSource.Override), crm.Source);
    }

    [Fact]
    public async Task ReadingFeatures_DoesNotLetYouChangeThem()
    {
        var context = new Context(FeaturesRead);
        Gym gym = context.GivenGym();

        // The split that makes the read permission safe to hand out widely: seeing what a gym gets is
        // not the authority to give it more.
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            context.SetAsync(gym.Id, new GymFeatureRequest(OffByDefault, true, Reason)));

        Assert.Empty(context.FeatureCatalogue.Overrides);
    }

    [Fact]
    public async Task ManagingFeatures_DoesNotRequireBillingAuthority()
    {
        var context = new Context(FeaturesManage);
        Gym gym = context.GivenGym();

        // Granting one gym an exception is administration; deciding what a plan sells is billing. A
        // support agent holds the first without the second.
        Result<IReadOnlyList<ResolvedFeatureDetail>> result = await context.SetAsync(
            gym.Id, new GymFeatureRequest(OffByDefault, true, Reason));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task AnEmptyRequest_ChangesNothing()
    {
        var context = new Context(FeaturesManage);
        Gym gym = context.GivenGym();

        Result<IReadOnlyList<ResolvedFeatureDetail>> result = await context.SetAsync(gym.Id);

        // Still a successful read of the resolution — a caller sending nothing gets the current
        // answer rather than an error about an empty list.
        Assert.True(result.IsSuccess);
        Assert.Equal(PlatformFeatures.All.Count, result.Value.Count);
        Assert.Empty(context.FeatureCatalogue.Overrides);
    }
}
