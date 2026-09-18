using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The gyms surface over real HTTP against real SQL Server: the lifecycle, the uniqueness the database
/// enforces, the confirmation that guards deletion, and what happens when the caller lacks the
/// permission — which is the case an endpoint test is actually for.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class GymApiTests : IAsyncLifetime
{
    private static readonly Uri Gyms = new("/api/v1/gyms", UriKind.Relative);

    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public GymApiTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        string connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("gyms");
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
        await _sqlServer.DropDatabaseAsync(_host.ConnectionString);
    }

    [Fact]
    public async Task ProvisioningAGym_Returns201WithTheResourceAndItsETag()
    {
        using HttpResponseMessage created = await _client.PostAsJsonAsync(Gyms, NewGym("acme-gym"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        JsonElement body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Guid gymId = body.GetProperty("id").GetGuid();

        Assert.Equal("acme-gym", body.GetProperty("slug").GetString());
        Assert.Equal(nameof(GymStatus.Provisioning), body.GetProperty("status").GetString());
        Assert.Equal($"/api/v1/gyms/{gymId}", created.Headers.Location!.ToString());

        // The ETag and the body's rowVersion are one value, so a client can use either.
        Assert.NotNull(created.Headers.ETag);
        Assert.Equal($"\"{body.GetProperty("rowVersion").GetString()}\"", created.Headers.ETag!.ToString());

        // Branding is present and empty rather than absent, so a client never has to special-case it.
        JsonElement branding = body.GetProperty("branding");
        Assert.Equal(JsonValueKind.Null, branding.GetProperty("logoUrl").ValueKind);
    }

    [Fact]
    public async Task Provisioning_Returns201WithoutFeatures_WhenTheCallerLacksFeaturesRead()
    {
        // The same finding the review made about GET /gyms/{id}, through a write instead: the 201's
        // body is a GymDetail too, and one entry can carry overrideReason. gyms.create without
        // features.read is not a stock combination today, but roles.manage will be able to create
        // one — the same reasoning the review already applied to the read route. IGymQueries.GetAsync
        // is the single point both the direct GET and this write's read-back share (M4.1 task 4), so
        // gating it there — rather than in each write handler — covers this route without anyone
        // having to remember it.
        const string RoleName = "Gym Creator (gyms.create only)";
        const string Email = "gyms-create-only@gymos.test";

        await using (AppDbContext context = _host.CreateDbContext())
        {
            Guid permissionId = await context.Set<Permission>()
                .Where(permission => permission.Key == PlatformPermissions.GymsCreate)
                .Select(permission => permission.Id)
                .SingleAsync();

            Role role = Role.Create(
                RoleName, "Integration test: gyms.create without features.read.", RoleScope.Platform);
            role.GrantPermission(permissionId);

            context.Add(role);
            await context.SaveChangesAsync();
        }

        using HttpClient limited = await PlatformApiHelpers.SignedInAsync(_host, _client, RoleName, Email);

        using HttpResponseMessage created = await limited.PostAsJsonAsync(
            Gyms, NewGym("gyms-create-only-gym"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        JsonElement body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("features", out JsonElement features) &&
                     features.ValueKind is not JsonValueKind.Null);

        // A SuperAdmin provisioning the same way still gets the whole block — this is a permission
        // gate, not a change to what provisioning returns.
        using HttpResponseMessage createdBySuperAdmin = await _client.PostAsJsonAsync(
            Gyms, NewGym("gyms-create-full-gym"));
        Assert.Equal(HttpStatusCode.Created, createdBySuperAdmin.StatusCode);

        JsonElement fullBody = await createdBySuperAdmin.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement fullFeatures = fullBody.GetProperty("features");
        Assert.NotEqual(JsonValueKind.Null, fullFeatures.ValueKind);
        Assert.True(fullFeatures.GetArrayLength() > 0);
    }

    [Fact]
    public async Task TheDatabaseRefusesADuplicateSlug()
    {
        await ProvisionAsync("acme-gym");

        using HttpResponseMessage second = await _client.PostAsJsonAsync(Gyms, NewGym("acme-gym"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("gyms.slug_taken", await AuthApi.ReadProblemCodeAsync(second));
    }

    [Fact]
    public async Task ADeletedGymKeepsItsSlugForever()
    {
        Guid gymId = await ProvisionAsync("acme-gym");
        await DeleteAsync(gymId, "acme-gym");

        using HttpResponseMessage reuse = await _client.PostAsJsonAsync(Gyms, NewGym("acme-gym"));

        // The slug may still be in a member's bookmark; handing it to a different gym would point
        // them at someone else's business.
        Assert.Equal(HttpStatusCode.Conflict, reuse.StatusCode);
        Assert.Equal("gyms.slug_taken", await AuthApi.ReadProblemCodeAsync(reuse));
    }

    [Fact]
    public async Task AnInvalidSlugIs400WithTheRuleCode()
    {
        using HttpResponseMessage response = await _client.PostAsJsonAsync(Gyms, NewGym("Acme Gym"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("gyms.slug_invalid", await AuthApi.ReadProblemCodeAsync(response));
    }

    [Fact]
    public async Task TheLifecycleRunsProvisioningToActiveToSuspendedAndBack()
    {
        Guid gymId = await ProvisionAsync("lifecycle-gym");

        using HttpResponseMessage activated = await PostActionAsync(gymId, "activate");
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        Assert.Equal(nameof(GymStatus.Active), await StatusOfAsync(activated));

        using HttpResponseMessage suspended = await _client.PostAsJsonAsync(
            new Uri($"/api/v1/gyms/{gymId}/suspend", UriKind.Relative),
            new { reason = "Non-payment, invoice 1042" });
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);

        JsonElement suspendedBody = await suspended.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(nameof(GymStatus.Suspended), suspendedBody.GetProperty("status").GetString());
        Assert.Equal("Non-payment, invoice 1042", suspendedBody.GetProperty("suspensionReason").GetString());

        using HttpResponseMessage reactivated = await PostActionAsync(gymId, "reactivate");
        Assert.Equal(HttpStatusCode.OK, reactivated.StatusCode);

        JsonElement reactivatedBody = await reactivated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(nameof(GymStatus.Active), reactivatedBody.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, reactivatedBody.GetProperty("suspensionReason").ValueKind);
    }

    [Fact]
    public async Task SuspendingWithoutAReasonIs400()
    {
        Guid gymId = await ProvisionAsync("no-reason-gym");

        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            new Uri($"/api/v1/gyms/{gymId}/suspend", UriKind.Relative),
            new { reason = "  " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("gyms.reason_required", await AuthApi.ReadProblemCodeAsync(response));
    }

    [Fact]
    public async Task ReactivatingAGymThatIsNotSuspendedIs409()
    {
        Guid gymId = await ProvisionAsync("not-suspended-gym");

        using HttpResponseMessage response = await PostActionAsync(gymId, "reactivate");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("gyms.not_suspended", await AuthApi.ReadProblemCodeAsync(response));
    }

    [Fact]
    public async Task UpdatingWithoutIfMatchIs428()
    {
        Guid gymId = await ProvisionAsync("precondition-gym");

        using var request = new HttpRequestMessage(
            HttpMethod.Put, new Uri($"/api/v1/gyms/{gymId}", UriKind.Relative))
        {
            Content = JsonContent.Create(UpdatedGym()),
        };

        using HttpResponseMessage response = await _client.SendAsync(request);

        // Proceeding without a version would be last-write-wins on a resource the caller read.
        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        Assert.Equal("concurrency.version_required", await AuthApi.ReadProblemCodeAsync(response));
    }

    [Fact]
    public async Task UpdatingWithAStaleIfMatchIs409()
    {
        Guid gymId = await ProvisionAsync("stale-gym");
        string firstVersion = await RowVersionAsync(gymId);

        using HttpResponseMessage first = await PutAsync(gymId, UpdatedGym(), firstVersion);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using HttpResponseMessage second = await PutAsync(gymId, UpdatedGym(), firstVersion);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("concurrency.conflict", await AuthApi.ReadProblemCodeAsync(second));
    }

    [Fact]
    public async Task AnUpdateReturnsTheNewVersionSoNoFollowUpReadIsNeeded()
    {
        Guid gymId = await ProvisionAsync("versioned-gym");
        string before = await RowVersionAsync(gymId);

        using HttpResponseMessage updated = await PutAsync(gymId, UpdatedGym(), before);
        JsonElement body = await updated.Content.ReadFromJsonAsync<JsonElement>();
        string after = body.GetProperty("rowVersion").GetString()!;

        Assert.NotEqual(before, after);
        Assert.Equal($"\"{after}\"", updated.Headers.ETag!.ToString());

        // And the returned version is immediately usable for the next edit.
        using HttpResponseMessage again = await PutAsync(gymId, UpdatedGym(), after);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task TheSlugCannotBeChanged()
    {
        Guid gymId = await ProvisionAsync("immutable-gym");
        string version = await RowVersionAsync(gymId);

        // The request body has no slug field at all; sending one is ignored rather than honoured.
        using HttpResponseMessage response = await PutAsync(
            gymId,
            new
            {
                slug = "renamed-gym",
                name = "Renamed",
                ownerName = "Ada Owner",
                ownerEmail = "ada@acme.test",
                phone = (string?)null,
                countryCode = "CY",
                timeZoneId = "Asia/Nicosia",
                currency = "EUR",
            },
            version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("immutable-gym", body.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task DeletingWithoutTheConfirmationHeaderIs400()
    {
        Guid gymId = await ProvisionAsync("guarded-gym");

        using HttpResponseMessage response = await _client.DeleteAsync(
            new Uri($"/api/v1/gyms/{gymId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("gyms.confirmation_required", await AuthApi.ReadProblemCodeAsync(response));

        // Still there.
        using HttpResponseMessage detail = await _client.GetAsync(
            new Uri($"/api/v1/gyms/{gymId}", UriKind.Relative));
        Assert.Equal(nameof(GymStatus.Provisioning), await StatusOfAsync(detail));
    }

    [Fact]
    public async Task DeletingWithTheWrongConfirmationIs400()
    {
        Guid gymId = await ProvisionAsync("guarded-gym");

        using var request = new HttpRequestMessage(
            HttpMethod.Delete, new Uri($"/api/v1/gyms/{gymId}", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("X-Confirm-Action", "some-other-gym");

        using HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("gyms.confirmation_mismatch", await AuthApi.ReadProblemCodeAsync(response));
    }

    [Fact]
    public async Task DeletingWithTheSlugIs204_AndTheGymStillReads()
    {
        Guid gymId = await ProvisionAsync("doomed-gym");

        using HttpResponseMessage deleted = await DeleteAsync(gymId, "doomed-gym");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // Soft delete: the row survives the retention window, and an operator investigating why a
        // tenant stopped working is told it was deleted rather than that it never existed.
        using HttpResponseMessage detail = await _client.GetAsync(
            new Uri($"/api/v1/gyms/{gymId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);

        JsonElement body = await detail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(nameof(GymStatus.Deleted), body.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("deletedAtUtc").ValueKind);
    }

    [Fact]
    public async Task RepeatingADeleteIsStill204()
    {
        Guid gymId = await ProvisionAsync("doomed-gym");
        await DeleteAsync(gymId, "doomed-gym");

        using HttpResponseMessage again = await DeleteAsync(gymId, "doomed-gym");

        // An operator who deleted a gym and retried should be told it is done, not shown a failure
        // for having succeeded.
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    [Fact]
    public async Task ADeletedGymCannotBeEditedOrRevived()
    {
        Guid gymId = await ProvisionAsync("gone-gym");
        string version = await RowVersionAsync(gymId);
        await DeleteAsync(gymId, "gone-gym");

        using HttpResponseMessage update = await PutAsync(gymId, UpdatedGym(), version);
        using HttpResponseMessage activate = await PostActionAsync(gymId, "activate");

        Assert.Equal(HttpStatusCode.Conflict, update.StatusCode);
        Assert.Equal("gyms.deleted", await AuthApi.ReadProblemCodeAsync(update));
        Assert.Equal(HttpStatusCode.Conflict, activate.StatusCode);
    }

    [Fact]
    public async Task ListingExcludesDeletedGymsUnlessAskedFor()
    {
        Guid live = await ProvisionAsync("live-gym");
        Guid gone = await ProvisionAsync("gone-gym");
        await DeleteAsync(gone, "gone-gym");

        using HttpResponseMessage defaultList = await _client.GetAsync(Gyms);
        using HttpResponseMessage withDeleted = await _client.GetAsync(
            new Uri("/api/v1/gyms?includeDeleted=true", UriKind.Relative));

        Assert.Equal([live], await IdsAsync(defaultList));
        Assert.Equal(2, (await IdsAsync(withDeleted)).Count);
    }

    [Fact]
    public async Task ListingFiltersBySearchStatusAndCountry()
    {
        Guid nicosia = await ProvisionAsync("nicosia-gym");
        await ProvisionAsync("athens-gym", countryCode: "GR", timeZoneId: "Europe/Athens");
        await PostActionAsync(nicosia, "activate");

        using HttpResponseMessage bySearch = await _client.GetAsync(
            new Uri("/api/v1/gyms?search=nicosia", UriKind.Relative));
        using HttpResponseMessage byStatus = await _client.GetAsync(
            new Uri($"/api/v1/gyms?status={nameof(GymStatus.Active)}", UriKind.Relative));
        using HttpResponseMessage byCountry = await _client.GetAsync(
            new Uri("/api/v1/gyms?country=GR", UriKind.Relative));

        Assert.Equal([nicosia], await IdsAsync(bySearch));
        Assert.Equal([nicosia], await IdsAsync(byStatus));
        Assert.Equal("athens-gym", await OnlySlugAsync(byCountry));
    }

    [Fact]
    public async Task AnUnparseableStatusFilterIsAnEmptyList_NotAnError()
    {
        await ProvisionAsync("acme-gym");

        using HttpResponseMessage response = await _client.GetAsync(
            new Uri("/api/v1/gyms?status=Bananas", UriKind.Relative));

        // A stale filter chip in a bookmarked URL should show an empty list, not a 400.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await IdsAsync(response));
    }

    [Fact]
    public async Task BrandingIsReplacedWholesaleAndAdvancesTheGymsVersion()
    {
        Guid gymId = await ProvisionAsync("branded-gym");
        string version = await RowVersionAsync(gymId);

        using HttpResponseMessage first = await PutBrandingAsync(
            gymId,
            new
            {
                logoUrl = "https://cdn.gymos.test/logo.png",
                faviconUrl = (string?)null,
                primaryColour = "#1A2B3C",
                secondaryColour = "#FFFFFF",
                customDomain = "acme.test",
                emailSenderName = "Acme Gym",
            },
            version);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        JsonElement body = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("#1A2B3C", body.GetProperty("branding").GetProperty("primaryColour").GetString());
        Assert.Equal("acme.test", body.GetProperty("branding").GetProperty("customDomain").GetString());

        // Branding lives in its own table, so the gym's version only moves because the handler says
        // so — without that, two concurrent branding edits would both succeed.
        string after = body.GetProperty("rowVersion").GetString()!;
        Assert.NotEqual(version, after);

        // The stale version is now refused.
        using HttpResponseMessage stale = await PutBrandingAsync(
            gymId,
            new
            {
                logoUrl = (string?)null,
                faviconUrl = (string?)null,
                primaryColour = "#000000",
                secondaryColour = (string?)null,
                customDomain = (string?)null,
                emailSenderName = (string?)null,
            },
            version);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task TwoGymsCannotClaimTheSameCustomDomain()
    {
        Guid incumbent = await ProvisionAsync("incumbent-gym");
        Guid challenger = await ProvisionAsync("challenger-gym");

        using HttpResponseMessage claimed = await PutBrandingAsync(
            incumbent, BrandingWithDomain("acme.test"), await RowVersionAsync(incumbent));
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);

        using HttpResponseMessage contested = await PutBrandingAsync(
            challenger, BrandingWithDomain("ACME.test"), await RowVersionAsync(challenger));

        // A domain must resolve to exactly one gym, and hostnames are case-insensitive.
        Assert.Equal(HttpStatusCode.Conflict, contested.StatusCode);
        Assert.Equal("gyms.custom_domain_taken", await AuthApi.ReadProblemCodeAsync(contested));
    }

    [Fact]
    public async Task GymChangesAppearInTheAuditTrail()
    {
        Guid gymId = await ProvisionAsync("audited-gym");

        using HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/api/v1/audit-logs?entityId={gymId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var actions = body.GetProperty("items").EnumerateArray()
            .Select(entry => entry.GetProperty("action").GetString())
            .ToList();

        // Who provisioned a tenant is the question a support conversation or a billing dispute turns on.
        Assert.Contains("gym.created", actions);
    }

    [Fact]
    public async Task ASupportAgentCanReadGymsButNotChangeThem()
    {
        Guid gymId = await ProvisionAsync("read-only-gym");
        HttpClient supportAgent = await PlatformApiHelpers.SignedInAsync(
            _host, _client, SystemRoles.SupportAgent, "support.gyms@gymos.test");

        using HttpResponseMessage read = await supportAgent.GetAsync(Gyms);
        using HttpResponseMessage create = await supportAgent.PostAsJsonAsync(Gyms, NewGym("sneaky-gym"));
        using HttpResponseMessage suspend = await supportAgent.PostAsJsonAsync(
            new Uri($"/api/v1/gyms/{gymId}/suspend", UriKind.Relative),
            new { reason = "Not my call to make" });

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, suspend.StatusCode);

        supportAgent.Dispose();
    }

    [Fact]
    public async Task APlatformAdminCanSuspendAGymButNotDeleteOne()
    {
        Guid gymId = await ProvisionAsync("admin-scope-gym");
        HttpClient platformAdmin = await PlatformApiHelpers.SignedInAsync(
            _host, _client, SystemRoles.PlatformAdmin, "admin.gyms@gymos.test");

        using HttpResponseMessage suspend = await platformAdmin.PostAsJsonAsync(
            new Uri($"/api/v1/gyms/{gymId}/suspend", UriKind.Relative),
            new { reason = "Non-payment" });

        using var deleteRequest = new HttpRequestMessage(
            HttpMethod.Delete, new Uri($"/api/v1/gyms/{gymId}", UriKind.Relative));
        deleteRequest.Headers.TryAddWithoutValidation("X-Confirm-Action", "admin-scope-gym");
        using HttpResponseMessage delete = await platformAdmin.SendAsync(deleteRequest);

        // Suspension is reversible; deletion is not. Removing a tenant is deliberately reserved.
        Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);

        platformAdmin.Dispose();
    }

    [Fact]
    public async Task AnAnonymousCallerGets401()
    {
        using HttpClient anonymous = _host.CreateClient();

        using HttpResponseMessage response = await anonymous.GetAsync(Gyms);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static object NewGym(
        string slug,
        string countryCode = "CY",
        string timeZoneId = "Asia/Nicosia") => new
        {
            slug,
            name = "Acme Gym",
            ownerName = "Ada Owner",
            ownerEmail = "ada@acme.test",
            phone = "+357 99 123456",
            countryCode,
            timeZoneId,
            currency = "EUR",
        };

    private static object UpdatedGym() => new
    {
        name = "Acme Fitness",
        ownerName = "Ada Owner",
        ownerEmail = "ada@acme.test",
        phone = (string?)null,
        countryCode = "CY",
        timeZoneId = "Asia/Nicosia",
        currency = "EUR",
    };

    private static object BrandingWithDomain(string customDomain) => new
    {
        logoUrl = (string?)null,
        faviconUrl = (string?)null,
        primaryColour = (string?)null,
        secondaryColour = (string?)null,
        customDomain,
        emailSenderName = (string?)null,
    };

    private async Task<Guid> ProvisionAsync(
        string slug,
        string countryCode = "CY",
        string timeZoneId = "Asia/Nicosia")
    {
        using HttpResponseMessage created = await _client.PostAsJsonAsync(
            Gyms, NewGym(slug, countryCode, timeZoneId));

        Assert.True(
            created.StatusCode == HttpStatusCode.Created,
            $"Expected 201 provisioning {slug}, got {(int)created.StatusCode}: " +
            await created.Content.ReadAsStringAsync());

        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> PostActionAsync(Guid gymId, string action) =>
        _client.PostAsync(new Uri($"/api/v1/gyms/{gymId}/{action}", UriKind.Relative), content: null);

    private async Task<HttpResponseMessage> PutAsync(Guid gymId, object payload, string rowVersion)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, new Uri($"/api/v1/gyms/{gymId}", UriKind.Relative))
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");

        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PutBrandingAsync(Guid gymId, object payload, string rowVersion)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, new Uri($"/api/v1/gyms/{gymId}/branding", UriKind.Relative))
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");

        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> DeleteAsync(Guid gymId, string slug)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, new Uri($"/api/v1/gyms/{gymId}", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("X-Confirm-Action", slug);

        return await _client.SendAsync(request);
    }

    private async Task<string> RowVersionAsync(Guid gymId)
    {
        using HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/api/v1/gyms/{gymId}", UriKind.Relative));
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.GetProperty("rowVersion").GetString()!;
    }

    private static async Task<string?> StatusOfAsync(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("status").GetString();
    }

    private static async Task<List<Guid>> IdsAsync(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .ToList();
    }

    private static async Task<string?> OnlySlugAsync(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.GetProperty("items").EnumerateArray()
            .Single()
            .GetProperty("slug").GetString();
    }

}
