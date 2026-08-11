using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// HTTP setup shared by the platform API's integration suites: provisioning a priced gym or plan,
/// assigning one to the other, and narrowing a seeded administrator down to one role to prove what that
/// role can and cannot do. Eight test classes were growing their own copy of this before it moved here —
/// the same handful of endpoints, called the same way, to build the same fixtures.
/// </summary>
internal static class PlatformApiHelpers
{
    /// <summary>Looks up a role's id by its name, e.g. <c>SystemRoles.SupportAgent</c>.</summary>
    public static async Task<Guid> RoleIdAsync(HttpClient client, string roleName)
    {
        using HttpResponseMessage response = await client.GetAsync(new Uri("/api/v1/roles", UriKind.Relative));
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.EnumerateArray()
            .Single(role => role.GetProperty("name").GetString() == roleName)
            .GetProperty("id").GetGuid();
    }

    /// <summary>Finds a user's id by an exact email match against the search endpoint.</summary>
    public static async Task<Guid> FindUserIdAsync(HttpClient client, string email)
    {
        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/users?search={Uri.EscapeDataString(email)}", UriKind.Relative));
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.GetProperty("items").EnumerateArray()
            .Single(user => user.GetProperty("email").GetString() == email)
            .GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Replaces a user's role assignments. Reads the current row version itself, because the endpoint
    /// requires <c>If-Match</c> and a caller that just created or found the user has not necessarily
    /// kept it.
    /// </summary>
    public static async Task SetRolesAsync(HttpClient client, Guid userId, params Guid[] roleIds)
    {
        using HttpResponseMessage current = await client.GetAsync(
            new Uri($"/api/v1/users/{userId}", UriKind.Relative));
        using JsonDocument currentBody = JsonDocument.Parse(await current.Content.ReadAsStringAsync());
        string rowVersion = currentBody.RootElement.GetProperty("rowVersion").GetString()!;

        using var request = new HttpRequestMessage(
            HttpMethod.Put, new Uri($"/api/v1/users/{userId}/roles", UriKind.Relative))
        {
            Content = JsonContent.Create(new { roleIds }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");

        using HttpResponseMessage response = await client.SendAsync(request);

        // Body included in the message: a helper that fails with a bare status code sends the reader
        // hunting through logs for the reason.
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 setting roles, got {(int)response.StatusCode}: " +
            await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Creates an administrator in the named role with a usable password and returns a client signed in
    /// as them. The password is set by seeding a second host whose SuperAdmin seed targets this
    /// address — the API deliberately offers no way for one administrator to set another's password,
    /// and a test must not invent one. <paramref name="adminClient"/> is the caller's already
    /// authenticated client, used to find the seeded user and narrow their role.
    /// </summary>
    public static async Task<HttpClient> SignedInAsync(
        AuthenticationHost host, HttpClient adminClient, string roleName, string email)
    {
        const string Password = "role-holder-integration-password";

        await using AuthenticationHost seedHost = AuthenticationHost.Create(
            host.ConnectionString,
            settings =>
            {
                settings["Seed:SuperAdmin:Email"] = email;
                settings["Seed:SuperAdmin:Password"] = Password;
            });

        using HttpClient seedClient = seedHost.CreateClient();
        using HttpResponseMessage ready = await seedClient.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        // Seeded as SuperAdmin, then narrowed to the role under test.
        Guid userId = await FindUserIdAsync(adminClient, email);
        await SetRolesAsync(adminClient, userId, await RoleIdAsync(adminClient, roleName));

        HttpClient client = host.CreateClient();
        using HttpResponseMessage signIn = await AuthApi.LoginAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        AuthApi.Authorize(client, await AuthApi.ReadAccessTokenAsync(signIn));

        return client;
    }

    /// <summary>Provisions a gym with fixed, uninteresting owner details — only the slug varies.</summary>
    public static async Task<Guid> CreateGymAsync(HttpClient client, string slug)
    {
        using HttpResponseMessage created = await client.PostAsJsonAsync(
            new Uri("/api/v1/gyms", UriKind.Relative),
            new
            {
                slug,
                name = $"Gym {slug}",
                ownerName = "Ada Owner",
                ownerEmail = "ada@acme.test",
                phone = (string?)null,
                countryCode = "CY",
                timeZoneId = "Asia/Nicosia",
                currency = "EUR",
            });

        Assert.True(
            created.StatusCode == HttpStatusCode.Created,
            $"Expected 201 creating gym {slug}, got {(int)created.StatusCode}: " +
            await created.Content.ReadAsStringAsync());

        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>The id and post-pricing row version of a plan created by <see cref="CreatePlanAsync"/>.</summary>
    public sealed record PlanCreated(Guid Id, string RowVersion);

    /// <summary>
    /// Creates a plan and prices it Monthly/EUR in one call — every caller needs a priced plan, and none
    /// needs to see the unpriced one in between. <paramref name="trialDays"/> and <paramref name="amount"/>
    /// default to the values most callers use unchanged; the row version returned is the plan's version
    /// after the price write, since setting a price advances it.
    /// </summary>
    public static async Task<PlanCreated> CreatePlanAsync(
        HttpClient client, string code, int trialDays = 0, decimal amount = 49.99m)
    {
        using HttpResponseMessage created = await client.PostAsJsonAsync(
            new Uri("/api/v1/plans", UriKind.Relative),
            new
            {
                code,
                name = $"Plan {code}",
                description = "Created by the integration suite",
                trialDays,
                maxMembers = (int?)null,
                maxTrainers = (int?)null,
                maxStaff = (int?)null,
                storageLimitMb = (long?)null,
                sortOrder = 1,
            });

        Assert.True(
            created.StatusCode == HttpStatusCode.Created,
            $"Expected 201 creating plan {code}, got {(int)created.StatusCode}: " +
            await created.Content.ReadAsStringAsync());

        Guid planId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using HttpResponseMessage plan = await client.GetAsync(
            new Uri($"/api/v1/plans/{planId}", UriKind.Relative));
        string rowVersion = (await plan.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("rowVersion").GetString()!;

        using var priceRequest = new HttpRequestMessage(
            HttpMethod.Put, new Uri($"/api/v1/plans/{planId}/prices", UriKind.Relative))
        {
            Content = JsonContent.Create(new { billingCycle = "Monthly", currency = "EUR", amount }),
        };
        priceRequest.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");

        using HttpResponseMessage priced = await client.SendAsync(priceRequest);
        Assert.Equal(HttpStatusCode.OK, priced.StatusCode);

        string pricedRowVersion = (await priced.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("rowVersion").GetString()!;

        return new PlanCreated(planId, pricedRowVersion);
    }

    /// <summary>
    /// Assigns a gym to a plan under the given terms (Monthly/EUR by default) and asserts the assignment
    /// succeeded — unless <paramref name="assertSuccess"/> is <see langword="false"/>, for the callers
    /// proving a refusal (an inactive plan, a deleted gym, terms the plan does not offer), which need the
    /// failure status to reach them rather than be swallowed here.
    /// </summary>
    public static async Task<HttpResponseMessage> AssignAsync(
        HttpClient client,
        Guid gymId,
        Guid planId,
        string billingCycle = "Monthly",
        string currency = "EUR",
        bool assertSuccess = true)
    {
        HttpResponseMessage response = await client.PutAsJsonAsync(
            new Uri($"/api/v1/gyms/{gymId}/subscription", UriKind.Relative),
            new { planId, billingCycle, currency });

        if (assertSuccess)
        {
            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"Expected 200 assigning plan, got {(int)response.StatusCode}: " +
                await response.Content.ReadAsStringAsync());
        }

        return response;
    }
}
