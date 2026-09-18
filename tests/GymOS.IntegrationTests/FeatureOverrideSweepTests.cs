using System.Net;
using System.Text.Json;
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Domain.Features;
using GymOS.Platform.Infrastructure.Jobs;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The nightly sweep (M4.1 task 3) over real SQL Server: the two triggers whose blast radius is
/// every gym on a plan — a plan editing its own feature opinion, and a platform default flipping —
/// are too wide to withdraw inline (task 2's synchronous hook only covers one gym's own
/// subscription changing), so they wait for <see cref="FeatureOverrideSweepJob"/> instead.
///
/// <para>The host's own timer stays off (<c>Jobs:Enabled=false</c>): this suite drives the job by
/// hand, from a scope, exactly as <c>RetentionJobTests</c>/<c>PlatformStatsJobTests</c> drive
/// theirs.</para>
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class FeatureOverrideSweepTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public FeatureOverrideSweepTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        string connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("feature_override_sweep");

        // The job's own daily timer stays off: this suite drives FeatureOverrideSweepJob by hand,
        // the same way PlatformStatsJobTests drives DailyStatsCompiler.
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
        await _sqlServer.DropDatabaseAsync(_host.ConnectionString);
    }

    [Fact]
    public async Task It_withdraws_an_exception_a_plan_edit_made_redundant()
    {
        // The platform-wide trigger: the operator edited the PLAN, not the gym, so nothing
        // synchronous ran. The sweep is the guarantee.
        Guid plan = await CreatePlanAsync("SWEEP");
        Guid gym = await CreateGymAsync("swept-gym");
        await AssignAsync(gym, plan);
        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, "Goodwill."));

        await SetPlanFeatureAsync(plan, PlatformFeatures.Crm, isEnabled: true);
        Assert.Equal(1, await CountOverridesAsync(gym));   // still there: no sweep yet

        await RunSweepAsync();

        Assert.Equal(0, await CountOverridesAsync(gym));
    }

    [Fact]
    public async Task It_withdraws_an_exception_a_default_flip_made_redundant()
    {
        Guid gym = await CreateGymAsync("default-swept-gym");   // no subscription: default decides
        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, "Goodwill."));

        await SetFeatureDefaultAsync(PlatformFeatures.Crm, defaultEnabled: true);

        await RunSweepAsync();

        Assert.Equal(0, await CountOverridesAsync(gym));
    }

    [Fact]
    public async Task It_leaves_an_exception_that_still_differs()
    {
        Guid gym = await CreateGymAsync("untouched-gym");
        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, "Contractual."));

        await RunSweepAsync();

        Assert.Equal(1, await CountOverridesAsync(gym));
    }

    [Fact]
    public async Task Running_it_twice_changes_nothing_the_second_time()
    {
        // The IScheduledJob contract: a re-run must be a no-op. A sweep that deleted something on
        // every pass would be deleting exceptions that still differ.
        Guid plan = await CreatePlanAsync("IDEM");
        Guid gym = await CreateGymAsync("idempotent-gym");
        await AssignAsync(gym, plan);
        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, "Goodwill."));
        await SetPlanFeatureAsync(plan, PlatformFeatures.Crm, isEnabled: true);

        Assert.Equal(1, await RunSweepAsync());
        Assert.Equal(0, await RunSweepAsync());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────
    // Thin wrappers binding the shared PlatformApiHelpers / FeatureApiHelpers to this fixture's
    // client/host — see GymFeatureLifecycleTests for the same pattern.

    private async Task<Guid> CreatePlanAsync(string code) =>
        (await PlatformApiHelpers.CreatePlanAsync(_client, code)).Id;

    private Task<Guid> CreateGymAsync(string slug) =>
        PlatformApiHelpers.CreateGymAsync(_client, slug);

    private Task<HttpResponseMessage> AssignAsync(Guid gymId, Guid planId) =>
        PlatformApiHelpers.AssignAsync(_client, gymId, planId);

    private Task SetPlanFeatureAsync(Guid planId, string featureKey, bool isEnabled) =>
        FeatureApiHelpers.SetPlanFeatureAsync(_client, planId, featureKey, isEnabled);

    private Task SetFeatureDefaultAsync(string featureKey, bool defaultEnabled) =>
        FeatureApiHelpers.SetFeatureDefaultAsync(_client, featureKey, defaultEnabled);

    private Task<JsonElement> SetGymFeaturesAsync(
        Guid gymId,
        params (string Key, bool IsEnabled, string? Reason)[] features) =>
        FeatureApiHelpers.SetGymFeaturesAsync(_client, gymId, features);

    private Task<int> CountOverridesAsync(Guid gymId) =>
        FeatureApiHelpers.CountOverridesAsync(_host, gymId);

    /// <summary>Resolves the job from a fresh scope and runs one pass, returning how many exceptions
    /// it withdrew — the count that makes the idempotence test meaningful.</summary>
    private async Task<int> RunSweepAsync()
    {
        using IServiceScope scope = _host.Services.CreateScope();
        FeatureOverrideSweepJob job = scope.ServiceProvider.GetRequiredService<FeatureOverrideSweepJob>();

        return await job.ExecuteAndCountAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_stale_override_delete_surfaces_as_ConcurrencyConflictException_not_the_EF_type()
    {
        // This pins the fact the sweep's own error handling depends on, and whose absence let a broken
        // fix live for two days.
        //
        // GymFeatureOverride carries an xmin row version, so an operator restating an override between
        // the sweep's read and its delete makes the delete match nothing. The sweep catches that as an
        // expected interleaving rather than a failure — but it was catching DbUpdateConcurrencyException,
        // an EF type that UnitOfWork translates and therefore never lets past the persistence boundary.
        // Its own docs say so: "DbUpdateConcurrencyException — an EF type — never reaches the
        // Application layer". The catch was unreachable, LogGymChangedUnderneath could never fire, and
        // the expected race kept being reported at Error.
        //
        // Nothing anywhere asserted which exception actually crosses that boundary. Now something does:
        // if UnitOfWork stops translating, this fails and the sweep's catch becomes visibly wrong again.
        Guid plan = await CreatePlanAsync("STALE");
        Guid gym = await CreateGymAsync("stale-gym");
        await AssignAsync(gym, plan);
        await SetGymFeaturesAsync(gym, ("crm", true, "granted for the race"));

        await using AsyncServiceScope scope = _host.Services.GetRequiredService<IServiceScopeFactory>()
            .CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // Load the override, then have somebody else change it underneath — the operator winning the
        // race the sweep is designed to lose.
        GymFeatureOverride tracked = await context.Set<GymFeatureOverride>()
            .SingleAsync(over => over.GymId == gym);

        await using (AppDbContext other = _host.CreateDbContext())
        {
            GymFeatureOverride theirs = await other.Set<GymFeatureOverride>()
                .SingleAsync(over => over.GymId == gym);
            theirs.Restate("crm", isEnabled: true, reason: "the operator restated it", setByUserId: null, now: new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
            await other.SaveChangesAsync();
        }

        context.Set<GymFeatureOverride>().Remove(tracked);

        // The point of the test: this is the type that crosses the boundary.
        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => unitOfWork.SaveChangesAsync(CancellationToken.None));
    }

}
