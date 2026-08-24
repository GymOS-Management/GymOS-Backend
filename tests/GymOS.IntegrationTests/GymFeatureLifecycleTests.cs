using System.Net;
using System.Text.Json;
using GymOS.BuildingBlocks.Infrastructure.Time;
using GymOS.Platform.Application.Features;
using GymOS.Platform.Application.Invoices;
using GymOS.Platform.Application.Subscriptions;
using GymOS.Platform.Domain.Features;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.Platform.Infrastructure.Invoices;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.Platform.Infrastructure.Persistence.Repositories;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// A subscription change withdrawing the exceptions it makes redundant — the maintenance M4's
/// override rule was missing (ARCHITECTURE.md §2.4). Every existing override test in
/// <see cref="GymFeatureApiTests"/> moves the override while the layers beneath it stand still, which
/// is exactly why a gym could be granted CRM as a goodwill trial, upgrade onto a plan that already
/// grants it, and keep the now-redundant row — reporting <c>source: Override</c> and a stale reason
/// for a capability it started paying for, then keeping it for free on a later downgrade.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class GymFeatureLifecycleTests : IAsyncLifetime
{
    private const string TrialReason = "3-month goodwill trial.";

    // Rule R2 / AD-40: SystemClock is the sole sanctioned DateTimeOffset.UtcNow call site — even
    // here, where the test needs "real now" to backdate a subscription and to drive the renewal
    // step at it.
    private static readonly SystemClock Clock = new();

    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public GymFeatureLifecycleTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        string connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("gym_features_lifecycle");
        // Jobs off. This suite asserts on stored gym_feature_overrides rows, and
        // FeatureOverrideSweepJob runs daily at 03:50 UTC — a CI run alive across that
        // minute would have the sweep withdraw the very exceptions these assertions
        // depend on, as an unreproducible failure that looks like a real defect.
        // FeatureOverrideSweepTests does the same, for the same reason.
        _host = AuthenticationHost.Create(
            connectionString, settings => settings["Jobs:Enabled"] = "false");
        _client = _host.CreateClient();

        using HttpResponseMessage ready = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        using HttpResponseMessage signIn = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);
        AuthApi.Authorize(_client, await AuthApi.ReadAccessTokenAsync(signIn));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task A_plan_upgrade_withdraws_an_exception_it_has_made_redundant()
    {
        // The test M4 never had. Every existing override test moves the override while the layers
        // beneath stand still, which is exactly why this defect survived six task reviews and a
        // single-pass review.
        Guid basic = await CreatePlanAsync("BASIC");
        await SetPlanFeatureAsync(basic, PlatformFeatures.Crm, isEnabled: false);

        Guid pro = await CreatePlanAsync("PRO");
        await SetPlanFeatureAsync(pro, PlatformFeatures.Crm, isEnabled: true);

        Guid gym = await CreateGymAsync("upgrading-gym");
        await AssignAsync(gym, basic);

        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, TrialReason));
        Assert.Equal(1, await CountOverridesAsync(gym));

        // The upgrade. The plan now grants what the exception granted.
        await AssignAsync(gym, pro);

        Assert.Equal(0, await CountOverridesAsync(gym));

        JsonElement crm = Feature(await ReadFeaturesAsync(gym), PlatformFeatures.Crm);
        Assert.True(crm.GetProperty("enabled").GetBoolean());
        Assert.Equal("Plan", crm.GetProperty("source").GetString());
        Assert.Null(crm.GetProperty("overrideReason").GetString());
    }

    [Fact]
    public async Task A_later_downgrade_does_not_resurrect_the_withdrawn_exception()
    {
        // The consequence that costs money: without withdrawal, the stale row wins on the way back
        // down and the gym keeps a capability it stopped paying for.
        Guid basic = await CreatePlanAsync("BASIC");
        await SetPlanFeatureAsync(basic, PlatformFeatures.Crm, isEnabled: false);
        Guid pro = await CreatePlanAsync("PRO");
        await SetPlanFeatureAsync(pro, PlatformFeatures.Crm, isEnabled: true);

        Guid gym = await CreateGymAsync("round-trip-gym");
        await AssignAsync(gym, basic);
        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, TrialReason));
        await AssignAsync(gym, pro);
        await AssignAsync(gym, basic);

        JsonElement crm = Feature(await ReadFeaturesAsync(gym), PlatformFeatures.Crm);
        Assert.False(crm.GetProperty("enabled").GetBoolean());
        Assert.Equal("Plan", crm.GetProperty("source").GetString());
    }

    [Fact]
    public async Task An_exception_that_still_differs_survives_a_plan_change()
    {
        // The guard against over-eager withdrawal: this one is still doing work.
        Guid basic = await CreatePlanAsync("BASIC");
        Guid other = await CreatePlanAsync("OTHER");
        Guid gym = await CreateGymAsync("still-differs-gym");
        await AssignAsync(gym, basic);
        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, "Contractual."));

        await AssignAsync(gym, other);

        Assert.Equal(1, await CountOverridesAsync(gym));
        Assert.Equal("Override", Feature(await ReadFeaturesAsync(gym), PlatformFeatures.Crm)
            .GetProperty("source").GetString());
    }

    [Fact]
    public async Task A_pending_plan_change_applied_at_renewal_withdraws_a_redundant_exception()
    {
        // The gap the coordinator caught: the brief for this task enumerated three renewal
        // terminal branches and missed a fourth. A plan change scheduled for the period boundary
        // (a cross-cycle or mid-period change) is applied by the renewal step, not by a console
        // request — and that is still "the gym's plan changed", so the sweep must not be the first
        // thing to notice a now-redundant exception.
        Guid basic = await CreatePlanAsync("BASIC");
        await SetPlanFeatureAsync(basic, PlatformFeatures.Crm, isEnabled: false);
        Guid pro = await CreatePlanAsync("PRO");
        await SetPlanFeatureAsync(pro, PlatformFeatures.Crm, isEnabled: true);

        Guid gym = await CreateGymAsync("pending-change-gym");

        // Seeded directly rather than through AssignAsync/SchedulePlanChange's own HTTP path,
        // backdated so the pending change is already due at the boundary without waiting for real
        // time to pass — the same backdating RenewalSweepTests uses to make a subscription due.
        Guid subscriptionId = await SeedSubscriptionWithPendingChangeAsync(gym, basic, pro);

        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, TrialReason));
        Assert.Equal(1, await CountOverridesAsync(gym));

        // Drive the renewal step itself the way RenewalSweepTests does: construct the service
        // directly over a fresh AppDbContext and call ProcessAsync, rather than inventing a second
        // mechanism to trigger it.
        RenewalAction action = await ProcessRenewalAsync(subscriptionId);
        Assert.Equal(RenewalAction.AppliedPendingChangeAndRenewed, action);

        Assert.Equal(0, await CountOverridesAsync(gym));

        JsonElement crm = Feature(await ReadFeaturesAsync(gym), PlatformFeatures.Crm);
        Assert.True(crm.GetProperty("enabled").GetBoolean());
        Assert.Equal("Plan", crm.GetProperty("source").GetString());
    }

    [Fact]
    public async Task An_override_granted_before_any_subscription_is_withdrawn_by_the_first_assignment()
    {
        // The maintainer must see the plan the gym is about to have, not the plan a database query
        // says it has: a brand-new assignment is an unsaved Add, invisible to any query run before
        // the transaction commits.
        Guid pro = await CreatePlanAsync("PRO");
        await SetPlanFeatureAsync(pro, PlatformFeatures.Crm, isEnabled: true);

        Guid gym = await CreateGymAsync("no-subscription-yet-gym");

        // Granted while the gym has no subscription at all — judged against the platform default
        // (CRM off), so the override is needed and stored.
        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, TrialReason));
        Assert.Equal(1, await CountOverridesAsync(gym));

        // The gym's very first assignment — onto a plan that already grants what the exception
        // granted.
        await AssignAsync(gym, pro);

        Assert.Equal(0, await CountOverridesAsync(gym));

        JsonElement crm = Feature(await ReadFeaturesAsync(gym), PlatformFeatures.Crm);
        Assert.True(crm.GetProperty("enabled").GetBoolean());
        Assert.Equal("Plan", crm.GetProperty("source").GetString());
    }

    [Fact]
    public async Task An_override_that_becomes_redundant_because_the_subscription_ended_is_withdrawn_on_expiry()
    {
        // Reports ships on by default; the plan explicitly withholds it, so the override granting it
        // back differs from the plan and is needed. Once the subscription ends there is no live plan
        // opinion left to differ from — only the platform default, which already agrees with the
        // override — so it must go. The maintainer must see "no live plan" here, not the plan that is
        // in the middle of ending: CancelNow/Expire only set Status in memory, so a query run before
        // the save still matches the not-yet-saved row as live.
        Guid basic = await CreatePlanAsync("BASIC");
        await SetPlanFeatureAsync(basic, PlatformFeatures.Reports, isEnabled: false);

        Guid gym = await CreateGymAsync("expiring-gym");

        // Seeded directly and backdated with auto-renew off, so the renewal step expires it without
        // waiting for real time to pass — the same backdating the pending-change test above uses.
        Guid subscriptionId = await SeedExpiringSubscriptionAsync(gym, basic);

        await SetGymFeaturesAsync(gym, (PlatformFeatures.Reports, true, "Needed reports during a dispute."));
        Assert.Equal(1, await CountOverridesAsync(gym));

        RenewalAction action = await ProcessRenewalAsync(subscriptionId);
        Assert.Equal(RenewalAction.Expired, action);

        Assert.Equal(0, await CountOverridesAsync(gym));

        JsonElement reports = Feature(await ReadFeaturesAsync(gym), PlatformFeatures.Reports);
        Assert.True(reports.GetProperty("enabled").GetBoolean());
        Assert.Equal("Default", reports.GetProperty("source").GetString());
    }

    [Fact]
    public async Task A_gym_deletion_withdraws_an_exception_it_has_made_redundant()
    {
        // The gap the whole-branch review found: DeleteGymCommandHandler cancels the live
        // subscription (the plan layer goes silent, exactly like the expiry test above) but was the
        // only CancelNow/Expire/ChangePlan/Assign site never wired to the maintainer. Without the
        // fix, this override survives deletion and is only cleared by the 03:50 UTC sweep — up to a
        // day in which a deleted gym's `GET /gyms/{id}` reports `source: Override` for a capability
        // that no longer differs from the default.
        Guid basic = await CreatePlanAsync("BASIC");
        await SetPlanFeatureAsync(basic, PlatformFeatures.Reports, isEnabled: false);

        Guid gym = await CreateGymAsync("deleted-gym");
        await AssignAsync(gym, basic);

        await SetGymFeaturesAsync(gym, (PlatformFeatures.Reports, true, "Needed reports during a dispute."));
        Assert.Equal(1, await CountOverridesAsync(gym));

        await PlatformApiHelpers.DeleteGymAsync(_client, gym, "deleted-gym");

        // Same transaction as the deletion, not the next sweep: no job ran between the write above
        // and this read.
        Assert.Equal(0, await CountOverridesAsync(gym));

        // Reading a deleted gym's features still works (Features.md: "a deleted gym cannot be
        // configured" is deliberately not "cannot be read"), and now reports the truth: no live
        // plan, so the platform default decides, and the default agrees with what the override used
        // to grant.
        JsonElement reports = Feature(await ReadFeaturesAsync(gym), PlatformFeatures.Reports);
        Assert.True(reports.GetProperty("enabled").GetBoolean());
        Assert.Equal("Default", reports.GetProperty("source").GetString());
        Assert.Null(reports.GetProperty("overrideReason").GetString());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────
    // Thin wrappers binding the shared PlatformApiHelpers / FeatureApiHelpers to this fixture's
    // client/host — see GymFeatureApiTests for the same pattern and why the five feature helpers
    // are not copied here.

    private async Task<Guid> CreatePlanAsync(string code) =>
        (await PlatformApiHelpers.CreatePlanAsync(_client, code)).Id;

    private Task<Guid> CreateGymAsync(string slug) =>
        PlatformApiHelpers.CreateGymAsync(_client, slug);

    private Task<HttpResponseMessage> AssignAsync(Guid gymId, Guid planId) =>
        PlatformApiHelpers.AssignAsync(_client, gymId, planId);

    private Task SetPlanFeatureAsync(Guid planId, string featureKey, bool isEnabled) =>
        FeatureApiHelpers.SetPlanFeatureAsync(_client, planId, featureKey, isEnabled);

    private Task<JsonElement> SetGymFeaturesAsync(
        Guid gymId,
        params (string Key, bool IsEnabled, string? Reason)[] features) =>
        FeatureApiHelpers.SetGymFeaturesAsync(_client, gymId, features);

    private Task<int> CountOverridesAsync(Guid gymId) =>
        FeatureApiHelpers.CountOverridesAsync(_host, gymId);

    private Task<JsonElement> ReadFeaturesAsync(Guid gymId) =>
        FeatureApiHelpers.ReadFeaturesAsync(_client, gymId);

    private static JsonElement Feature(JsonElement features, string key) =>
        FeatureApiHelpers.Feature(features, key);

    /// <summary>
    /// Seeds a live subscription on <paramref name="currentPlanId"/> with a plan change already
    /// pending to <paramref name="pendingPlanId"/> — assigned five weeks ago, exactly as
    /// <c>RenewalSweepTests.SeedSubscriptionAssignedAtAsync</c> backdates, so the period has ended
    /// and the pending change (scheduled for that same period's boundary) is due without waiting
    /// for real time to pass.
    /// </summary>
    private async Task<Guid> SeedSubscriptionWithPendingChangeAsync(
        Guid gymId, Guid currentPlanId, Guid pendingPlanId)
    {
        await using AppDbContext context = _host.CreateDbContext();
        var plans = new PlanRepository(context);

        SubscriptionPlan currentPlan = await plans.FindByIdAsync(currentPlanId, CancellationToken.None)
            ?? throw new InvalidOperationException("Seeded current plan not found.");
        SubscriptionPlan pendingPlan = await plans.FindByIdAsync(pendingPlanId, CancellationToken.None)
            ?? throw new InvalidOperationException("Seeded pending plan not found.");

        DateTimeOffset assignedAt = Clock.UtcNow.AddDays(-35);
        GymSubscription subscription = GymSubscription.Assign(
            gymId, currentPlan, SubscriptionTerms.From(currentPlan.Prices.Single()), assignedAt);

        // At the period boundary, exactly what AssignSubscriptionCommandHandler schedules for a
        // cross-cycle or mid-period change — already due, since that boundary is weeks in the past.
        subscription.SchedulePlanChange(
            pendingPlan, SubscriptionTerms.From(pendingPlan.Prices.Single()), subscription.CurrentPeriodEndUtc);

        context.Add(subscription);
        await context.SaveChangesAsync();

        return subscription.Id;
    }

    /// <summary>
    /// Seeds a live subscription on <paramref name="planId"/>, assigned five weeks ago with
    /// auto-renew off, so the renewal step expires it without waiting for real time to pass.
    /// </summary>
    private async Task<Guid> SeedExpiringSubscriptionAsync(Guid gymId, Guid planId)
    {
        await using AppDbContext context = _host.CreateDbContext();
        var plans = new PlanRepository(context);

        SubscriptionPlan plan = await plans.FindByIdAsync(planId, CancellationToken.None)
            ?? throw new InvalidOperationException("Seeded plan not found.");

        DateTimeOffset assignedAt = Clock.UtcNow.AddDays(-35);
        GymSubscription subscription = GymSubscription.Assign(
            gymId, plan, SubscriptionTerms.From(plan.Prices.Single()), assignedAt);
        subscription.DisableAutoRenew();

        context.Add(subscription);
        await context.SaveChangesAsync();

        return subscription.Id;
    }

    /// <summary>
    /// Drives one renewal-sweep step directly over a fresh <see cref="AppDbContext"/> — the same
    /// construction <c>RenewalSweepTests.Service</c> uses, so this test exercises the real
    /// production wiring (including the real <see cref="FeatureExceptionMaintainer"/>) rather than
    /// a second, test-only way of invoking the sweep.
    /// </summary>
    private async Task<RenewalAction> ProcessRenewalAsync(Guid subscriptionId)
    {
        await using AppDbContext context = _host.CreateDbContext();

        var service = new SubscriptionRenewalService(
            new SubscriptionRepository(context),
            new PlanRepository(context),
            new InvoiceIssuer(new InvoiceRepository(context), new InvoiceNumberGenerator(context), Clock),
            new FeatureExceptionMaintainer(new FeatureRepository(context), new PlanRepository(context)),
            new UnitOfWork(context));

        return await service.ProcessAsync(subscriptionId, Clock.UtcNow, CancellationToken.None);
    }
}
