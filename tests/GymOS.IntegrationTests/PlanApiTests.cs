using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GymOS.Platform.Domain.Identity;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The plan catalogue over real HTTP against real PostgreSQL — including the two things only a real
/// database can prove: that <c>Money</c> round-trips through its column pair, and that the
/// (plan, cycle, currency) unique index created as raw SQL in the migration actually exists.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PlanApiTests : IAsyncLifetime
{
    private static readonly Uri Plans = new("/api/v1/plans", UriKind.Relative);
    private static readonly Uri Roles = new("/api/v1/roles", UriKind.Relative);

    private readonly PostgresFixture _postgres;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public PlanApiTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        string connectionString = await _postgres.CreateIsolatedDatabaseAsync("plans");
        _host = AuthenticationHost.Create(connectionString);
        _client = _host.CreateClient();

        using HttpResponseMessage ready = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        using HttpResponseMessage signIn = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        AuthApi.Authorize(_client, await AuthApi.ReadAccessTokenAsync(signIn));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task CreatingAPlan_Returns201WithTheResourceAndItsETag()
    {
        using HttpResponseMessage created = await _client.PostAsJsonAsync(Plans, NewPlan("STARTER"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        JsonElement body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Guid planId = body.GetProperty("id").GetGuid();

        Assert.Equal("STARTER", body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("isActive").GetBoolean());
        Assert.Empty(body.GetProperty("prices").EnumerateArray());
        Assert.Equal($"/api/v1/plans/{planId}", created.Headers.Location!.ToString());
        Assert.Equal($"\"{body.GetProperty("rowVersion").GetString()}\"", created.Headers.ETag!.ToString());
    }

    [Fact]
    public async Task TheDatabaseRefusesADuplicateCode()
    {
        await CreateAsync("STARTER");

        using HttpResponseMessage second = await _client.PostAsJsonAsync(Plans, NewPlan("starter"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("plans.code_taken", await AuthApi.ReadProblemCodeAsync(second));
    }

    [Fact]
    public async Task AMoneyPriceRoundTripsThroughItsColumnPair()
    {
        Guid planId = await CreateAsync("PRICED");

        using HttpResponseMessage set = await SetPriceAsync(planId, "Yearly", "EUR", 499.99m);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        // Read back through a fresh request, so the value came from PostgreSQL rather than from the
        // aggregate still in memory.
        using HttpResponseMessage detail = await _client.GetAsync(PlanUri(planId));
        JsonElement body = await detail.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement price = body.GetProperty("prices").EnumerateArray().Single();

        Assert.Equal("Yearly", price.GetProperty("billingCycle").GetString());
        Assert.Equal(12, price.GetProperty("billingCycleMonths").GetInt32());
        Assert.Equal("EUR", price.GetProperty("currency").GetString());
        Assert.Equal(499.99m, price.GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task APriceIsRoundedToTheCurrencysMinorUnitsBeforeItIsStored()
    {
        Guid planId = await CreateAsync("ROUNDED");

        await SetPriceAsync(planId, "Monthly", "EUR", 49.999m);
        await SetPriceAsync(planId, "Monthly", "JPY", 4999.6m);

        using HttpResponseMessage detail = await _client.GetAsync(PlanUri(planId));
        JsonElement body = await detail.Content.ReadFromJsonAsync<JsonElement>();

        var amounts = body.GetProperty("prices").EnumerateArray()
            .ToDictionary(
                price => price.GetProperty("currency").GetString()!,
                price => price.GetProperty("amount").GetDecimal());

        Assert.Equal(50.00m, amounts["EUR"]);

        // JPY has no minor unit; storing 4999.6 or rounding it to two places would both be wrong.
        Assert.Equal(5000m, amounts["JPY"]);
    }

    [Fact]
    public async Task SettingTheSameCycleAndCurrencyTwice_ChangesTheOnePrice()
    {
        Guid planId = await CreateAsync("UPSERT");

        await SetPriceAsync(planId, "Monthly", "EUR", 49.99m);
        await SetPriceAsync(planId, "Monthly", "EUR", 59.99m);

        using HttpResponseMessage detail = await _client.GetAsync(PlanUri(planId));
        JsonElement body = await detail.Content.ReadFromJsonAsync<JsonElement>();

        // One row, changed — the unique index would have refused a second insert, which is the point.
        JsonElement price = body.GetProperty("prices").EnumerateArray().Single();
        Assert.Equal(59.99m, price.GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task OneCycleCanCarryPricesInSeveralCurrencies()
    {
        Guid planId = await CreateAsync("MULTICUR");

        await SetPriceAsync(planId, "Monthly", "EUR", 49.99m);
        await SetPriceAsync(planId, "Monthly", "GBP", 44.99m);
        await SetPriceAsync(planId, "Yearly", "EUR", 499.99m);

        using HttpResponseMessage detail = await _client.GetAsync(PlanUri(planId));
        JsonElement body = await detail.Content.ReadFromJsonAsync<JsonElement>();

        // Prices are rows, not columns: three offers, no schema change.
        Assert.Equal(3, body.GetProperty("prices").GetArrayLength());

        // Ordered by period length, so the list reads monthly before yearly.
        var cycles = body.GetProperty("prices").EnumerateArray()
            .Select(price => price.GetProperty("billingCycleMonths").GetInt32())
            .ToList();
        Assert.Equal(cycles.OrderBy(months => months), cycles);
    }

    [Fact]
    public async Task ThePlanCycleCurrencyUniqueIndexExists()
    {
        // Created as raw SQL in the migration because EF cannot index inside a complex property. If that
        // statement were ever dropped from the migration, nothing else in the suite would notice.
        await using var connection = new Npgsql.NpgsqlConnection(_host.ConnectionString);
        await connection.OpenAsync();

        await using Npgsql.NpgsqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT indexdef
            FROM pg_indexes
            WHERE schemaname = 'platform'
              AND tablename = 'plan_prices'
              AND indexname = 'ix_plan_prices_plan_cycle_currency';
            """;

        object? definition = await command.ExecuteScalarAsync();

        Assert.NotNull(definition);
        string sql = Assert.IsType<string>(definition);
        Assert.Contains("UNIQUE", sql, StringComparison.Ordinal);
        Assert.Contains("price_currency", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovingAPriceReturnsThePlanWithoutIt()
    {
        Guid planId = await CreateAsync("REMOVABLE");
        await SetPriceAsync(planId, "Monthly", "EUR", 49.99m);

        using HttpResponseMessage detail = await _client.GetAsync(PlanUri(planId));
        JsonElement body = await detail.Content.ReadFromJsonAsync<JsonElement>();
        Guid priceId = body.GetProperty("prices").EnumerateArray().Single().GetProperty("id").GetGuid();

        // If-Match required, like the upsert: withdrawing a price the caller may not have seen
        // repriced is the same lost-update hazard from the other direction.
        using var removeRequest = new HttpRequestMessage(
            HttpMethod.Delete, new Uri($"/api/v1/plans/{planId}/prices/{priceId}", UriKind.Relative));
        removeRequest.Headers.TryAddWithoutValidation(
            "If-Match", $"\"{await RowVersionAsync(planId)}\"");

        using HttpResponseMessage removed = await _client.SendAsync(removeRequest);

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        JsonElement after = await removed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(after.GetProperty("prices").EnumerateArray());
    }

    [Fact]
    public async Task UpdatingWithoutIfMatchIs428_AndWithAStaleVersionIs409()
    {
        Guid planId = await CreateAsync("VERSIONED");
        string version = await RowVersionAsync(planId);

        using var withoutPrecondition = new HttpRequestMessage(HttpMethod.Put, PlanUri(planId))
        {
            Content = JsonContent.Create(UpdatedPlan()),
        };
        using HttpResponseMessage missing = await _client.SendAsync(withoutPrecondition);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal("concurrency.version_required", await AuthApi.ReadProblemCodeAsync(missing));

        using HttpResponseMessage first = await PutAsync(planId, UpdatedPlan(), version);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using HttpResponseMessage second = await PutAsync(planId, UpdatedPlan(), version);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("concurrency.conflict", await AuthApi.ReadProblemCodeAsync(second));
    }

    [Fact]
    public async Task APriceChangeAdvancesThePlansVersion()
    {
        Guid planId = await CreateAsync("VERSIONBUMP");
        string before = await RowVersionAsync(planId);

        using HttpResponseMessage set = await SetPriceAsync(planId, "Monthly", "EUR", 49.99m);
        JsonElement body = await set.Content.ReadFromJsonAsync<JsonElement>();

        // Prices live in their own table, so the plan's version only moves because the handler says so.
        // Without that, two operators editing prices concurrently would both succeed.
        Assert.NotEqual(before, body.GetProperty("rowVersion").GetString());
    }

    [Fact]
    public async Task DeactivatingHidesThePlanFromTheCatalogueButKeepsIt()
    {
        Guid planId = await CreateAsync("WITHDRAWN");
        await SetPriceAsync(planId, "Monthly", "EUR", 49.99m);

        using HttpResponseMessage deactivated = await _client.PostAsync(
            new Uri($"/api/v1/plans/{planId}/deactivate", UriKind.Relative), content: null);
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);

        using HttpResponseMessage defaultList = await _client.GetAsync(Plans);
        Assert.DoesNotContain(planId, await IdsAsync(defaultList));

        using HttpResponseMessage withInactive = await _client.GetAsync(
            new Uri("/api/v1/plans?includeInactive=true", UriKind.Relative));
        Assert.Contains(planId, await IdsAsync(withInactive));

        // Still readable, still priced: gyms on it keep billing (§2.3).
        using HttpResponseMessage detail = await _client.GetAsync(PlanUri(planId));
        JsonElement body = await detail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("isActive").GetBoolean());
        Assert.Single(body.GetProperty("prices").EnumerateArray());
    }

    [Fact]
    public async Task ThereIsNoWayToDeleteAPlan()
    {
        Guid planId = await CreateAsync("PERMANENT");

        // §3.4: "delete = deactivate only, never destroys history". Asserted as a property of the API
        // surface, not just of intent — subscriptions and invoices will point here.
        using HttpResponseMessage delete = await _client.DeleteAsync(PlanUri(planId));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
    }

    [Fact]
    public async Task PlanChangesAppearInTheAuditTrail()
    {
        Guid planId = await CreateAsync("AUDITED");
        await SetPriceAsync(planId, "Monthly", "EUR", 49.99m);

        // Two queries, because `action` matches by prefix and the two entity names do not share one:
        // the plan audits as `subscription_plan.*` and its prices as `plan_price.*`.
        Assert.Contains("subscription_plan.created", await AuditActionsAsync("subscription_plan"));

        // A price edit is the entry a billing dispute turns on.
        Assert.Contains("plan_price.created", await AuditActionsAsync("plan_price"));
    }

    [Fact]
    public async Task ChangingAPriceIsAudited_WithTheAmountBeforeAndAfter()
    {
        // The case that was silently broken: a change confined to a `Money` complex property left
        // EntityEntry.Properties empty, so the interceptor saw "nothing but a timestamp" and dropped the
        // entry. A repriced plan with no record of who repriced it is the worst possible gap in a
        // billing audit trail, so it is pinned here rather than trusted.
        Guid planId = await CreateAsync("REPRICED");
        await SetPriceAsync(planId, "Monthly", "EUR", 50.00m);
        await SetPriceAsync(planId, "Monthly", "EUR", 59.99m);

        using HttpResponseMessage response = await _client.GetAsync(
            new Uri("/api/v1/audit-logs?action=plan_price.updated", UriKind.Relative));
        JsonElement list = await response.Content.ReadFromJsonAsync<JsonElement>();

        JsonElement entry = list.GetProperty("items").EnumerateArray().First();

        using HttpResponseMessage detail = await _client.GetAsync(
            new Uri($"/api/v1/audit-logs/{entry.GetProperty("id").GetGuid()}", UriKind.Relative));
        string body = await detail.Content.ReadAsStringAsync();

        // Named by path (Price.Amount), so an invoice's Net.Amount and Tax.Amount stay distinguishable.
        Assert.Contains("Price.Amount", body, StringComparison.Ordinal);
        Assert.Contains("50.00", body, StringComparison.Ordinal);
        Assert.Contains("59.99", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABillingAdminCanManagePlansAndASupportAgentCannot()
    {
        Guid planId = await CreateAsync("SCOPED");

        HttpClient billingAdmin = await SignedInAsync(SystemRoles.BillingAdmin, "billing.plans@gymos.test");
        HttpClient supportAgent = await SignedInAsync(SystemRoles.SupportAgent, "support.plans@gymos.test");

        using HttpResponseMessage adminRead = await billingAdmin.GetAsync(Plans);
        using HttpResponseMessage adminWrite = await billingAdmin.PostAsJsonAsync(Plans, NewPlan("BILLING_OK"));
        using HttpResponseMessage agentRead = await supportAgent.GetAsync(Plans);
        using HttpResponseMessage agentWrite = await supportAgent.PostAsJsonAsync(Plans, NewPlan("AGENT_NO"));
        using HttpResponseMessage agentDeactivate = await supportAgent.PostAsync(
            new Uri($"/api/v1/plans/{planId}/deactivate", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.OK, adminRead.StatusCode);
        Assert.Equal(HttpStatusCode.Created, adminWrite.StatusCode);

        // A support agent answering "what am I paying for?" can read the catalogue but not reprice it.
        Assert.Equal(HttpStatusCode.OK, agentRead.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, agentWrite.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, agentDeactivate.StatusCode);

        billingAdmin.Dispose();
        supportAgent.Dispose();
    }

    [Fact]
    public async Task AnAnonymousCallerGets401()
    {
        using HttpClient anonymous = _host.CreateClient();

        using HttpResponseMessage response = await anonymous.GetAsync(Plans);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static Uri PlanUri(Guid planId) => new($"/api/v1/plans/{planId}", UriKind.Relative);

    private static object NewPlan(string code) => new
    {
        code,
        name = $"Plan {code}",
        description = "Created by the integration suite",
        trialDays = 14,
        maxMembers = 100,
        maxTrainers = 3,
        maxStaff = 5,
        storageLimitMb = 1_024,
        sortOrder = 1,
    };

    private static object UpdatedPlan() => new
    {
        name = "Renamed plan",
        description = "Updated",
        trialDays = 30,
        maxMembers = (int?)null,
        maxTrainers = (int?)null,
        maxStaff = (int?)null,
        storageLimitMb = (long?)null,
        sortOrder = 2,
        isPublic = false,
    };

    private async Task<Guid> CreateAsync(string code)
    {
        using HttpResponseMessage created = await _client.PostAsJsonAsync(Plans, NewPlan(code));

        Assert.True(
            created.StatusCode == HttpStatusCode.Created,
            $"Expected 201 creating {code}, got {(int)created.StatusCode}: " +
            await created.Content.ReadAsStringAsync());

        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Upserts a price, sending the plan's current <c>If-Match</c> — the endpoint requires it,
    /// because it overwrites a price the caller read and <c>SetPrice</c> is an upsert with no
    /// state machine to refuse a stale interleaving.
    /// </summary>
    private async Task<HttpResponseMessage> SetPriceAsync(
        Guid planId,
        string billingCycle,
        string currency,
        decimal amount)
    {
        string rowVersion = await RowVersionAsync(planId);

        using var request = new HttpRequestMessage(
            HttpMethod.Put, new Uri($"/api/v1/plans/{planId}/prices", UriKind.Relative))
        {
            Content = JsonContent.Create(new { billingCycle, currency, amount }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");

        HttpResponseMessage response = await _client.SendAsync(request);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 setting {billingCycle}/{currency}, got {(int)response.StatusCode}: " +
            await response.Content.ReadAsStringAsync());

        return response;
    }

    private async Task<HttpResponseMessage> PutAsync(Guid planId, object payload, string rowVersion)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, PlanUri(planId))
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");

        return await _client.SendAsync(request);
    }

    private async Task<string> RowVersionAsync(Guid planId)
    {
        using HttpResponseMessage response = await _client.GetAsync(PlanUri(planId));
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.GetProperty("rowVersion").GetString()!;
    }

    private async Task<List<string?>> AuditActionsAsync(string actionPrefix)
    {
        using HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/api/v1/audit-logs?action={actionPrefix}", UriKind.Relative));
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.GetProperty("items").EnumerateArray()
            .Select(entry => entry.GetProperty("action").GetString())
            .ToList();
    }

    private static async Task<List<Guid>> IdsAsync(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .ToList();
    }

    /// <summary>
    /// An administrator in the named role with a usable password, signed in — the same approach as
    /// <c>PlatformAdminApiTests</c>, because the API deliberately offers no way for one administrator to
    /// set another's password.
    /// </summary>
    private async Task<HttpClient> SignedInAsync(string roleName, string email)
    {
        const string Password = "role-holder-integration-password";

        await using AuthenticationHost seedHost = AuthenticationHost.Create(
            _host.ConnectionString,
            settings =>
            {
                settings["Seed:SuperAdmin:Email"] = email;
                settings["Seed:SuperAdmin:Password"] = Password;
            });

        using HttpClient seedClient = seedHost.CreateClient();
        using HttpResponseMessage ready = await seedClient.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        Guid userId = await FindUserIdAsync(email);
        await SetRolesAsync(userId, await RoleIdAsync(roleName));

        HttpClient client = _host.CreateClient();
        using HttpResponseMessage signIn = await AuthApi.LoginAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        AuthApi.Authorize(client, await AuthApi.ReadAccessTokenAsync(signIn));

        return client;
    }

    private async Task<Guid> RoleIdAsync(string roleName)
    {
        using HttpResponseMessage response = await _client.GetAsync(Roles);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.EnumerateArray()
            .Single(role => role.GetProperty("name").GetString() == roleName)
            .GetProperty("id").GetGuid();
    }

    private async Task<Guid> FindUserIdAsync(string email)
    {
        using HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/api/v1/users?search={Uri.EscapeDataString(email)}", UriKind.Relative));
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.GetProperty("items").EnumerateArray()
            .Single(user => user.GetProperty("email").GetString() == email)
            .GetProperty("id").GetGuid();
    }

    private async Task SetRolesAsync(Guid userId, params Guid[] roleIds)
    {
        using HttpResponseMessage current = await _client.GetAsync(
            new Uri($"/api/v1/users/{userId}", UriKind.Relative));
        using JsonDocument currentBody = JsonDocument.Parse(await current.Content.ReadAsStringAsync());
        string rowVersion = currentBody.RootElement.GetProperty("rowVersion").GetString()!;

        using var request = new HttpRequestMessage(
            HttpMethod.Put, new Uri($"/api/v1/users/{userId}/roles", UriKind.Relative))
        {
            Content = JsonContent.Create(new { roleIds }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");

        using HttpResponseMessage response = await _client.SendAsync(request);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 setting roles, got {(int)response.StatusCode}: " +
            await response.Content.ReadAsStringAsync());
    }
}
