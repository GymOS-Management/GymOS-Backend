using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GymOS.BuildingBlocks.Infrastructure.Auditing;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// What the audit interceptor actually captures (AD-7). The redaction *policy* has unit tests;
/// this covers the interceptor's behavior against a real change tracker — the diff shape, and the
/// cases where it must produce nothing at all.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class AuditCaptureTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public AuditCaptureTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        string connectionString = await _postgres.CreateIsolatedDatabaseAsync("audit_capture");
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
    public async Task AnUpdateRecordsOnlyThePropertiesThatChanged()
    {
        Guid userId = await InviteAsync("rename.me@gymos.test");

        using HttpResponseMessage updated = await PutAsync(
            $"/api/v1/users/{userId}",
            new { firstName = "Renamed", lastName = "Person" },
            await CurrentRowVersionAsync(userId));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        AuditRow entry = await LatestAsync("user.updated", userId);

        Dictionary<string, string?> oldValues = Parse(entry.OldValues);
        Dictionary<string, string?> newValues = Parse(entry.NewValues);

        Assert.Equal("Renamed", newValues["FirstName"]);
        Assert.Equal("Person", newValues["LastName"]);
        Assert.Equal("Test", oldValues["FirstName"]);

        // Not a full row snapshot: a diff listing every unchanged column is a diff nobody reads.
        // UpdatedAtUtc rides along because the timestamp interceptor really did change it.
        Assert.DoesNotContain("Email", newValues.Keys);
        Assert.DoesNotContain("Status", newValues.Keys);
    }

    [Fact]
    public async Task WritingTheSameValuesAgainRecordsNothing()
    {
        Guid userId = await InviteAsync("unchanged@gymos.test");
        int before = await CountAsync("user.updated", userId);

        // Same values as the invite created, twice. The version is re-read each time because a
        // no-op update still touches the row's timestamp, and therefore its version.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using HttpResponseMessage response = await PutAsync(
                $"/api/v1/users/{userId}",
                new { firstName = "Test", lastName = "Invitee" },
                await CurrentRowVersionAsync(userId));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // EF marks a property modified when it is *assigned*, not when the value differs. Without
        // comparing, every idempotent PUT would leave a permanent "changed nothing" entry and the
        // trail would fill with noise that hides the real events.
        Assert.Equal(before, await CountAsync("user.updated", userId));
    }

    [Fact]
    public async Task RoleAssignmentsAreAuditedAsTheirOwnEntity()
    {
        Guid userId = await InviteAsync("role.audited@gymos.test");
        Guid roleId = await RoleIdAsync(SystemRoles.SupportAgent);

        using HttpResponseMessage granted = await PutAsync(
            $"/api/v1/users/{userId}/roles",
            new { roleIds = new[] { roleId } },
            await CurrentRowVersionAsync(userId));
        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);

        using HttpResponseMessage revoked = await PutAsync(
            $"/api/v1/users/{userId}/roles",
            new { roleIds = Array.Empty<Guid>() },
            await CurrentRowVersionAsync(userId));
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

        // "Who granted whom which role, and when" is the question an access review asks, so the
        // join row is audited in its own right rather than folded into the user.
        Assert.True(await CountAsync("user_role.created") > 0);
        Assert.True(await CountAsync("user_role.deleted") > 0);
    }

    [Fact]
    public async Task RefreshTokensAreNotAudited()
    {
        // Deliberately excluded: every sign-in and every rotation writes one, so auditing the
        // table would bury the administrative actions the log exists for. Sessions are recorded
        // as auth.* entries instead.
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.True(await dbContext.Set<RefreshToken>().AnyAsync());
        Assert.False(await dbContext.Set<AuditLog>().AnyAsync(log => log.EntityType == "refresh_token"));
    }

    [Fact]
    public async Task TheAuditTrailDoesNotAuditItself()
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // A recursion with no reader — and one that would double the write volume of every request.
        Assert.False(await dbContext.Set<AuditLog>().AnyAsync(log => log.EntityType == "audit_log"));
    }

    [Fact]
    public async Task SystemInitiatedChangesAreRecordedWithNoActingUser()
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The startup catalog sync creates the roles with nobody signed in. Attributing those to
        // whoever happens to sign in first would be a lie in a permanent record.
        List<AuditLog> roleCreations = await dbContext.Set<AuditLog>()
            .Where(log => log.Action == "role.created")
            .ToListAsync();

        Assert.NotEmpty(roleCreations);
        Assert.All(roleCreations, log =>
        {
            Assert.Null(log.UserId);
            Assert.Null(log.UserEmail);
        });
    }

    [Fact]
    public async Task EveryEntryFromOneRequestSharesItsCorrelationId()
    {
        var correlationId = Guid.NewGuid();

        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/v1/users", UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                email = "correlated@gymos.test",
                firstName = "Test",
                lastName = "Invitee",
                roleIds = Array.Empty<Guid>(),
            }),
        };
        request.Headers.Add("X-Correlation-Id", correlationId.ToString());

        using HttpResponseMessage created = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // One id ties the audit rows and the outbox rows of a single request together (AD-38) —
        // which is what makes a multi-entity operation reconstructable after the fact.
        Assert.True(await dbContext.Set<AuditLog>().AnyAsync(log => log.CorrelationId == correlationId));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private async Task<Guid> InviteAsync(string email)
    {
        using HttpResponseMessage created = await _client.PostAsJsonAsync(
            new Uri("/api/v1/users", UriKind.Relative),
            new { email, firstName = "Test", lastName = "Invitee", roleIds = Array.Empty<Guid>() });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<HttpResponseMessage> PutAsync(string path, object body, string rowVersion)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");

        return await _client.SendAsync(request);
    }

    private async Task<string> CurrentRowVersionAsync(Guid userId)
    {
        using HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/api/v1/users/{userId}", UriKind.Relative));
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.GetProperty("rowVersion").GetString()!;
    }

    private async Task<Guid> RoleIdAsync(string roleName)
    {
        using HttpResponseMessage response = await _client.GetAsync(new Uri("/api/v1/roles", UriKind.Relative));
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.EnumerateArray()
            .Single(role => role.GetProperty("name").GetString() == roleName)
            .GetProperty("id").GetGuid();
    }

    private async Task<AuditRow> LatestAsync(string action, Guid entityId)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.Set<AuditLog>()
            .Where(log => log.Action == action && log.EntityId == entityId)
            .OrderByDescending(log => log.CreatedAtUtc)
            .Select(log => new AuditRow(log.OldValues, log.NewValues))
            .FirstAsync();
    }

    private async Task<int> CountAsync(string action, Guid? entityId = null)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.Set<AuditLog>()
            .CountAsync(log => log.Action == action && (entityId == null || log.EntityId == entityId));
    }

    private static Dictionary<string, string?> Parse(string? json) =>
        json is null
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ?? [];

    private sealed record AuditRow(string? OldValues, string? NewValues);
}
