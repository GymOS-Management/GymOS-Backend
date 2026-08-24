using System.Net;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The initial-SuperAdmin seed (M1.2). The two properties worth guarding are both
/// about what the seeder refuses to do: it never invents a credential, and it never
/// overwrites one.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class SuperAdminSeedingTests
{
    private readonly SqlServerFixture _sqlServer;

    public SuperAdminSeedingTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    [Fact]
    public async Task WithNoSeedConfigured_TheHostStarts_AndNoUserExists()
    {
        string connectionString = await _sqlServer.CreateDatabaseAsync("seed_unconfigured");

        // Blanked, not removed. WebApplicationFactory loads the host project's user
        // secrets, so a developer who configured a local SuperAdmin would otherwise have
        // it seeded into this test's database — and the test would pass on CI and fail on
        // their machine. An explicit empty value overrides whatever is out there.
        await using AuthenticationHost host = AuthenticationHost.Create(connectionString, settings =>
        {
            settings["Seed:SuperAdmin:Email"] = string.Empty;
            settings["Seed:SuperAdmin:Password"] = string.Empty;
        });

        using HttpClient client = host.CreateClient();
        using HttpResponseMessage ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        // Unconfigured means no account — not a default one. The seeder logs how to
        // configure it and does nothing else.
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await dbContext.Set<User>().ToListAsync());

        // The role catalog is still seeded, so configuring the credential later works.
        Assert.NotEmpty(await dbContext.Set<Role>().ToListAsync());
    }

    [Fact]
    public async Task ARejectedPassword_CreatesNoAccountAtAll()
    {
        string connectionString = await _sqlServer.CreateDatabaseAsync("seed_weak_password");

        await using AuthenticationHost host = AuthenticationHost.Create(
            connectionString,
            settings => settings["Seed:SuperAdmin:Password"] = "short");

        using HttpClient client = host.CreateClient();
        using HttpResponseMessage ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        // A weak seed password is an operator error, logged loudly — but it must not
        // produce a half-created, passwordless SuperAdmin either.
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await dbContext.Set<User>().ToListAsync());
    }

    [Fact]
    public async Task ASecondStartupWithADifferentPassword_LeavesTheExistingAccountUntouched()
    {
        string connectionString = await _sqlServer.CreateDatabaseAsync("seed_idempotency");

        await using (AuthenticationHost first = AuthenticationHost.Create(connectionString))
        {
            using HttpClient client = first.CreateClient();
            using HttpResponseMessage signIn = await AuthApi.LoginAsync(
                client, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);
            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        }

        // A stale environment variable on the next deployment must not silently reset a
        // real administrator's password — the seeder only ever creates.
        await using AuthenticationHost second = AuthenticationHost.Create(
            connectionString,
            settings => settings["Seed:SuperAdmin:Password"] = "a-completely-different-password");

        using HttpClient secondClient = second.CreateClient();

        using HttpResponseMessage withStaleConfig = await AuthApi.LoginAsync(
            secondClient, AuthenticationHost.SuperAdminEmail, "a-completely-different-password");
        Assert.Equal(HttpStatusCode.Unauthorized, withStaleConfig.StatusCode);

        using HttpResponseMessage withOriginal = await AuthApi.LoginAsync(
            secondClient, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);
        Assert.Equal(HttpStatusCode.OK, withOriginal.StatusCode);

        await using AsyncServiceScope scope = second.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(await dbContext.Set<User>().ToListAsync());
    }

    [Fact]
    public async Task TheSeededPasswordIsStoredAsAnArgon2idHash_NeverInTheClear()
    {
        string connectionString = await _sqlServer.CreateDatabaseAsync("seed_hash_shape");

        await using AuthenticationHost host = AuthenticationHost.Create(connectionString);
        using HttpClient client = host.CreateClient();
        using HttpResponseMessage ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        User seeded = await dbContext.Set<User>().SingleAsync();

        Assert.StartsWith("$argon2id$v=19$", seeded.PasswordHash, StringComparison.Ordinal);
        Assert.DoesNotContain(AuthenticationHost.SuperAdminPassword, seeded.PasswordHash, StringComparison.Ordinal);
        Assert.Equal(UserStatus.Active, seeded.Status);
    }
}
