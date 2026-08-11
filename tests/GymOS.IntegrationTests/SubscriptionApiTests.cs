using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// Subscriptions over real HTTP against real PostgreSQL. Two things only a database can prove are here:
/// that the price snapshot survives a repricing of the plan, and that the partial unique index really
/// allows only one live subscription per gym while keeping every ended one.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class SubscriptionApiTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public SubscriptionApiTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        string connectionString = await _postgres.CreateIsolatedDatabaseAsync("subscriptions");
        _host = AuthenticationHost.Create(connectionString);
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
    public async Task AssigningAPlan_StartsTheSubscriptionAndShowsTheAgreedTerms()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "assign-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(_client, "ASSIGN", trialDays: 0, amount: 49.99m)).Id;

        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(
            _client, gymId, planId, "Monthly", "EUR");

        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        JsonElement body = await assigned.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Active", body.GetProperty("status").GetString());
        Assert.Equal("ASSIGN", body.GetProperty("planCode").GetString());
        Assert.Equal("Monthly", body.GetProperty("billingCycle").GetString());
        Assert.Equal(1, body.GetProperty("billingCycleMonths").GetInt32());
        Assert.Equal("EUR", body.GetProperty("currency").GetString());
        Assert.Equal(49.99m, body.GetProperty("amount").GetDecimal());
        Assert.True(body.GetProperty("autoRenew").GetBoolean());
        Assert.NotNull(assigned.Headers.ETag);
    }

    [Fact]
    public async Task AssigningAPlanWithATrial_StartsTrialing()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "trial-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(_client, "TRIALED", trialDays: 14, amount: 29.99m)).Id;

        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(
            _client, gymId, planId, "Monthly", "EUR");
        JsonElement body = await assigned.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Trialing", body.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("trialEndsAtUtc").ValueKind);
    }

    [Fact]
    public async Task ThePriceIsASnapshot_UnaffectedByLaterRepricing()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "snapshot-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(_client, "SNAPSHOT", trialDays: 0, amount: 49.99m)).Id;
        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(
            _client, gymId, planId, "Monthly", "EUR");

        // Reprice the plan. §2.3: "renewals honor the price at assignment" — so this must not follow.
        // An FK-only snapshot could not give this, because setting a price mutates the row in place.
        using HttpResponseMessage repriced = await SetPriceAsync(planId, 79.99m);
        Assert.Equal(HttpStatusCode.OK, repriced.StatusCode);

        JsonElement subscription = await GetSubscriptionAsync(gymId);
        Assert.Equal(49.99m, subscription.GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task ThePriceSnapshotSurvivesWithdrawingThePriceFromThePlan()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "withdrawn-price-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(
            _client, "WITHDRAWNPRICE", trialDays: 0, amount: 39.99m)).Id;
        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(
            _client, gymId, planId, "Monthly", "EUR");

        Guid priceId = await FirstPriceIdAsync(planId);
        using var removeRequest = new HttpRequestMessage(
            HttpMethod.Delete, new Uri($"/api/v1/plans/{planId}/prices/{priceId}", UriKind.Relative));
        removeRequest.Headers.TryAddWithoutValidation(
            "If-Match", $"\"{await PlanRowVersionAsync(planId)}\"");
        using HttpResponseMessage removed = await _client.SendAsync(removeRequest);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        // The FK is nulled by ON DELETE SET NULL; the copied amount is what the gym still pays.
        JsonElement subscription = await GetSubscriptionAsync(gymId);
        Assert.Equal(39.99m, subscription.GetProperty("amount").GetDecimal());
        Assert.Equal("Active", subscription.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ChangingPlan_KeepsOneSubscriptionRatherThanChurningIt()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "upgrade-gym");
        Guid starter = (await PlatformApiHelpers.CreatePlanAsync(
            _client, "STARTER_U", trialDays: 0, amount: 19.99m)).Id;
        Guid pro = (await PlatformApiHelpers.CreatePlanAsync(_client, "PRO_U", trialDays: 0, amount: 99.99m)).Id;

        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(
            _client, gymId, starter, "Monthly", "EUR");
        using HttpResponseMessage changed = await PlatformApiHelpers.AssignAsync(
            _client, gymId, pro, "Monthly", "EUR");

        JsonElement body = await changed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PRO_U", body.GetProperty("planCode").GetString());
        Assert.Equal(99.99m, body.GetProperty("amount").GetDecimal());
        Assert.Equal("Active", body.GetProperty("status").GetString());

        // One row, not two: an upgrade recorded as a cancellation would inflate churn.
        JsonElement history = await GetHistoryAsync(gymId);
        Assert.Equal(1, history.GetArrayLength());
    }

    [Fact]
    public async Task OnlyOneLiveSubscriptionPerGym_ButEveryEndedOneIsKept()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "history-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(_client, "HISTORY", trialDays: 0, amount: 49.99m)).Id;

        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(
            _client, gymId, planId, "Monthly", "EUR");
        await CancelAsync(gymId, immediately: true);

        // A returning gym gets a *new* subscription; the partial unique index permits this only because
        // the first is no longer live.
        using HttpResponseMessage second = await PlatformApiHelpers.AssignAsync(
            _client, gymId, planId, "Monthly", "EUR");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        JsonElement history = await GetHistoryAsync(gymId);
        Assert.Equal(2, history.GetArrayLength());

        var statuses = history.EnumerateArray()
            .Select(entry => entry.GetProperty("status").GetString())
            .ToList();
        Assert.Contains("Canceled", statuses);
        Assert.Contains("Active", statuses);
    }

    [Fact]
    public async Task ThePartialUniqueIndexExistsAndCoversTheLiveStatuses()
    {
        // The one-live-per-gym rule is only as strong as this index. Nothing else in the suite would
        // notice if the filter were dropped or its status list drifted from the enum.
        await using var connection = new Npgsql.NpgsqlConnection(_host.ConnectionString);
        await connection.OpenAsync();

        await using Npgsql.NpgsqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT indexdef FROM pg_indexes
            WHERE schemaname = 'platform'
              AND tablename = 'gym_subscriptions'
              AND indexname = 'ux_gym_subscriptions_one_live_per_gym';
            """;

        string? definition = (string?)await command.ExecuteScalarAsync();

        Assert.NotNull(definition);
        Assert.Contains("UNIQUE", definition, StringComparison.Ordinal);
        Assert.Contains("gym_id", definition, StringComparison.Ordinal);
        Assert.Contains("status = ANY", definition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SchedulingCancellation_KeepsServiceToTheEndOfThePeriod()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "scheduled-cancel-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(
            _client, "SCHEDCANCEL", trialDays: 0, amount: 49.99m)).Id;
        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(
            _client, gymId, planId, "Monthly", "EUR");

        using HttpResponseMessage canceled = await CancelAsync(
            gymId, immediately: false, reason: "Too expensive");

        JsonElement body = await canceled.Content.ReadFromJsonAsync<JsonElement>();

        // Still active: the gym paid to the end of the period, and cutting it off now would be theft.
        Assert.Equal("Active", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("cancelAtPeriodEnd").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("canceledAtUtc").ValueKind);
        Assert.Equal("Too expensive", body.GetProperty("cancellationReason").GetString());
    }

    [Fact]
    public async Task ResumingWithdrawsAScheduledCancellation()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "resume-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(_client, "RESUMED", trialDays: 0, amount: 49.99m)).Id;
        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(
            _client, gymId, planId, "Monthly", "EUR");
        await CancelAsync(gymId, immediately: false);

        using HttpResponseMessage resumed = await _client.PostAsync(
            SubscriptionUri(gymId, "resume"), content: null);

        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        JsonElement body = await resumed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("cancelAtPeriodEnd").GetBoolean());
    }

    [Fact]
    public async Task CancellingImmediately_EndsItAndLeavesTheGymWithNoLiveSubscription()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "immediate-cancel-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(
            _client, "IMMEDIATE", trialDays: 0, amount: 49.99m)).Id;
        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(
            _client, gymId, planId, "Monthly", "EUR");

        using HttpResponseMessage canceled = await CancelAsync(gymId, immediately: true, reason: "Closed");
        JsonElement body = await canceled.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Canceled", body.GetProperty("status").GetString());

        // The gym now has no *live* subscription, which is a normal state, not an error condition.
        using HttpResponseMessage current = await _client.GetAsync(SubscriptionUri(gymId));
        Assert.Equal(HttpStatusCode.NotFound, current.StatusCode);
        Assert.Equal("subscriptions.not_found", await AuthApi.ReadProblemCodeAsync(current));
    }

    [Fact]
    public async Task ConvertingATrialStartsBilling()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "convert-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(
            _client, "CONVERTED", trialDays: 14, amount: 29.99m)).Id;
        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(
            _client, gymId, planId, "Monthly", "EUR");

        using HttpResponseMessage converted = await _client.PostAsync(
            SubscriptionUri(gymId, "convert-trial"), content: null);

        Assert.Equal(HttpStatusCode.OK, converted.StatusCode);
        JsonElement body = await converted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Active", body.GetProperty("status").GetString());

        // Converting twice is refused rather than silently accepted.
        using HttpResponseMessage again = await _client.PostAsync(
            SubscriptionUri(gymId, "convert-trial"), content: null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("subscriptions.not_trialing", await AuthApi.ReadProblemCodeAsync(again));
    }

    [Fact]
    public async Task PastDueDoesNotEndTheSubscription()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "pastdue-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(_client, "PASTDUE", trialDays: 0, amount: 49.99m)).Id;
        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(
            _client, gymId, planId, "Monthly", "EUR");

        using HttpResponseMessage late = await _client.PostAsJsonAsync(
            SubscriptionUri(gymId, "payment-state"), new { pastDue = true });
        JsonElement lateBody = await late.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PastDue", lateBody.GetProperty("status").GetString());

        // Still the gym's live subscription — service is withheld by suspending the gym, not by this.
        JsonElement current = await GetSubscriptionAsync(gymId);
        Assert.Equal("PastDue", current.GetProperty("status").GetString());

        using HttpResponseMessage paid = await _client.PostAsJsonAsync(
            SubscriptionUri(gymId, "payment-state"), new { pastDue = false });
        JsonElement paidBody = await paid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Active", paidBody.GetProperty("status").GetString());
    }

    [Fact]
    public async Task TermsThePlanDoesNotOfferAreRefused()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "terms-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(_client, "EURONLY", trialDays: 0, amount: 49.99m)).Id;

        // The plan prices Monthly/EUR only. Guessing a substitute would bill an unagreed amount.
        using HttpResponseMessage wrongCurrency = await PlatformApiHelpers.AssignAsync(
            _client, gymId, planId, "Monthly", "GBP", assertSuccess: false);
        using HttpResponseMessage wrongCycle = await PlatformApiHelpers.AssignAsync(
            _client, gymId, planId, "Yearly", "EUR", assertSuccess: false);

        Assert.Equal(HttpStatusCode.BadRequest, wrongCurrency.StatusCode);
        Assert.Equal("subscriptions.price_not_offered", await AuthApi.ReadProblemCodeAsync(wrongCurrency));
        Assert.Equal("subscriptions.price_not_offered", await AuthApi.ReadProblemCodeAsync(wrongCycle));
    }

    [Fact]
    public async Task AWithdrawnPlanCannotBeNewlyAssigned()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "withdrawn-plan-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(
            _client, "WITHDRAWN_P", trialDays: 0, amount: 49.99m)).Id;

        using HttpResponseMessage deactivated = await _client.PostAsync(
            new Uri($"/api/v1/plans/{planId}/deactivate", UriKind.Relative), content: null);
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);

        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(
            _client, gymId, planId, "Monthly", "EUR", assertSuccess: false);

        Assert.Equal(HttpStatusCode.Conflict, assigned.StatusCode);
        Assert.Equal("subscriptions.plan_inactive", await AuthApi.ReadProblemCodeAsync(assigned));
    }

    [Fact]
    public async Task ADeletedGymCannotBeSubscribed()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "doomed-sub-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(
            _client, "FORDOOMED", trialDays: 0, amount: 49.99m)).Id;

        using var deleteRequest = new HttpRequestMessage(
            HttpMethod.Delete, new Uri($"/api/v1/gyms/{gymId}", UriKind.Relative));
        deleteRequest.Headers.TryAddWithoutValidation("X-Confirm-Action", "doomed-sub-gym");
        using HttpResponseMessage deleted = await _client.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(
            _client, gymId, planId, "Monthly", "EUR", assertSuccess: false);

        Assert.Equal(HttpStatusCode.Conflict, assigned.StatusCode);
        Assert.Equal("subscriptions.gym_deleted", await AuthApi.ReadProblemCodeAsync(assigned));
    }

    [Fact]
    public async Task ASuspendedGymCanStillBeSubscribed()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "suspended-sub-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(
            _client, "FORSUSPENDED", trialDays: 0, amount: 49.99m)).Id;

        using HttpResponseMessage suspended = await _client.PostAsJsonAsync(
            new Uri($"/api/v1/gyms/{gymId}/suspend", UriKind.Relative),
            new { reason = "Non-payment" });
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);

        // Resolving a billing dispute often *is* fixing the subscription, so this must be allowed.
        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(
            _client, gymId, planId, "Monthly", "EUR");
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
    }

    [Fact]
    public async Task TheGymDetailViewNowCarriesTheSubscription()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "detail-sub-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(_client, "ONDETAIL", trialDays: 0, amount: 59.99m)).Id;

        using HttpResponseMessage before = await _client.GetAsync(
            new Uri($"/api/v1/gyms/{gymId}", UriKind.Relative));
        JsonElement beforeBody = await before.Content.ReadFromJsonAsync<JsonElement>();

        // Null, not absent: a gym can exist before it is sold anything.
        Assert.Equal(JsonValueKind.Null, beforeBody.GetProperty("subscription").ValueKind);

        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(
            _client, gymId, planId, "Monthly", "EUR");

        using HttpResponseMessage after = await _client.GetAsync(
            new Uri($"/api/v1/gyms/{gymId}", UriKind.Relative));
        JsonElement afterBody = await after.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement subscription = afterBody.GetProperty("subscription");

        Assert.Equal("ONDETAIL", subscription.GetProperty("planCode").GetString());
        Assert.Equal(59.99m, subscription.GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task ThePlanNowReportsHowManyGymsAreOnIt()
    {
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(_client, "COUNTED", trialDays: 0, amount: 49.99m)).Id;
        Guid firstGym = await PlatformApiHelpers.CreateGymAsync(_client, "counted-gym-one");
        Guid secondGym = await PlatformApiHelpers.CreateGymAsync(_client, "counted-gym-two");

        using HttpResponseMessage assignedFirst = await PlatformApiHelpers.AssignAsync(
            _client, firstGym, planId, "Monthly", "EUR");
        using HttpResponseMessage assignedSecond = await PlatformApiHelpers.AssignAsync(
            _client, secondGym, planId, "Monthly", "EUR");

        using HttpResponseMessage plan = await _client.GetAsync(
            new Uri($"/api/v1/plans/{planId}", UriKind.Relative));
        JsonElement body = await plan.Content.ReadFromJsonAsync<JsonElement>();

        // The number that decides whether withdrawing the plan is safe. Deliberately omitted in M3.1,
        // when it could only have read zero.
        Assert.Equal(2, body.GetProperty("subscribedGymCount").GetInt32());

        // Cancelling drops the count: it counts *live* subscriptions.
        await CancelAsync(secondGym, immediately: true);
        using HttpResponseMessage after = await _client.GetAsync(
            new Uri($"/api/v1/plans/{planId}", UriKind.Relative));
        JsonElement afterBody = await after.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, afterBody.GetProperty("subscribedGymCount").GetInt32());
    }

    [Fact]
    public async Task AStaleIfMatchIsRefused()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "concurrent-sub-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(
            _client, "CONCURRENT", trialDays: 0, amount: 49.99m)).Id;
        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(
            _client, gymId, planId, "Monthly", "EUR");

        JsonElement subscription = await GetSubscriptionAsync(gymId);
        string version = subscription.GetProperty("rowVersion").GetString()!;

        using HttpResponseMessage first = await CancelAsync(gymId, immediately: false, rowVersion: version);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using HttpResponseMessage second = await _client.SendAsync(
            WithPrecondition(
                new HttpRequestMessage(HttpMethod.Post, SubscriptionUri(gymId, "resume")),
                version));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("concurrency.conflict", await AuthApi.ReadProblemCodeAsync(second));
    }

    [Fact]
    public async Task SubscriptionChangesAreAudited()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "audited-sub-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(
            _client, "AUDITEDSUB", trialDays: 0, amount: 49.99m)).Id;
        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(
            _client, gymId, planId, "Monthly", "EUR");
        await CancelAsync(gymId, immediately: true);

        using HttpResponseMessage response = await _client.GetAsync(
            new Uri("/api/v1/audit-logs?action=gym_subscription", UriKind.Relative));
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var actions = body.GetProperty("items").EnumerateArray()
            .Select(entry => entry.GetProperty("action").GetString())
            .ToList();

        // Who assigned it and who cancelled it are the two questions a billing dispute starts with.
        Assert.Contains("gym_subscription.created", actions);
        Assert.Contains("gym_subscription.updated", actions);
    }

    [Fact]
    public async Task BillingReadCanSeeSubscriptionsButNotChangeThem()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "scoped-sub-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(
            _client, "SCOPEDSUB", trialDays: 0, amount: 49.99m)).Id;
        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(
            _client, gymId, planId, "Monthly", "EUR");

        // BillingAdmin holds billing.* and subscriptions.manage; SupportAgent holds only billing.read.
        HttpClient supportAgent = await PlatformApiHelpers.SignedInAsync(
            _host, _client, "SupportAgent", "support.subs@gymos.test");

        using HttpResponseMessage read = await supportAgent.GetAsync(SubscriptionUri(gymId));
        using HttpResponseMessage write = await supportAgent.PostAsJsonAsync(
            SubscriptionUri(gymId, "cancel"), new { reason = "not mine to do", immediately = false });

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        supportAgent.Dispose();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static Uri SubscriptionUri(Guid gymId, string? action = null) =>
        new(
            action is null
                ? $"/api/v1/gyms/{gymId}/subscription"
                : $"/api/v1/gyms/{gymId}/subscription/{action}",
            UriKind.Relative);

    private static HttpRequestMessage WithPrecondition(HttpRequestMessage request, string rowVersion)
    {
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");
        return request;
    }

    /// <summary>
    /// Upserts a Monthly/EUR price with the plan's current <c>If-Match</c> — required, because the
    /// endpoint overwrites a price the caller read (an upsert, with no state machine to refuse a
    /// stale interleaving).
    /// </summary>
    private async Task<HttpResponseMessage> SetPriceAsync(Guid planId, decimal amount)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, new Uri($"/api/v1/plans/{planId}/prices", UriKind.Relative))
        {
            Content = JsonContent.Create(new { billingCycle = "Monthly", currency = "EUR", amount }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{await PlanRowVersionAsync(planId)}\"");

        return await _client.SendAsync(request);
    }

    private async Task<string> PlanRowVersionAsync(Guid planId)
    {
        using HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/api/v1/plans/{planId}", UriKind.Relative));
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.GetProperty("rowVersion").GetString()!;
    }

    private async Task<HttpResponseMessage> CancelAsync(
        Guid gymId,
        bool immediately,
        string? reason = null,
        string? rowVersion = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, SubscriptionUri(gymId, "cancel"))
        {
            Content = JsonContent.Create(new { reason, immediately }),
        };

        if (rowVersion is not null)
        {
            WithPrecondition(request, rowVersion);
        }

        HttpResponseMessage response = await _client.SendAsync(request);
        request.Dispose();
        return response;
    }

    private async Task<JsonElement> GetSubscriptionAsync(Guid gymId)
    {
        using HttpResponseMessage response = await _client.GetAsync(SubscriptionUri(gymId));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 reading subscription, got {(int)response.StatusCode}: " +
            await response.Content.ReadAsStringAsync());

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> GetHistoryAsync(Guid gymId)
    {
        using HttpResponseMessage response = await _client.GetAsync(SubscriptionUri(gymId, "history"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<Guid> FirstPriceIdAsync(Guid planId)
    {
        using HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/api/v1/plans/{planId}", UriKind.Relative));
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return body.GetProperty("prices").EnumerateArray().First().GetProperty("id").GetGuid();
    }

}
