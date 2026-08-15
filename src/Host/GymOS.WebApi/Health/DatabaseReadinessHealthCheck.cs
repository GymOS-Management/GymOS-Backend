using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GymOS.WebApi.Health;

/// <summary>
/// The readiness gate on the database (AD-48): reports <b>unhealthy</b> unless the database is
/// reachable <i>and</i> its schema matches this build's migrations.
/// </summary>
/// <remarks>
/// <para><b>Why "reachable" was the wrong question.</b> This replaces
/// <c>AddDbContextCheck&lt;AppDbContext&gt;</c>, which answers <c>CanConnectAsync</c> — true of an
/// empty database, of one three migrations behind, and of a correct one alike. Every startup step
/// that follows already declines to run against a pending schema:
/// <c>TenantIsolationValidator</c> returns early, so R14 is off for that boot; all three seeders
/// skip; both maintenance sweeps skip. Each writes a warning nobody is watching, and then
/// <c>/health/ready</c> answered <b>200</b>. A bare <c>dotnet run</c> against the development
/// configuration (<c>MigrateOnStartup: false</c>, no <c>MigrateOnly</c>) is exactly that shape.</para>
/// <para><b>The gap is the database that exists but is empty or behind</b> — not the one that is
/// missing entirely. An earlier version of this remark claimed a host pointed at a <i>nonexistent</i>
/// database "would have reported ready", and that overstated what changed: <c>CanConnectAsync</c>
/// opens a connection, so <c>3D000 invalid_catalog_name</c> arrived there as a failed open, which
/// EF's <c>RelationalDatabaseCreator</c> catches into <c>false</c> — the old registration already
/// reported Unhealthy for it. (Read off EF's implementation, not re-observed here.) Stating a
/// control's scope wider than it is, in the comment a future reader trusts, is the same
/// stale-rationale trap this repository keeps finding elsewhere.</para>
/// <para><b>The nonexistent database stays covered here too, and that part was measured.</b> Npgsql's
/// database creator treats <c>3D000</c> as "the database does not exist" rather than as an error, so
/// <c>GetPendingMigrationsAsync</c> against a database that <i>does not exist at all</i> returns
/// every migration as pending instead of throwing — this check reports Unhealthy with the list
/// rather than failing on the way to the answer. Removing the <c>Database:MigrateOnly</c> branch
/// from <c>Program.cs</c> and booting a host at a nonexistent database produced no exception and six
/// skip warnings, every startup step declining in turn. A process that cannot serve correct data is
/// not ready, so this is where that belongs.</para>
/// <para><b>Reachability comes for free.</b> Asking for the pending list opens a connection, so an
/// unreachable database throws here and the health-check pipeline reports the registration's failure
/// status — <c>Unhealthy</c>. That is why this is one check rather than a connectivity check plus a
/// migrations check: the health-check service runs registrations concurrently, each in its own scope
/// with its own <c>DbContext</c>, so a second registration costs every host a second physical
/// connection. Across the integration suite's ~100 hosts against one container that exhausted
/// <c>max_connections</c>; see the comment at the registration in <c>Program.cs</c>.</para>
/// <para><b>Not wired into liveness.</b> <c>/health/live</c> runs no checks at all
/// (<c>Predicate = _ =&gt; false</c>). Reporting unready tells a load balancer to send no traffic;
/// reporting undead would restart a container that is behaving perfectly, forever, since nothing
/// about a restart applies a migration.</para>
/// <para><b>Where it changes nothing.</b> Compose gates <c>api</c> on the one-shot <c>migrator</c>
/// service completing successfully, so nothing is ever pending there; the integration harness
/// migrates before it boots a host. Both verified rather than assumed.</para>
/// <para>Lives in the host, like <c>DashboardHealthCheckAdapter</c> and for the same reason: the
/// health-check abstractions ship in the ASP.NET Core shared framework, which the Infrastructure
/// project — a plain <c>Microsoft.NET.Sdk</c> library — does not reference.</para>
/// </remarks>
internal sealed class DatabaseReadinessHealthCheck : IHealthCheck
{
    private readonly AppDbContext _dbContext;

    public DatabaseReadinessHealthCheck(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        List<string> pending =
            [.. await _dbContext.Database.GetPendingMigrationsAsync(cancellationToken)];

        if (pending.Count == 0)
        {
            return HealthCheckResult.Healthy(
                "The database is reachable and its schema matches this build's migrations.");
        }

        // Names, not just a count: the operator's next question is always which ones, and the answer
        // separates "the migrator container did not run" from "this image is older than the
        // database". Migration names are compiled-in identifiers, not data — and nothing here
        // reaches a caller anyway: /health/ready returns the status word alone, and
        // /dashboard/health projects neither Description nor Exception by design.
        return HealthCheckResult.Unhealthy(
            $"The database has {pending.Count} pending migration(s), so this host cannot serve " +
            $"correct data: {string.Join(", ", pending)}. Migrations are applied by the one-shot " +
            "`migrator` service (Database:MigrateOnly), not by this process.");
    }
}
