using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GymOS.Platform.Domain.Identity;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The users, roles, and audit surfaces over real HTTP against real PostgreSQL — including
/// what happens when the caller lacks the permission, which is the case an endpoint test is
/// actually for.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PlatformAdminApiTests : IAsyncLifetime
{
    private static readonly Uri Users = new("/api/v1/users", UriKind.Relative);
    private static readonly Uri Roles = new("/api/v1/roles", UriKind.Relative);
    private static readonly Uri AuditLogs = new("/api/v1/audit-logs", UriKind.Relative);

    private readonly PostgresFixture _postgres;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public PlatformAdminApiTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        string connectionString = await _postgres.CreateIsolatedDatabaseAsync("platform_admin");
        _host = AuthenticationHost.Create(connectionString);
        _client = _host.CreateClient();

        using HttpResponseMessage ready = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        await SignInAsSuperAdminAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task TheSeededSuperAdminCanListAdministrators()
    {
        using HttpResponseMessage response = await _client.GetAsync(Users);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(1, body.RootElement.GetProperty("totalCount").GetInt32());
        JsonElement onlyUser = body.RootElement.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(AuthenticationHost.SuperAdminEmail, onlyUser.GetProperty("email").GetString());
        Assert.Equal(nameof(UserStatus.Active), onlyUser.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PagingIsCappedServerSide()
    {
        using HttpResponseMessage response = await _client.GetAsync(
            new Uri("/api/v1/users?page=0&pageSize=100000", UriKind.Relative));

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Clamped rather than rejected: paging is navigation, and a 400 on ?page=0 helps
        // nobody. But an unbounded pageSize must never reach the database.
        Assert.Equal(1, body.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(200, body.RootElement.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task InvitingAnAdministrator_CreatesAPendingAccountThatCannotSignIn()
    {
        Guid roleId = await SupportAgentRoleIdAsync();

        using HttpResponseMessage created = await _client.PostAsJsonAsync(Users, new
        {
            email = "invited@gymos.test",
            firstName = "Invited",
            lastName = "Admin",
            roleIds = new[] { roleId },
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // 201 carries the created resource and its ETag, so a client needs no follow-up read.
        JsonElement createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        Guid invitedId = createdBody.GetProperty("id").GetGuid();
        Assert.Equal(nameof(UserStatus.PendingActivation), createdBody.GetProperty("status").GetString());
        Assert.NotNull(created.Headers.ETag);
        Assert.Equal($"\"{createdBody.GetProperty("rowVersion").GetString()}\"", created.Headers.ETag!.ToString());
        Assert.Equal($"/api/v1/users/{invitedId}", created.Headers.Location!.ToString());

        using HttpResponseMessage detail = await _client.GetAsync(
            new Uri($"/api/v1/users/{invitedId}", UriKind.Relative));
        using JsonDocument body = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());

        Assert.Equal(nameof(UserStatus.PendingActivation), body.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            SystemRoles.SupportAgent,
            body.RootElement.GetProperty("roles").EnumerateArray().Single().GetProperty("name").GetString());

        // The invited account holds the role's permissions but has no way to authenticate:
        // no administrator ever sets another's password.
        Assert.Contains(
            PlatformPermissions.AuditRead,
            body.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task AnAdministratorWithoutUsersManage_IsRefusedWith403()
    {
        // A SupportAgent holds users.read and audit.read, but not users.manage.
        HttpClient supportAgent = await SignedInSupportAgentAsync();

        using HttpResponseMessage read = await supportAgent.GetAsync(Users);
        using HttpResponseMessage write = await supportAgent.PostAsJsonAsync(Users, new
        {
            email = "sneaky@gymos.test",
            firstName = "No",
            lastName = "Permission",
            roleIds = Array.Empty<Guid>(),
        });

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal("application/problem+json", write.Content.Headers.ContentType?.MediaType);

        supportAgent.Dispose();
    }

    [Fact]
    public async Task AnAdministratorWithoutRolesRead_CannotReadRoles()
    {
        HttpClient billingAdmin = await PlatformApiHelpers.SignedInAsync(
            _host, _client, SystemRoles.BillingAdmin, "billing@gymos.test");

        // BillingAdmin currently holds only users.read (billing permissions arrive with M3).
        using HttpResponseMessage response = await billingAdmin.GetAsync(Roles);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        billingAdmin.Dispose();
    }

    [Fact]
    public async Task RolesReportTheirPermissionsAndHolderCount()
    {
        using HttpResponseMessage response = await _client.GetAsync(Roles);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement superAdmin = body.RootElement.EnumerateArray()
            .Single(role => role.GetProperty("name").GetString() == SystemRoles.SuperAdmin);

        Assert.True(superAdmin.GetProperty("isSystem").GetBoolean());
        Assert.Equal(1, superAdmin.GetProperty("assignedUserCount").GetInt32());
        Assert.Equal(
            PlatformPermissions.All.Count,
            superAdmin.GetProperty("permissions").GetArrayLength());
    }

    [Fact]
    public async Task SuspendingAnAdministrator_EndsTheirSessionOnTheNextRequest()
    {
        Guid invitedId = await InviteAsync("to.suspend@gymos.test");
        Guid roleId = await SupportAgentRoleIdAsync();
        await PlatformApiHelpers.SetRolesAsync(_client, invitedId, roleId);

        // Give the invited account a password so it can actually hold a session.
        HttpClient victim = await PlatformApiHelpers.SignedInAsync(
            _host, _client, SystemRoles.SupportAgent, "session.victim@gymos.test");
        using HttpResponseMessage beforeSuspension = await victim.GetAsync(
            new Uri("/api/v1/auth/me", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, beforeSuspension.StatusCode);

        Guid victimId = await PlatformApiHelpers.FindUserIdAsync(_client, "session.victim@gymos.test");
        using HttpResponseMessage suspended = await _client.PostAsJsonAsync(
            new Uri($"/api/v1/users/{victimId}/suspend", UriKind.Relative),
            new { reason = "Compromised credentials" });
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);

        // The access token is still signed and unexpired — and refused, because suspension
        // rotated the security stamp the validator checks (AD-4).
        using HttpResponseMessage afterSuspension = await victim.GetAsync(
            new Uri("/api/v1/auth/me", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, afterSuspension.StatusCode);

        victim.Dispose();
    }

    [Fact]
    public async Task TheLastSuperAdminCannotBeSuspended()
    {
        Guid superAdminId = await PlatformApiHelpers.FindUserIdAsync(_client, AuthenticationHost.SuperAdminEmail);

        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            new Uri($"/api/v1/users/{superAdminId}/suspend", UriKind.Relative),
            new { reason = "Trying to lock everyone out" });

        // Caught by the self-suspension guard first — the seeded SuperAdmin is the caller.
        // Either way the platform stays administrable.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.StartsWith("users.", body.RootElement.GetProperty("code").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The action filter matches regardless of case, and treats LIKE metacharacters literally.
    ///
    /// <para>Reported from the console: typing `User.` returned nothing while `user.` worked, because
    /// the filter translated to a case-sensitive LIKE. Action codes are lower-case by convention, so
    /// the old behaviour required the reader to already know that — and answered "no entries match"
    /// when entries plainly matched.</para>
    ///
    /// <para>The `%` case is the same finding the 2026-08-11 audit fixed for four other query
    /// classes: an unescaped metacharacter makes the filter match rows that do not start with what
    /// was typed. Asserted here because it is invisible until someone types one.</para>
    /// </summary>
    [Fact]
    public async Task TheActionFilterIgnoresCaseAndTreatsWildcardsLiterally()
    {
        await InviteAsync("filter-case@gymos.test");

        // The same prefix in three casings must return the same entries.
        int lower = await CountActionsAsync("user.");
        int capitalised = await CountActionsAsync("User.");
        int shouty = await CountActionsAsync("USER.");

        Assert.True(lower > 0, "expected user.* entries from the invite above");
        Assert.Equal(lower, capitalised);
        Assert.Equal(lower, shouty);

        // A bare `%` is a literal, not "everything": no action code contains a percent sign.
        Assert.Equal(0, await CountActionsAsync("%"));

        // And an underscore matches an underscore, not any single character. `use_` is the prefix
        // that proves it: nothing starts with a literal "use_", but if the underscore were a
        // wildcard it would match every `user.*` and `user_role.*` row there is.
        //
        // (Not `user_`, which was the first attempt and a wrong assertion rather than a bug —
        // `user_role` is a real audited entity, so entries legitimately start with it.)
        Assert.Equal(0, await CountActionsAsync("use_"));
    }

    private async Task<int> CountActionsAsync(string actionPrefix)
    {
        using HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/api/v1/audit-logs?pageSize=200&action={Uri.EscapeDataString(actionPrefix)}",
                UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.GetProperty("items").GetArrayLength();
    }

    [Fact]
    public async Task EntityChangesAppearInTheAuditTrail_WithSecretsRedacted()
    {
        Guid invitedId = await InviteAsync("audited@gymos.test");

        using HttpResponseMessage response = await _client.GetAsync(
            new Uri("/api/v1/audit-logs?action=user.", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement created = body.RootElement.GetProperty("items").EnumerateArray()
            .First(entry => entry.GetProperty("action").GetString() == "user.created"
                && entry.GetProperty("entityId").GetGuid() == invitedId);

        // Attributed to the acting administrator, not to the row that changed.
        Assert.Equal(AuthenticationHost.SuperAdminEmail, created.GetProperty("userEmail").GetString());
        Assert.NotNull(created.GetProperty("correlationId").GetString());

        using HttpResponseMessage detail = await _client.GetAsync(
            new Uri($"/api/v1/audit-logs/{created.GetProperty("id").GetGuid()}", UriKind.Relative));
        string detailBody = await detail.Content.ReadAsStringAsync();

        Assert.Contains("audited@gymos.test", detailBody, StringComparison.Ordinal);

        // The password hash and security stamp are masked at write time, so there is nothing
        // sensitive present to be returned.
        Assert.Contains("***REDACTED***", detailBody, StringComparison.Ordinal);
        Assert.DoesNotContain("$argon2id$", detailBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignInOutcomesAreAudited_IncludingTheFailuresTheCallerIsNotTold()
    {
        using HttpClient anonymous = _host.CreateClient();
        using HttpResponseMessage failed = await AuthApi.LoginAsync(
            anonymous, AuthenticationHost.SuperAdminEmail, "wrong-password");
        Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);

        using HttpResponseMessage response = await _client.GetAsync(
            new Uri("/api/v1/audit-logs?action=auth.", UriKind.Relative));
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var actions = body.RootElement.GetProperty("items").EnumerateArray()
            .Select(entry => entry.GetProperty("action").GetString())
            .ToList();

        // The response says only "invalid credentials"; the trail says which account and why.
        Assert.Contains("auth.signed_in", actions);
        Assert.Contains("auth.sign_in_failed", actions);
    }

    [Fact]
    public async Task TheAuditTrailHasNoWriteEndpoints()
    {
        // Append-only is a property of the API surface, not just of intent (AD-7).
        using HttpResponseMessage post = await _client.PostAsJsonAsync(AuditLogs, new { action = "forged" });
        using HttpResponseMessage delete = await _client.DeleteAsync(AuditLogs);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
    }

    [Fact]
    public async Task OutboxMessagesNowRecordTheActingUser()
    {
        Guid invitedId = await InviteAsync("outbox.attributed@gymos.test");
        Guid superAdminId = await PlatformApiHelpers.FindUserIdAsync(_client, AuthenticationHost.SuperAdminEmail);

        using HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/api/v1/audit-logs?entityId={invitedId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The audit row and the outbox row are both attributed to the acting administrator —
        // the seam that stamps them is the same one, so this covers both.
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.All(
            body.RootElement.GetProperty("items").EnumerateArray(),
            entry => Assert.Equal(superAdminId, entry.GetProperty("userId").GetGuid()));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private async Task SignInAsSuperAdminAsync()
    {
        using HttpResponseMessage signIn = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        AuthApi.Authorize(_client, await AuthApi.ReadAccessTokenAsync(signIn));
    }

    private async Task<Guid> SupportAgentRoleIdAsync()
    {
        using HttpResponseMessage response = await _client.GetAsync(Roles);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.EnumerateArray()
            .Single(role => role.GetProperty("name").GetString() == SystemRoles.SupportAgent)
            .GetProperty("id").GetGuid();
    }

    private async Task<Guid> InviteAsync(string email)
    {
        using HttpResponseMessage created = await _client.PostAsJsonAsync(Users, new
        {
            email,
            firstName = "Test",
            lastName = "Invitee",
            roleIds = Array.Empty<Guid>(),
        });

        Assert.True(
            created.StatusCode == HttpStatusCode.Created,
            $"Expected 201 inviting {email}, got {(int)created.StatusCode}: " +
            await created.Content.ReadAsStringAsync());

        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<HttpClient> SignedInSupportAgentAsync() =>
        PlatformApiHelpers.SignedInAsync(_host, _client, SystemRoles.SupportAgent, "support@gymos.test");
}
