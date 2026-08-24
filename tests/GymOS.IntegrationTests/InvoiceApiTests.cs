using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// Invoices over real HTTP against real SQL Server — the M3.3 endpoint surface, and the AD-39
/// idempotency framework's first end-to-end proof: the same <c>Idempotency-Key</c> replays the stored
/// response byte for byte, a reused key with a different body is refused, and a deterministic failure
/// replays as the same failure.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class InvoiceApiTests : IAsyncLifetime
{
    private static readonly Uri Invoices = new("/api/v1/invoices", UriKind.Relative);

    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public InvoiceApiTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        string connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("invoices_api");
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
    public async Task AssigningWithoutATrial_IssuesTheFirstPeriodInvoice()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "invoice-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(_client, "INVOICED", trialDays: 0, amount: 49.99m)).Id;
        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(_client, gymId, planId);

        JsonElement list = await GymInvoicesAsync(gymId);

        Assert.Equal(1, list.GetArrayLength());
        JsonElement item = list[0];
        Assert.Equal("Issued", item.GetProperty("status").GetString());
        Assert.Matches(@"^\d{4}-\d{6}$", item.GetProperty("invoiceNumber").GetString());
        Assert.Equal(49.99m, item.GetProperty("total").GetDecimal());
        Assert.Equal("EUR", item.GetProperty("currency").GetString());
        Assert.False(item.GetProperty("isOverdue").GetBoolean());

        JsonElement detail = await InvoiceDetailAsync(item.GetProperty("id").GetGuid());
        Assert.Equal(0m, detail.GetProperty("taxAmount").GetDecimal());
        Assert.Equal(49.99m, detail.GetProperty("subtotal").GetDecimal());
        Assert.Equal($"Gym invoice-gym", detail.GetProperty("gymName").GetString());
        Assert.Equal(1, detail.GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public async Task ATrialBillsNothingUntilItConverts()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "trial-invoice-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(_client, "TRIALINV", trialDays: 14, amount: 29.99m)).Id;
        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(_client, gymId, planId);

        Assert.Equal(0, (await GymInvoicesAsync(gymId)).GetArrayLength());

        using HttpResponseMessage converted = await _client.PostAsync(
            new Uri($"/api/v1/gyms/{gymId}/subscription/convert-trial", UriKind.Relative), content: null);
        Assert.Equal(HttpStatusCode.OK, converted.StatusCode);

        JsonElement list = await GymInvoicesAsync(gymId);
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal(29.99m, list[0].GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task UpgradingIssuesATwoLineProrationInvoice()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "upgrade-invoice-gym");
        Guid basic = (await PlatformApiHelpers.CreatePlanAsync(_client, "BASICINV", trialDays: 0, amount: 10m)).Id;
        Guid pro = (await PlatformApiHelpers.CreatePlanAsync(_client, "PROINV", trialDays: 0, amount: 20m)).Id;
        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(_client, gymId, basic);

        using HttpResponseMessage changed = await PlatformApiHelpers.AssignAsync(_client, gymId, pro);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        JsonElement list = await GymInvoicesAsync(gymId);
        Assert.Equal(2, list.GetArrayLength());

        // Newest first, so the proration invoice leads the list.
        JsonElement proration = await InvoiceDetailAsync(list[0].GetProperty("id").GetGuid());
        JsonElement lines = proration.GetProperty("lines");
        Assert.Equal(2, lines.GetArrayLength());
        Assert.Equal(1, lines.EnumerateArray().Count(line => line.GetProperty("amount").GetDecimal() < 0m));
        Assert.True(proration.GetProperty("total").GetDecimal() > 0m);
    }

    [Fact]
    public async Task DowngradingSchedulesInsteadOfBilling_AndAskingForTheCurrentPlanClearsIt()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "downgrade-invoice-gym");
        Guid pro = (await PlatformApiHelpers.CreatePlanAsync(_client, "PRODOWN", trialDays: 0, amount: 20m)).Id;
        Guid basic = (await PlatformApiHelpers.CreatePlanAsync(_client, "BASICDOWN", trialDays: 0, amount: 10m)).Id;
        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(_client, gymId, pro);

        using HttpResponseMessage downgraded = await PlatformApiHelpers.AssignAsync(_client, gymId, basic);
        Assert.Equal(HttpStatusCode.OK, downgraded.StatusCode);
        JsonElement body = await downgraded.Content.ReadFromJsonAsync<JsonElement>();

        // Today's plan is untouched; the change is visible, scheduled state.
        Assert.Equal("PRODOWN", body.GetProperty("planCode").GetString());
        JsonElement pending = body.GetProperty("pendingChange");
        Assert.Equal(basic, pending.GetProperty("planId").GetGuid());
        Assert.Equal(10m, pending.GetProperty("amount").GetDecimal());
        Assert.Equal(
            body.GetProperty("currentPeriodEndUtc").GetDateTimeOffset(),
            pending.GetProperty("changeAtUtc").GetDateTimeOffset());

        // No money moved.
        Assert.Equal(1, (await GymInvoicesAsync(gymId)).GetArrayLength());

        // Requesting the current plan again is the cancellation gesture.
        using HttpResponseMessage cleared = await PlatformApiHelpers.AssignAsync(_client, gymId, pro);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        JsonElement clearedBody = await cleared.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, clearedBody.GetProperty("pendingChange").ValueKind);
    }

    [Fact]
    public async Task TheListFiltersByStatusAndRefusesAnUnknownOne()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "list-invoice-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(_client, "LISTED", trialDays: 0, amount: 15m)).Id;
        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(_client, gymId, planId);

        JsonElement invoice = (await GymInvoicesAsync(gymId))[0];
        using HttpResponseMessage paid = await MarkPaidAsync(
            invoice.GetProperty("id").GetGuid(), "BANK-REF-1", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);

        using HttpResponseMessage paidList = await _client.GetAsync(
            new Uri("/api/v1/invoices?status=paid", UriKind.Relative));
        JsonElement paidBody = await paidList.Content.ReadFromJsonAsync<JsonElement>();
        Assert.All(
            paidBody.GetProperty("items").EnumerateArray(),
            item => Assert.Equal("Paid", item.GetProperty("status").GetString()));
        Assert.True(paidBody.GetProperty("totalCount").GetInt32() >= 1);

        // A fresh invoice is not overdue, so overdue=true excludes it and overdue=false includes it.
        using HttpResponseMessage notOverdue = await _client.GetAsync(
            new Uri("/api/v1/invoices?overdue=false", UriKind.Relative));
        JsonElement notOverdueBody = await notOverdue.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(notOverdueBody.GetProperty("totalCount").GetInt32() >= 1);

        using HttpResponseMessage overdue = await _client.GetAsync(
            new Uri("/api/v1/invoices?overdue=true", UriKind.Relative));
        JsonElement overdueBody = await overdue.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, overdueBody.GetProperty("totalCount").GetInt32());

        using HttpResponseMessage unknown = await _client.GetAsync(
            new Uri("/api/v1/invoices?status=bogus", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("invoices.status_invalid", await AuthApi.ReadProblemCodeAsync(unknown));
    }

    [Fact]
    public async Task MarkPaidIsIdempotentEndToEnd()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "idempotent-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(_client, "IDEMINV", trialDays: 0, amount: 49.99m)).Id;
        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(_client, gymId, planId);
        Guid invoiceId = (await GymInvoicesAsync(gymId))[0].GetProperty("id").GetGuid();
        string key = Guid.NewGuid().ToString();

        // 1. The key is required: this operation moves money.
        using HttpResponseMessage keyless = await MarkPaidAsync(invoiceId, "BANK-REF-42", key: null);
        Assert.Equal(HttpStatusCode.BadRequest, keyless.StatusCode);
        Assert.Equal("idempotency.key_required", await AuthApi.ReadProblemCodeAsync(keyless));

        // 2. First request wins.
        using HttpResponseMessage first = await MarkPaidAsync(invoiceId, "BANK-REF-42", key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        string firstBody = await first.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"Paid\"", firstBody, StringComparison.Ordinal);

        // 3. Same key + same body replays the stored response, byte for byte, and says so.
        using HttpResponseMessage replay = await MarkPaidAsync(invoiceId, "BANK-REF-42", key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(firstBody, await replay.Content.ReadAsStringAsync());
        Assert.True(replay.Headers.TryGetValues("Idempotency-Replayed", out var replayed));
        Assert.Equal("true", Assert.Single(replayed));

        // 4. Same key + a different body is a caller bug worth naming, not a replay.
        using HttpResponseMessage mismatch = await MarkPaidAsync(invoiceId, "DIFFERENT-REF", key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);
        Assert.Equal("idempotency.key_reuse_mismatch", await AuthApi.ReadProblemCodeAsync(mismatch));

        // 5. A NEW key on the now-paid invoice fails deterministically — and that failure is itself
        //    stored and replayed under its key.
        string secondKey = Guid.NewGuid().ToString();
        using HttpResponseMessage alreadyPaid = await MarkPaidAsync(invoiceId, "BANK-REF-43", secondKey);
        Assert.Equal(HttpStatusCode.Conflict, alreadyPaid.StatusCode);
        Assert.Equal("invoices.already_paid", await AuthApi.ReadProblemCodeAsync(alreadyPaid));

        using HttpResponseMessage replayedFailure = await MarkPaidAsync(invoiceId, "BANK-REF-43", secondKey);
        Assert.Equal(HttpStatusCode.Conflict, replayedFailure.StatusCode);
        Assert.Equal("invoices.already_paid", await AuthApi.ReadProblemCodeAsync(replayedFailure));
        Assert.True(replayedFailure.Headers.Contains("Idempotency-Replayed"));
    }

    [Fact]
    public async Task VoidingIsConfirmationGated()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "void-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(_client, "VOIDINV", trialDays: 0, amount: 25m)).Id;
        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(_client, gymId, planId);
        JsonElement invoice = (await GymInvoicesAsync(gymId))[0];
        Guid invoiceId = invoice.GetProperty("id").GetGuid();
        string number = invoice.GetProperty("invoiceNumber").GetString()!;

        using HttpResponseMessage wrong = await VoidAsync(invoiceId, "2026-999999");
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal("invoices.confirmation_mismatch", await AuthApi.ReadProblemCodeAsync(wrong));

        using HttpResponseMessage missing = await VoidAsync(invoiceId, confirm: null);
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);

        using HttpResponseMessage voided = await VoidAsync(invoiceId, number);
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        JsonElement body = await voided.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Void", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task APaidInvoiceCannotBeVoided()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "paid-void-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(_client, "PAIDVOID", trialDays: 0, amount: 25m)).Id;
        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(_client, gymId, planId);
        JsonElement invoice = (await GymInvoicesAsync(gymId))[0];
        Guid invoiceId = invoice.GetProperty("id").GetGuid();

        using HttpResponseMessage paid = await MarkPaidAsync(invoiceId, "REF-PAID", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);

        using HttpResponseMessage voided = await VoidAsync(
            invoiceId, invoice.GetProperty("invoiceNumber").GetString());
        Assert.Equal(HttpStatusCode.Conflict, voided.StatusCode);
        Assert.Equal("invoices.already_paid", await AuthApi.ReadProblemCodeAsync(voided));
    }

    [Fact]
    public async Task BillingReadCanSeeInvoicesButNotPayThem()
    {
        Guid gymId = await PlatformApiHelpers.CreateGymAsync(_client, "scoped-invoice-gym");
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(_client, "SCOPEDINV", trialDays: 0, amount: 25m)).Id;
        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(_client, gymId, planId);
        Guid invoiceId = (await GymInvoicesAsync(gymId))[0].GetProperty("id").GetGuid();

        HttpClient supportAgent = await PlatformApiHelpers.SignedInAsync(
            _host, _client, "SupportAgent", "support.invoices@gymos.test");

        using HttpResponseMessage read = await supportAgent.GetAsync(Invoices);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri($"/api/v1/invoices/{invoiceId}/mark-paid", UriKind.Relative))
        {
            Content = JsonContent.Create(new { paymentReference = "not mine to record" }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        using HttpResponseMessage write = await supportAgent.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        supportAgent.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private async Task<JsonElement> GymInvoicesAsync(Guid gymId)
    {
        using HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/api/v1/gyms/{gymId}/invoices", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> InvoiceDetailAsync(Guid invoiceId)
    {
        using HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/api/v1/invoices/{invoiceId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<HttpResponseMessage> MarkPaidAsync(
        Guid invoiceId,
        string? reference,
        string? key,
        string? rowVersion = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri($"/api/v1/invoices/{invoiceId}/mark-paid", UriKind.Relative))
        {
            Content = JsonContent.Create(new { paymentReference = reference }),
        };

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        if (rowVersion is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");
        }

        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> VoidAsync(Guid invoiceId, string? confirm)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri($"/api/v1/invoices/{invoiceId}/void", UriKind.Relative));

        if (confirm is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Confirm-Action", confirm);
        }

        return await _client.SendAsync(request);
    }

}
