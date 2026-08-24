using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// Optimistic concurrency over real HTTP against real SQL Server (ARCHITECTURE.md §3).
///
/// The scenario every case here comes back to: two administrators open the same account, both
/// edit, both save. Exactly one must win, and the loser must be told — not silently overwritten.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class ConcurrencyTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public ConcurrencyTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        string connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("concurrency");
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
    public async Task AReadCarriesAnETagThatMatchesTheBodysRowVersion()
    {
        Guid userId = await InviteAsync("etag@gymos.test");

        using HttpResponseMessage response = await _client.GetAsync(UserUri(userId));
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        string rowVersion = body.RootElement.GetProperty("rowVersion").GetString()!;

        // Header and body must agree, or a client that trusts one and sends the other gets a 409
        // for no reason.
        Assert.NotNull(response.Headers.ETag);
        Assert.Equal($"\"{rowVersion}\"", response.Headers.ETag!.ToString());
        Assert.True(response.Headers.ETag.IsWeak is false);
    }

    [Fact]
    public async Task TheSecondOfTwoConcurrentUpdatesIsRefused()
    {
        Guid userId = await InviteAsync("lost.update@gymos.test");

        // Both administrators read the same version — the interleaving that loses a write.
        string sharedVersion = await RowVersionAsync(userId);

        using HttpResponseMessage first = await PutAsync(
            userId, new { firstName = "First", lastName = "Writer" }, sharedVersion);
        using HttpResponseMessage second = await PutAsync(
            userId, new { firstName = "Second", lastName = "Writer" }, sharedVersion);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // The first writer's change stands; the second was told, not silently dropped.
        using JsonDocument current = JsonDocument.Parse(
            await (await _client.GetAsync(UserUri(userId))).Content.ReadAsStringAsync());
        Assert.Equal("First", current.RootElement.GetProperty("firstName").GetString());
    }

    [Fact]
    public async Task AConflictExplainsItselfAndSaysItIsNotRetryable()
    {
        Guid userId = await InviteAsync("conflict.body@gymos.test");
        string staleVersion = await RowVersionAsync(userId);

        using HttpResponseMessage _ = await PutAsync(
            userId, new { firstName = "Winner", lastName = "Writer" }, staleVersion);
        using HttpResponseMessage conflicted = await PutAsync(
            userId, new { firstName = "Loser", lastName = "Writer" }, staleVersion);

        Assert.Equal(HttpStatusCode.Conflict, conflicted.StatusCode);
        Assert.Equal("application/problem+json", conflicted.Content.Headers.ContentType?.MediaType);

        using JsonDocument problem = JsonDocument.Parse(await conflicted.Content.ReadAsStringAsync());

        Assert.Equal("concurrency.conflict", problem.RootElement.GetProperty("code").GetString());

        // Says so explicitly: a client that retries the same stale version loops forever.
        Assert.False(problem.RootElement.GetProperty("retryable").GetBoolean());
        Assert.Contains("Re-read", problem.RootElement.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryingWithTheFreshVersionSucceeds()
    {
        Guid userId = await InviteAsync("retry@gymos.test");
        string staleVersion = await RowVersionAsync(userId);

        using HttpResponseMessage _ = await PutAsync(
            userId, new { firstName = "Other", lastName = "Writer" }, staleVersion);
        using HttpResponseMessage conflicted = await PutAsync(
            userId, new { firstName = "Mine", lastName = "Writer" }, staleVersion);
        Assert.Equal(HttpStatusCode.Conflict, conflicted.StatusCode);

        // The documented recovery: re-read, re-apply, retry.
        using HttpResponseMessage retried = await PutAsync(
            userId, new { firstName = "Mine", lastName = "Writer" }, await RowVersionAsync(userId));

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await retried.Content.ReadAsStringAsync());
        Assert.Equal("Mine", body.RootElement.GetProperty("firstName").GetString());
    }

    [Fact]
    public async Task AnUpdateReturnsTheNewVersionSoASecondEditNeedsNoExtraRead()
    {
        Guid userId = await InviteAsync("chained@gymos.test");

        using HttpResponseMessage first = await PutAsync(
            userId, new { firstName = "Step", lastName = "One" }, await RowVersionAsync(userId));
        using JsonDocument firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        string nextVersion = firstBody.RootElement.GetProperty("rowVersion").GetString()!;

        // The whole reason writes return the resource rather than 204: a client can chain edits.
        using HttpResponseMessage second = await PutAsync(
            userId, new { firstName = "Step", lastName = "Two" }, nextVersion);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal($"\"{nextVersion}\"", first.Headers.ETag!.ToString());
    }

    [Fact]
    public async Task APutWithNoPreconditionIsRefusedWith428()
    {
        Guid userId = await InviteAsync("no.precondition@gymos.test");

        using HttpResponseMessage response = await _client.PutAsJsonAsync(
            UserUri(userId), new { firstName = "No", lastName = "Precondition" });

        // Not accepted-as-overwrite: allowing it would be last-write-wins with a nicer name.
        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);

        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("concurrency.version_required", problem.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("W/\"12\"")]
    public async Task AMalformedPreconditionIsA400NotA409(string ifMatch)
    {
        Guid userId = await InviteAsync($"malformed{ifMatch.Length}@gymos.test");

        using HttpResponseMessage response = await PutAsync(
            userId, new { firstName = "Bad", lastName = "Header" }, ifMatch, quote: false);

        // 409 would tell the caller to retry a request that can never succeed.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("concurrency.malformed_version", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RoleChangesAreVersionedToo()
    {
        Guid userId = await InviteAsync("versioned.roles@gymos.test");
        string sharedVersion = await RowVersionAsync(userId);

        using HttpResponseMessage rolesResponse = await _client.GetAsync(new Uri("/api/v1/roles", UriKind.Relative));
        using JsonDocument roles = JsonDocument.Parse(await rolesResponse.Content.ReadAsStringAsync());
        var roleIds = roles.RootElement.EnumerateArray()
            .Take(1)
            .Select(role => role.GetProperty("id").GetGuid())
            .ToArray();

        using HttpResponseMessage first = await PutRolesAsync(userId, roleIds, sharedVersion);
        using HttpResponseMessage second = await PutRolesAsync(userId, [], sharedVersion);

        // A role set is exactly the kind of state two administrators edit at once.
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task AnActionEndpointAcceptsAFreshPreconditionAndRefusesAStaleOne()
    {
        Guid userId = await InviteAsync("versioned.suspend@gymos.test");
        string staleVersion = await RowVersionAsync(userId);

        using HttpResponseMessage renamed = await PutAsync(
            userId, new { firstName = "Moved", lastName = "On" }, staleVersion);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        using HttpResponseMessage stale = await SuspendAsync(userId, staleVersion);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using HttpResponseMessage fresh = await SuspendAsync(userId, await RowVersionAsync(userId));
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task AnActionEndpointStillWorksWithNoPrecondition()
    {
        Guid userId = await InviteAsync("unversioned.suspend@gymos.test");

        // Honored when given, not demanded: the lifecycle transition rules already refuse the
        // orderings that would corrupt state, so requiring a fresh GET here buys nothing.
        using HttpResponseMessage response = await SuspendAsync(userId, ifMatch: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static Uri UserUri(Guid userId) => new($"/api/v1/users/{userId}", UriKind.Relative);

    private async Task<Guid> InviteAsync(string email)
    {
        using HttpResponseMessage created = await _client.PostAsJsonAsync(
            new Uri("/api/v1/users", UriKind.Relative),
            new { email, firstName = "Test", lastName = "Invitee", roleIds = Array.Empty<Guid>() });

        Assert.True(
            created.StatusCode == HttpStatusCode.Created,
            $"Expected 201, got {(int)created.StatusCode}: {await created.Content.ReadAsStringAsync()}");

        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<string> RowVersionAsync(Guid userId)
    {
        using HttpResponseMessage response = await _client.GetAsync(UserUri(userId));
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.GetProperty("rowVersion").GetString()!;
    }

    private Task<HttpResponseMessage> PutAsync(Guid userId, object body, string ifMatch, bool quote = true) =>
        SendAsync(HttpMethod.Put, UserUri(userId), body, quote ? $"\"{ifMatch}\"" : ifMatch);

    private Task<HttpResponseMessage> PutRolesAsync(Guid userId, Guid[] roleIds, string ifMatch) =>
        SendAsync(
            HttpMethod.Put,
            new Uri($"/api/v1/users/{userId}/roles", UriKind.Relative),
            new { roleIds },
            $"\"{ifMatch}\"");

    private Task<HttpResponseMessage> SuspendAsync(Guid userId, string? ifMatch) =>
        SendAsync(
            HttpMethod.Post,
            new Uri($"/api/v1/users/{userId}/suspend", UriKind.Relative),
            new { reason = "Concurrency test" },
            ifMatch is null ? null : $"\"{ifMatch}\"");

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        Uri uri,
        object body,
        string? ifMatch)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = JsonContent.Create(body) };

        if (ifMatch is not null)
        {
            // TryAddWithoutValidation: the malformed-header cases exist precisely to send values
            // HttpClient's own validation would refuse to transmit.
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await _client.SendAsync(request);
    }
}
