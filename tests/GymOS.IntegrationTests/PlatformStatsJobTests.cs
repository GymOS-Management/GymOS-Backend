using System.Net;
using System.Net.Http.Json;
using GymOS.Platform.Domain.Analytics;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.Platform.Infrastructure.Analytics;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.SharedKernel.Currencies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// <see cref="DailyStatsCompiler"/> over real PostgreSQL rather than fakes — every number it produces
/// is a SQL count, so a fake repository would only prove the fake's own arithmetic (the M4
/// <c>PlanRepository</c> missing-<c>Include</c> lesson, applied in advance).
///
/// <para>The job is not driven directly: <c>Jobs:Enabled=false</c> keeps the host's timer off so the
/// compiler can be called by hand, from a scope, with a fixed <c>now</c> — exactly what
/// <c>RetentionJobTests</c> does for its job.</para>
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PlatformStatsJobTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 4, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Yesterday = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(-1);

    private readonly PostgresFixture _postgres;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public PlatformStatsJobTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        string connectionString = await _postgres.CreateIsolatedDatabaseAsync("platform_stats_job");

        // The host's own timer stays off: this suite drives DailyStatsCompiler by hand, with a fixed
        // `now`, the same way RetentionJobTests drives RetentionJob.
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
    public async Task ItRecordsTheDayItIsGiven()
    {
        await SeedGymOnMonthlyPlanAsync("stats-gym", 49.99m, "EUR");

        Assert.True(await CompileAsync(Yesterday));

        DailyPlatformStats stats = await ReadAsync(Yesterday);
        Assert.Equal(1, stats.TotalGyms);
        Assert.Equal(Money.Of(49.99m, Currency.Of("EUR")), Assert.Single(stats.Mrr).Amount);
    }

    [Fact]
    public async Task ASecondRunLeavesTheRowExactlyAsItWas()
    {
        // The design's central claim. Running again after the world has changed must not rewrite
        // history with today's answer.
        await SeedGymOnMonthlyPlanAsync("first-gym", 49.99m, "EUR");
        Assert.True(await CompileAsync(Yesterday));
        DailyPlatformStats before = await ReadAsync(Yesterday);

        await SeedGymOnMonthlyPlanAsync("second-gym", 100m, "EUR");
        Assert.False(await CompileAsync(Yesterday));

        DailyPlatformStats after = await ReadAsync(Yesterday);
        Assert.Equal(before.TotalGyms, after.TotalGyms);
        Assert.Equal(before.CreatedAtUtc, after.CreatedAtUtc);
        Assert.Equal(Money.Of(49.99m, Currency.Of("EUR")), Assert.Single(after.Mrr).Amount);
    }

    [Fact]
    public async Task AMissedDayStaysMissingRatherThanBeingBackfilled()
    {
        await CompileAsync(Yesterday);

        // Nothing invents the day before: a gap is discoverable, an invented row is not.
        await using AppDbContext context = _host.CreateDbContext();
        Assert.False(await context.Set<DailyPlatformStats>()
            .AnyAsync(stats => stats.StatDate == Yesterday.AddDays(-1)));
    }

    [Fact]
    public async Task ATrialContributesNothingButStillCountsAsAGym()
    {
        await SeedGymOnTrialAsync("trial-gym", 49.99m, "EUR");

        await CompileAsync(Yesterday);

        DailyPlatformStats stats = await ReadAsync(Yesterday);
        Assert.Equal(1, stats.TotalGyms);
        Assert.Empty(stats.Mrr);
    }

    [Fact]
    public async Task AScheduledCancellationStillCountsUntilItActuallyEnds()
    {
        // The gym has paid to the period end and the subscription is still Active, so the money is
        // still real. This case cannot be tested on the calculator — MrrContribution carries only
        // status, cycle and price, and a scheduled cancellation is invisible in all three — so it
        // belongs here, where the subscription is loaded.
        Guid gym = await SeedGymOnMonthlyPlanAsync("leaving-gym", 30m, "EUR");
        await ScheduleCancellationAsync(gym);

        await CompileAsync(Yesterday);

        DailyPlatformStats stats = await ReadAsync(Yesterday);
        Assert.Equal(Money.Of(30m, Currency.Of("EUR")), Assert.Single(stats.Mrr).Amount);
    }

    [Fact]
    public async Task ADeletedGymLeavesThePopulationButStillCountsAsChurn()
    {
        // Population and event counts read differently on purpose (design §4): a deleted gym is
        // gone from the population — it is not a tenant any more — but the day it was created and
        // the day its paid subscription was cancelled by the deletion are still real events that
        // happened, so they stay in new_gyms and canceled_gyms. Suppressing them would drop the
        // population with nothing in the series to explain why.
        Guid gym = await SeedGymOnMonthlyPlanAsync("gone-gym", 49.99m, "EUR");
        await PlatformApiHelpers.DeleteGymAsync(_client, gym, "gone-gym");

        // The gym's CreatedAtUtc and its subscription's CanceledAtUtc are stamped by the host's own,
        // real clock — at SaveChanges time and at delete time respectively, both strictly later
        // than any `now` this test could capture beforehand. Reading the clock again afterwards
        // would only narrow that race, not close it: the sequence could still cross a UTC midnight
        // between the read and either write. Reading the timestamps actually stored removes the
        // race entirely — the day compiled is, by construction, the day those events were recorded
        // on. The equality assert documents (and guards) the one assumption this still relies on:
        // that the whole create-plan-assign-delete sequence lands on a single calendar day.
        DateOnly today;
        await using (AppDbContext context = _host.CreateDbContext())
        {
            DateTimeOffset createdAtUtc = await context.Set<Gym>()
                .Where(g => g.Id == gym)
                .Select(g => g.CreatedAtUtc)
                .SingleAsync();
            DateTimeOffset canceledAtUtc = await context.Set<GymSubscription>()
                .Where(subscription => subscription.GymId == gym)
                .Select(subscription => subscription.CanceledAtUtc)
                .SingleAsync() ?? throw new InvalidOperationException("The deleted gym's subscription was not cancelled.");

            Assert.Equal(
                DateOnly.FromDateTime(createdAtUtc.UtcDateTime), DateOnly.FromDateTime(canceledAtUtc.UtcDateTime));
            today = DateOnly.FromDateTime(createdAtUtc.UtcDateTime);
        }

        Assert.True(await CompileAsync(today));

        DailyPlatformStats stats = await ReadAsync(today);
        Assert.Equal(0, stats.TotalGyms);
        Assert.Equal(1, stats.NewGyms);
        Assert.Equal(1, stats.CanceledGyms);
    }

    /// <summary>A plan code is upper-case letters, digits and underscores only — a slug is not.</summary>
    private static string PlanCode(string slug) => slug.ToUpperInvariant().Replace('-', '_');

    /// <summary>Provisions a gym and puts it on a priced, non-trial plan.</summary>
    private async Task<Guid> SeedGymOnMonthlyPlanAsync(string slug, decimal amount, string currency)
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, slug);
        PlatformApiHelpers.PlanCreated plan = await PlatformApiHelpers.CreatePlanAsync(
            _client, PlanCode(slug), amount: amount);
        await PlatformApiHelpers.AssignAsync(_client, gymId, plan.Id, currency: currency);

        return gymId;
    }

    /// <summary>Provisions a gym and puts it on a plan whose trial has not yet been converted.</summary>
    private async Task<Guid> SeedGymOnTrialAsync(string slug, decimal amount, string currency)
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, slug);
        PlatformApiHelpers.PlanCreated plan = await PlatformApiHelpers.CreatePlanAsync(
            _client, PlanCode(slug), trialDays: 14, amount: amount);
        await PlatformApiHelpers.AssignAsync(_client, gymId, plan.Id, currency: currency);

        return gymId;
    }

    /// <summary>
    /// Schedules cancellation for the end of the paid period: the subscription is still <c>Active</c>
    /// and paid for, so nothing about the gym's status or its bill changes today.
    /// </summary>
    private async Task ScheduleCancellationAsync(Guid gymId)
    {
        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            new Uri($"/api/v1/gyms/{gymId}/subscription/cancel", UriKind.Relative),
            new { reason = (string?)null, immediately = false });

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 scheduling cancellation, got {(int)response.StatusCode}: " +
            await response.Content.ReadAsStringAsync());
    }

    /// <summary>Resolves <see cref="DailyStatsCompiler"/> from a fresh scope and runs it for one day.</summary>
    private async Task<bool> CompileAsync(DateOnly statDate)
    {
        using IServiceScope scope = _host.Services.CreateScope();
        DailyStatsCompiler compiler = scope.ServiceProvider.GetRequiredService<DailyStatsCompiler>();

        return await compiler.CompileAsync(statDate, Now, CancellationToken.None);
    }

    private async Task<DailyPlatformStats> ReadAsync(DateOnly statDate)
    {
        await using AppDbContext context = _host.CreateDbContext();

        return await context.Set<DailyPlatformStats>()
            .Include(stats => stats.Mrr)
            .SingleAsync(stats => stats.StatDate == statDate);
    }

    [Fact]
    public async Task AGymThatChurnsTwiceInADayIsOneChurnedGym()
    {
        // `canceled_gyms` is named, stored and published as a count of GYMS — Analytics.md groups it
        // with `new_gyms` under "what happened on this day" — but it was computed as a count of
        // GymSubscription ROWS. Nothing stops a gym holding several terminal rows: the one-live-per-gym
        // unique index is partial (`status IN (1, 2, 3)`), so a cancelled row leaves the constraint
        // behind and the next assignment inserts a fresh one.
        //
        // So: assign, cancel immediately, assign again, cancel again. That is one gym that churned,
        // and two Canceled rows. Counting rows reported 2 — and on a single-tenant platform that is a
        // churn rate of 200%, written permanently into a write-once table.
        Guid gym = await PlatformApiHelpers.CreateGymAsync(_client, "twice-gym");
        PlatformApiHelpers.PlanCreated first = await PlatformApiHelpers.CreatePlanAsync(
            _client, "TWICE1", amount: 20m);
        PlatformApiHelpers.PlanCreated second = await PlatformApiHelpers.CreatePlanAsync(
            _client, "TWICE2", amount: 25m);

        await PlatformApiHelpers.AssignAsync(_client, gym, first.Id, currency: "EUR");
        await CancelImmediatelyAsync(gym);
        await PlatformApiHelpers.AssignAsync(_client, gym, second.Id, currency: "EUR");
        await CancelImmediatelyAsync(gym);

        // Both cancellations are stamped by the host's real clock, so the day is read back from what
        // was actually stored — the same race-free approach the deleted-gym test above uses.
        DateOnly today;
        await using (AppDbContext context = _host.CreateDbContext())
        {
            List<DateTimeOffset> canceledAt = await context.Set<GymSubscription>()
                .Where(subscription => subscription.GymId == gym
                    && subscription.Status == SubscriptionStatus.Canceled)
                .Select(subscription => subscription.CanceledAtUtc!.Value)
                .ToListAsync();

            // The premise of the test: two terminal rows really do exist for this one gym.
            Assert.Equal(2, canceledAt.Count);
            Assert.Equal(
                DateOnly.FromDateTime(canceledAt[0].UtcDateTime),
                DateOnly.FromDateTime(canceledAt[1].UtcDateTime));
            today = DateOnly.FromDateTime(canceledAt[0].UtcDateTime);
        }

        Assert.True(await CompileAsync(today));

        await using AppDbContext assertions = _host.CreateDbContext();
        DailyPlatformStats stats = await assertions.Set<DailyPlatformStats>()
            .SingleAsync(row => row.StatDate == today);

        Assert.Equal(1, stats.CanceledGyms);
    }

    private async Task CancelImmediatelyAsync(Guid gymId)
    {
        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            new Uri($"/api/v1/gyms/{gymId}/subscription/cancel", UriKind.Relative),
            new { reason = (string?)null, immediately = true });

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 cancelling immediately, got {(int)response.StatusCode}: " +
            await response.Content.ReadAsStringAsync());
    }

}
