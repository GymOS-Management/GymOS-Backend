using System.Net;
using GymOS.BuildingBlocks.Infrastructure.Outbox;
using GymOS.Platform.Application.Identity;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Identity.Events;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.Platform.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// Audit finding 7, pinned against real SQL Server: failed sign-ins are tallied by one atomic
/// statement rather than through the aggregate's row version, so parallel wrong guesses each count,
/// each answer 401, and the lockout trips at exactly the configured threshold. Its own host, because
/// the per-IP limiter has to be raised out of the way and the threshold set low enough to reach.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class FailedLoginTallyTests : IAsyncLifetime
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public FailedLoginTallyTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        string connectionString = await _sqlServer.CreateDatabaseAsync("failed_login_tally_tests");

        _host = AuthenticationHost.Create(connectionString, settings =>
        {
            settings["Security:LoginPolicy:MaxFailedAttempts"] =
                MaxFailedAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings["Security:LoginPolicy:LockoutDuration"] = LockoutDuration.ToString("c");

            // The IP limiter is the other half of sign-in throttling and would otherwise answer
            // 429 before the tally is ever reached. Not under test here.
            settings["Security:RateLimiting:SignInAttemptsPerWindow"] = "1000";
        });

        _client = _host.CreateClient();

        using HttpResponseMessage ready = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
        await _sqlServer.DropDatabaseAsync(_host.ConnectionString);
    }

    [Fact]
    public async Task ParallelWrongPasswords_EachCount_NoneIsAConflict_AndTheThresholdLocksTheAccount()
    {
        // Every guess is in flight before any completes — the shape finding 7 described, where four
        // of five used to lose the row version and come back 409.
        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, MaxFailedAttempts).Select(_ =>
                AuthApi.LoginAsync(_client, AuthenticationHost.SuperAdminEmail, "wrong-password")));

        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }

        // Five attempts counted means five: the account is now locked, and even the right password
        // is refused for the configured window.
        using HttpResponseMessage locked = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Equal(AuthenticationErrors.AccountLocked.Code, await AuthApi.ReadProblemCodeAsync(locked));

        await using AppDbContext context = _host.CreateDbContext();
        User user = await context.Set<User>().SingleAsync(u => u.Email == AuthenticationHost.SuperAdminEmail);

        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.NotNull(user.LockedOutUntilUtc);

        // One lockout, announced once — by whichever attempt tripped it, not by all five.
        List<OutboxMessage> lockouts = await context.Set<OutboxMessage>()
            .Where(message => message.EventName == "gymos.platform.user.locked_out")
            .ToListAsync();
        Assert.Single(lockouts);

        // And every one of the five was audited — the tally must not have cost the audit entry its
        // SaveChanges, which is exactly what the conflict used to do — plus the locked-out refusal
        // above, which is a failed sign-in too and is recorded with its own reason.
        int auditedFailures = await context.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM platform.audit_logs WHERE action = 'auth.sign_in_failed'")
            .SingleAsync();
        Assert.Equal(MaxFailedAttempts + 1, auditedFailures);
    }

    [Fact]
    public async Task TheTally_AdvancesByOne_LocksAtTheThreshold_AndIgnoresGuessesDuringTheLockout()
    {
        DateTimeOffset now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

        await using AppDbContext context = _host.CreateDbContext();
        var repository = new UserRepository(context);

        User user = User.Create($"tally-{Guid.NewGuid():N}@gymos.test", "Tally", "Test");
        user.SetPasswordHash("hash", now);
        repository.Add(user);
        await context.SaveChangesAsync();
        user.DequeueDomainEvents();

        for (int attempt = 1; attempt < 3; attempt++)
        {
            await repository.RecordFailedLoginAsync(user, now, maxAttempts: 3, LockoutDuration, CancellationToken.None);
            Assert.Equal(attempt, user.FailedLoginAttempts);
            Assert.False(user.IsLockedOut(now));
        }

        await repository.RecordFailedLoginAsync(user, now, maxAttempts: 3, LockoutDuration, CancellationToken.None);

        Assert.True(user.IsLockedOut(now));
        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.Equal(now.Add(LockoutDuration), user.LockedOutUntilUtc);
        Assert.IsType<UserLockedOut>(Assert.Single(user.DomainEvents));

        // A guess during the window changes nothing — the lockout is not extended and the streak
        // does not start again underneath it.
        await repository.RecordFailedLoginAsync(user, now.AddMinutes(1), maxAttempts: 3, LockoutDuration, CancellationToken.None);

        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.Equal(now.Add(LockoutDuration), user.LockedOutUntilUtc);

        // Once it has expired, the next guess is the first of a new streak.
        DateTimeOffset later = now.Add(LockoutDuration).AddMinutes(1);
        await repository.RecordFailedLoginAsync(user, later, maxAttempts: 3, LockoutDuration, CancellationToken.None);

        Assert.Equal(1, user.FailedLoginAttempts);
        Assert.False(user.IsLockedOut(later));

        // The aggregate is left saveable: no stale row version is carried into the next write.
        user.UpdateProfile("Tally", "Saved");
        await context.SaveChangesAsync();
    }
}
