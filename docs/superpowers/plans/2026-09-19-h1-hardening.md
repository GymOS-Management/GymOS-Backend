# H1 — Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the privilege-escalation window in tenant resolution (gym-scoped-grants spec steps 1–2) and land every review finding from the M4.1/M5 close-outs and later whole-branch reviews that waits on nothing but an increment to carry it.

**Architecture:** `TenantResolutionMiddleware` moves between authentication and authorization and lets anonymous callers through to be challenged; the caller snapshot splits into an identity read (stamp + status, used by the bearer handler) and a permissions read keyed by (user, tenant), so `IPermissionEvaluator` answers for the acting gym. Everything else is local: a seeder under the existing advisory lock, one shared source for two duplicated analytics queries, a transaction around the subscription-assignment handler, tests given the assertions their names promised, and documents brought back in line with the code — including two churn findings that turned out to be already fixed.

**Tech Stack:** .NET 10 (SDK 10.0.400), ASP.NET Core minimal APIs, EF Core + SQL Server (`UseSnakeCaseNamingConvention`), xUnit + Testcontainers, `Microsoft.CodeAnalysis.BannedApiAnalyzers`.

**Spec:** `docs/superpowers/specs/2026-09-19-h1-hardening-design.md` (which builds on `docs/superpowers/specs/2026-08-15-gym-scoped-grants-design.md` §2 D1–D4 and §4 tests 1–5).

## Global Constraints

- Build clean with warnings as errors: `dotnet build GymOS.Backend.slnx -c Release`. `dotnet format GymOS.Backend.slnx --verify-no-changes` before every commit.
- `DateTimeOffset.UtcNow` / `DateTime.UtcNow` are **banned** (BannedSymbols.txt, RS0030 as error). Use `IClock`, `SystemClock`, `FakeClock`, or fixed constants. Task 6 adds `IUnitOfWork.DiscardTrackedChanges` to the same file.
- **No new endpoint, permission, setting, table or migration.** `ApiEndpointTests`'s secured-route tripwire stays at **65**. The OpenAPI snapshot `contracts/openapi-v1.json` changes exactly once (Task 8, `ActivityEntry.entityType` becomes nullable) via `UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/GymOS.IntegrationTests --filter FullyQualifiedName~OpenApiContractTests`.
- Pipeline order after Task 1, verbatim: `UseAuthentication` → `UseRateLimiter` → `TenantResolutionMiddleware` → `MaintenanceModeMiddleware` → `UseAuthorization`.
- Permission rule (grants spec D3): effective permissions = grants with `TenantId == null` ∪ grants with `TenantId == actingTenant`. With no acting tenant, exactly the platform grants. A non-`Active` user holds nothing regardless of tenant.
- `TenantResolutionMiddleware`'s own grant check stays a raw `UserRole` query — never a call into `IPermissionEvaluator`.
- Security-sensitive tests are written before their fix and their red is observed: Task 2's test 1 fails on the unfixed code; Task 1's test 5 fails after the reorder and before the early return; Task 2's test 2 is the control that would fail under a platform-grants-only D3 (it passes today through the escalation). The commit message for each fix names the test that was red.
- Commits go straight to `development`; each task ends in a commit. Push only in the last task, after the controller's final review. Do not edit `docs/STATUS.md` before the last task.
- Commit trailers, verbatim: `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` and `Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE`.
- Every command runs from `C:\Work\Personal-Projects\GymOS\GymOS-Backend`; Docker Desktop must be running for `GymOS.IntegrationTests`. Integration tests are filtered by class while working (`--filter FullyQualifiedName~TenantResolutionTests`); the whole suite runs once, in Task 9.

---

## File map

**BuildingBlocks**
- Modify `src/BuildingBlocks/GymOS.BuildingBlocks.Application/Abstractions/IUnitOfWork.cs` (doc), `src/BuildingBlocks/GymOS.BuildingBlocks.Infrastructure/Idempotency/IdempotencyRecordConfiguration.cs` (doc).

**Platform.Application**
- Modify `Identity/Abstractions/IIdentityQueries.cs` (records + interfaces), `Analytics/Abstractions/IDashboardQueries.cs` (`ActivityEntry.EntityType` nullable, `IDashboardHealthCheck` doc), `Subscriptions/SubscriptionCommands.cs` (assign transaction + locked read + logging), `Gyms/GymLifecycleCommands.cs` (delete logging), `Subscriptions/SubscriptionRenewalService.cs` (logging), `Features/GymFeatureCommands.cs` (pragma).
- Create `Features/FeatureExceptionLogging.cs`.

**Platform.Infrastructure**
- Modify `Identity/PermissionEvaluator.cs` (both classes), `Persistence/Queries/IdentityQueries.cs`, `Persistence/Queries/DashboardQueries.cs`, `Analytics/DailyStatsCompiler.cs`, `Persistence/Configurations/DailyPlatformStatsConfiguration.cs`, `Persistence/Seeding/SuperAdminSeeder.cs`, `Persistence/Seeding/SeedingLocks.cs`.
- Create `Analytics/PlatformPopulationQueries.cs`.

**Platform.Domain**
- Modify `Features/GymFeatureExceptions.cs` (remove the suppression).

**Host**
- Modify `Program.cs` (pipeline), `Middleware/TenantResolutionMiddleware.cs`, `Auth/AuthenticationExtensions.cs` (identity read), `Extensions/OpenApiExtensions.cs` (seam + doc), `Extensions/ApiReferenceBrowserLauncher.cs` (null process).

**Repo root**
- Modify `BannedSymbols.txt`, `.editorconfig`, `docker-compose.override.yml`, `contracts/openapi-v1.json` (regenerated).

**Tests**
- IntegrationTests: `Tenancy/TenantResolutionTests.cs`, `MaintenanceModeTests.cs`, `SuperAdminSeedingTests.cs`, `AnalyticsApiTests.cs`, `DashboardApiTests.cs`, `ApiReferenceBrowserRegistrationTests.cs`, `GymFeatureLifecycleTests.cs` (one constructor call).
- Infrastructure.UnitTests: create `PermissionEvaluatorTests.cs`.
- Application.UnitTests: `Fakes/IdentityFakes.cs`, `Fakes/GymFakes.cs`, `Fakes/SubscriptionFakes.cs`, `GymTestContext.cs`, `GymManagementTests.cs`, `SubscriptionAssignmentTests.cs`.
- Domain.UnitTests: `MonthlyRecurringRevenueTests.cs`.

**Docs (Task 9 unless named earlier)**: `docs/STATUS.md`, `docs/BusinessRules/Analytics.md` §11, `docs/BusinessRules/TenantIsolation.md`, `docs/Infrastructure/DOCKER.md`, `docs/RELEASE-NOTES.md`, `docs/superpowers/specs/2026-08-15-gym-scoped-grants-design.md` §6.

---

### Task 1: Move `TenantResolutionMiddleware` before authorization; anonymous gym routes are challenged, not hidden

**Files:**
- Modify: `src/Host/GymOS.WebApi/Program.cs:334-353`
- Modify: `src/Host/GymOS.WebApi/Middleware/TenantResolutionMiddleware.cs`
- Test: `tests/GymOS.IntegrationTests/Tenancy/TenantResolutionTests.cs`, `tests/GymOS.IntegrationTests/MaintenanceModeTests.cs`

**Interfaces:**
- Consumes: `ICurrentUser.IsAuthenticated` (`HttpCurrentUser`: true iff the principal carries a parsable user id).
- Produces: the pipeline order every later task assumes; `ITenantContext.TenantId` is set **before** `MaintenanceModeMiddleware` and `UseAuthorization` run for a gym route.

- [ ] **Step 1: Write the test (grants spec §4 test 5).** In `TenantResolutionTests`, after `AGymTheCallerHasNoGrantForIsNotFound_NotForbidden`:

```csharp
    [Fact]
    public async Task AnAnonymousGymRouteIsChallenged_NotHidden()
    {
        // Grants spec D2. 404 is the right answer for "you hold nothing on this gym"; it is the wrong
        // answer for "you have not said who you are" — that is authentication's 401, and it must carry
        // the bearer challenge every other anonymous request gets. Today this holds only because
        // authorization runs first and the middleware never sees an anonymous request; once the
        // middleware moves in front of authorization (H1), it has to let anonymous callers through
        // itself, or every anonymous gym route becomes a 404.
        Guid gym = await PlatformApiHelpers.CreateGymAsync(_client, "anonymous-route-gym");
        using HttpClient anonymous = _host.CreateClient();

        using HttpResponseMessage response = await anonymous.GetAsync(
            new Uri($"/api/v1/gyms/{gym}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, challenge =>
            string.Equals(challenge.Scheme, "Bearer", StringComparison.Ordinal));

        // And nothing was resolved for nobody.
        Assert.Equal(ResolvedTenantProbeStartupFilter.NoTenant, ResolvedTenant(response));
    }
```

- [ ] **Step 2: Run it against today's pipeline**

Run: `dotnet test tests/GymOS.IntegrationTests --filter FullyQualifiedName~TenantResolutionTests.AnAnonymousGymRouteIsChallenged_NotHidden`
Expected: PASS — today authorization challenges before the middleware runs. Record it. The test earns its place in Step 4.

- [ ] **Step 3: Reorder the pipeline.** In `Program.cs`, delete the `app.UseMiddleware<TenantResolutionMiddleware>();` after `app.UseAuthorization();` together with its four-line comment, and insert immediately after `app.UseRateLimiter();`:

```csharp
    // H1 / grants spec D1: after authentication, so the caller is known; before the maintenance
    // middleware and before authorization, because both ask IPermissionEvaluator and its answer now
    // depends on the tenant this sets. An anonymous caller passes straight through and is challenged
    // by authorization (401); an authenticated caller with no grant for the gym is 404. Wrong order
    // still fails closed — no tenant, no rows — because TenantSessionInterceptor writes whatever this
    // leaves on the context.
    app.UseMiddleware<TenantResolutionMiddleware>();
```

- [ ] **Step 4: Run the test again — this is the red**

Run: the same command as Step 2.
Expected: FAIL with 404 — the middleware, now first, refuses the anonymous caller (`CallerHoldsAGrantForAsync` returns false for a null user) before authorization can challenge. This is the hole D2 names; Step 4a closes it.

- [ ] **Step 4a: Early return for anonymous callers.** In `TenantResolutionMiddleware.InvokeAsync`, between the `Guid.TryParse` block and the `CallerHoldsAGrantForAsync` call:

```csharp
        if (!currentUser.IsAuthenticated)
        {
            // Grants spec D2: an anonymous caller on a gym route is challenged by authorization (401),
            // not told the gym does not exist. Nothing is resolved here — no grant check, no tenant —
            // because there is no caller to resolve it for.
            await _next(context);
            return;
        }
```

Then rewrite the class's `<summary>`/`<remarks>` (read them first) so they say: runs **after authentication and before authorization**; anonymous → pass through to the challenge; authenticated without a grant → 404; the grant check is a raw `UserRole` query and must stay one because `IPermissionEvaluator` now depends on the tenant this sets. Keep the 404-not-403 reasoning. Run the Step 2 command once more: PASS.

- [ ] **Step 5: Add the maintenance-mode case.** In `MaintenanceModeTests`, after `WhenOnSuperAdminHealthAndSignInStillPass`:

```csharp
    [Fact]
    public async Task WhenOnSuperAdminPassesOnAGymRouteToo()
    {
        // H1 moved TenantResolutionMiddleware in front of this one. The SuperAdmin's pass is a
        // permission check that now runs with the tenant already set; it must still say yes.
        Guid gym = await PlatformApiHelpers.CreateGymAsync(_client, "maintenance-gym");

        using HttpResponseMessage toggle = await ToggleMaintenanceAsync(enabled: true, "maintenance window", "maintenance");
        Assert.Equal(HttpStatusCode.OK, toggle.StatusCode);

        using HttpResponseMessage response = await _client.GetAsync(new Uri($"/api/v1/gyms/{gym}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
```

- [ ] **Step 6: Mark the §6 proof.** In `ARouteWithAGymIdSetsThatTenantForTheRequest`, append to the existing comment block: `// Since H1 this test also carries the gym-scoped-grants spec's §6 claim: the middleware now runs before authorization, so a tenant observed here proves route values are populated that early in the pipeline.`

- [ ] **Step 7: Run the affected classes**

Run: `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~TenantResolutionTests|FullyQualifiedName~MaintenanceModeTests|FullyQualifiedName~ApiEndpointTests|FullyQualifiedName~InvoiceApiTests"`
Expected: PASS, all. If `ARouteWithAGymIdSetsThatTenantForTheRequest` fails with `NoTenant`, the §6 claim is false: add `app.UseRouting();` immediately before `app.UseAuthentication();` in `Program.cs` with the comment `// Route values must exist before TenantResolutionMiddleware reads them; WebApplication's implicit UseRouting proved insufficient here (H1 Task 1).`, re-run, and say so in the report.

- [ ] **Step 8: Format and commit**

```bash
dotnet format GymOS.Backend.slnx --verify-no-changes
git add src/Host/GymOS.WebApi/Program.cs src/Host/GymOS.WebApi/Middleware/TenantResolutionMiddleware.cs tests/GymOS.IntegrationTests/Tenancy/TenantResolutionTests.cs tests/GymOS.IntegrationTests/MaintenanceModeTests.cs
git commit -m "tenancy: resolve the tenant before authorization, and let anonymous gym routes reach the challenge

Grants spec D1/D2. With the middleware moved and no early return, AnAnonymousGymRouteIsChallenged_NotHidden
was red (404); the early return is what keeps it green in the new position.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 2: Tenant-aware permissions — split the caller snapshot, key permissions by (user, tenant)

**Files:**
- Modify: `src/Platform/GymOS.Platform.Application/Identity/Abstractions/IIdentityQueries.cs`
- Modify: `src/Platform/GymOS.Platform.Infrastructure/Persistence/Queries/IdentityQueries.cs:54-100`
- Modify: `src/Platform/GymOS.Platform.Infrastructure/Identity/PermissionEvaluator.cs` (both classes)
- Modify: `src/Host/GymOS.WebApi/Auth/AuthenticationExtensions.cs:92-97`
- Modify: `tests/GymOS.Platform.Application.UnitTests/Fakes/IdentityFakes.cs:114-142`
- Create: `tests/GymOS.Platform.Infrastructure.UnitTests/PermissionEvaluatorTests.cs`
- Test: `tests/GymOS.IntegrationTests/Tenancy/TenantResolutionTests.cs`

**Interfaces:**
- Consumes: `ITenantContext.TenantId` (BuildingBlocks.Application.Abstractions; set by Task 1's middleware before authorization).
- Produces:

```csharp
public sealed record UserSecurityIdentity(Guid UserId, Guid SecurityStamp, string Status);
public sealed record UserSecuritySnapshot(Guid UserId, Guid? TenantId, Guid SecurityStamp, string Status, IReadOnlyCollection<string> Permissions);
public interface ICallerSnapshot
{
    Task<UserSecurityIdentity?> GetIdentityAsync(Guid userId, CancellationToken cancellationToken);
    Task<UserSecuritySnapshot?> GetAsync(Guid userId, Guid? tenantId, CancellationToken cancellationToken);
}
public interface IIdentityQueries
{
    Task<UserProfile?> GetProfileAsync(Guid userId, CancellationToken cancellationToken);
    Task<UserSecurityIdentity?> GetSecurityIdentityAsync(Guid userId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(Guid userId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(Guid userId, Guid? tenantId, CancellationToken cancellationToken);
}
```
`GetSecuritySnapshotAsync` is **removed**.

- [ ] **Step 1: Write the failing integration tests (grants spec §4 tests 1 and 2).** In `TenantResolutionTests`, after Task 1's anonymous test. `SignedInWithGrantForAsync(gymId)` already exists in the file and writes exactly the grant these need (SupportAgent scoped to one gym, every platform grant revoked). SupportAgent holds `audit.read` and `gyms.read`.

```csharp
    [Fact]
    public async Task AGymScopedGrantHoldsNothingOnAPlatformRoute()
    {
        // Grants spec F1 — the escalation itself. SupportAgent holds audit.read. A SupportAgent grant
        // scoped to one gym must not make the platform-wide audit log readable; before H1 the
        // permission join filtered on UserId alone and it did.
        Guid gym = await PlatformApiHelpers.CreateGymAsync(_client, "scoped-grant-gym");
        using HttpClient scoped = await SignedInWithGrantForAsync(gym);

        using HttpResponseMessage response = await scoped.GetAsync(
            new Uri("/api/v1/audit-logs", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AGymScopedGrantHoldsItsPermissionsOnThatGymsRoutes()
    {
        // The negative control for the test above (grants spec §4, test 2): an evaluator that returned
        // nothing at all for a gym-scoped caller would pass it and be useless. SupportAgent holds
        // gyms.read; on the gym the grant names, that is a 200 — and the tenant was set for it.
        Guid gym = await PlatformApiHelpers.CreateGymAsync(_client, "scoped-grant-home-gym");
        using HttpClient scoped = await SignedInWithGrantForAsync(gym);

        using HttpResponseMessage response = await scoped.GetAsync(
            new Uri($"/api/v1/gyms/{gym}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(gym.ToString(), ResolvedTenant(response));
    }
```

- [ ] **Step 2: Run them**

Run: `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~TenantResolutionTests.AGymScopedGrant"`
Expected: `AGymScopedGrantHoldsNothingOnAPlatformRoute` FAILS (200 — the escalation). `AGymScopedGrantHoldsItsPermissionsOnThatGymsRoutes` PASSES today *through* the escalation; it turns red only if the fix returns platform grants alone, which is what makes it the control. Record both in the report.

- [ ] **Step 3: Abstractions.** Replace the `UserSecuritySnapshot` record, `ICallerSnapshot` and `IIdentityQueries` in `IIdentityQueries.cs` with:

```csharp
/// <summary>What the bearer handler needs on every request: is this still the same account, and is it active.</summary>
public sealed record UserSecurityIdentity(Guid UserId, Guid SecurityStamp, string Status);

/// <summary>
/// The identity plus the permissions the caller holds <em>for the acting tenant</em> —
/// platform grants ∪ grants naming <see cref="TenantId"/> (grants spec D3). With a null tenant,
/// exactly the platform grants.
/// </summary>
public sealed record UserSecuritySnapshot(
    Guid UserId,
    Guid? TenantId,
    Guid SecurityStamp,
    string Status,
    IReadOnlyCollection<string> Permissions);

/// <summary>
/// Per-request memo of the caller's security reads (AD-22). Identity is cached once per user;
/// permissions once per (user, tenant) — so the stamp check during authentication, which knows no
/// tenant yet, never pays for a permission set it does not use, and a permission set computed for one
/// gym can never answer for another (grants spec D4).
/// </summary>
public interface ICallerSnapshot
{
    Task<UserSecurityIdentity?> GetIdentityAsync(Guid userId, CancellationToken cancellationToken);

    Task<UserSecuritySnapshot?> GetAsync(Guid userId, Guid? tenantId, CancellationToken cancellationToken);
}

public interface IIdentityQueries
{
    Task<UserProfile?> GetProfileAsync(Guid userId, CancellationToken cancellationToken);

    Task<UserSecurityIdentity?> GetSecurityIdentityAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Platform grants only. Describes the user, not a request — for sessions, profiles and the user console.</summary>
    Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Platform grants ∪ grants naming <paramref name="tenantId"/>; with null, exactly the platform grants.</summary>
    Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 4: `IdentityQueries`.** Replace `GetSecuritySnapshotAsync` and `GetEffectivePermissionsAsync` (lines 54–100) with:

```csharp
    public async Task<UserSecurityIdentity?> GetSecurityIdentityAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _dbContext.Set<User>()
            .AsNoTracking()
            .Where(candidate => candidate.Id == userId)
            .Select(candidate => new { candidate.SecurityStamp, candidate.Status })
            .SingleOrDefaultAsync(cancellationToken);

        return user is null
            ? null
            : new UserSecurityIdentity(userId, user.SecurityStamp, user.Status.ToString());
    }

    public Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        GetEffectivePermissionsAsync(userId, tenantId: null, cancellationToken);

    public async Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        // Grants spec D3: platform grants (TenantId null) ∪ grants naming the acting tenant. With no
        // acting tenant the second disjunct collapses to `TenantId == null` — exactly the platform set,
        // by construction rather than by a special case. Before H1 this filtered on UserId alone, which
        // would have handed a gym-scoped role's permissions to every route the day such a grant existed.
        return await _dbContext.Set<UserRole>()
            .AsNoTracking()
            .Where(assignment => assignment.UserId == userId
                && (assignment.TenantId == null || assignment.TenantId == tenantId))
            .Join(
                _dbContext.Set<RolePermission>(),
                assignment => assignment.RoleId,
                grant => grant.RoleId,
                (_, grant) => grant.PermissionId)
            .Join(
                _dbContext.Set<Permission>(),
                permissionId => permissionId,
                permission => permission.Id,
                (_, permission) => permission.Key)
            .Distinct()
            .OrderBy(key => key)
            .ToListAsync(cancellationToken);
    }
```

- [ ] **Step 5: `CallerSnapshot` and `PermissionEvaluator`.** Replace both classes in `PermissionEvaluator.cs` (keep the file's existing `///` docs where they still hold; rewrite the ones that mention a single cached snapshot):

```csharp
internal sealed class PermissionEvaluator : IPermissionEvaluator
{
    private readonly ICurrentUser _currentUser;
    private readonly ITenantContext _tenantContext;
    private readonly ICallerSnapshot _snapshot;

    public PermissionEvaluator(ICurrentUser currentUser, ITenantContext tenantContext, ICallerSnapshot snapshot)
    {
        _currentUser = currentUser;
        _tenantContext = tenantContext;
        _snapshot = snapshot;
    }

    public async Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        IReadOnlyCollection<string> held = await GetPermissionsAsync(cancellationToken);
        return held.Contains(permission, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyCollection<string>> GetPermissionsAsync(CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is not { } userId)
        {
            return [];
        }

        // The acting tenant is whatever TenantResolutionMiddleware set — null on every platform route.
        UserSecuritySnapshot? snapshot = await _snapshot.GetAsync(userId, _tenantContext.TenantId, cancellationToken);

        // A suspended account holds nothing, whatever its roles say and whichever gym it acts for.
        // Status is re-read per request, so a suspension takes effect immediately rather than at
        // token expiry.
        return snapshot is null || !string.Equals(snapshot.Status, "Active", StringComparison.Ordinal)
            ? []
            : snapshot.Permissions;
    }
}

internal sealed class CallerSnapshot : ICallerSnapshot
{
    private readonly IIdentityQueries _identityQueries;

    private Guid? _identityFor;
    private UserSecurityIdentity? _identity;

    private (Guid UserId, Guid? TenantId)? _permissionsFor;
    private IReadOnlyCollection<string> _permissions = [];

    public CallerSnapshot(IIdentityQueries identityQueries) => _identityQueries = identityQueries;

    public async Task<UserSecurityIdentity?> GetIdentityAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_identityFor != userId)
        {
            _identity = await _identityQueries.GetSecurityIdentityAsync(userId, cancellationToken);
            _identityFor = userId;
        }

        return _identity;
    }

    public async Task<UserSecuritySnapshot?> GetAsync(Guid userId, Guid? tenantId, CancellationToken cancellationToken)
    {
        UserSecurityIdentity? identity = await GetIdentityAsync(userId, cancellationToken);
        if (identity is null)
        {
            return null;
        }

        // Keyed by (user, tenant), not user: a set computed for gym A must never answer for gym B,
        // even if the pipeline order that makes a second tenant impossible today is changed again.
        if (_permissionsFor != (userId, tenantId))
        {
            _permissions = await _identityQueries.GetEffectivePermissionsAsync(userId, tenantId, cancellationToken);
            _permissionsFor = (userId, tenantId);
        }

        return new UserSecuritySnapshot(userId, tenantId, identity.SecurityStamp, identity.Status, _permissions);
    }
}
```

Add `using GymOS.BuildingBlocks.Application.Abstractions;` if not already present (it is — `ICurrentUser` lives there, and so does `ITenantContext`).

- [ ] **Step 6: The bearer handler reads identity only.** In `AuthenticationExtensions.ValidateSecurityStampAsync`, change `UserSecuritySnapshot? current;` to `UserSecurityIdentity? current;` and `snapshot.GetAsync(userId, …)` to `snapshot.GetIdentityAsync(userId, context.HttpContext.RequestAborted)`. The stamp and status checks below it compile unchanged.

- [ ] **Step 7: The Application fake.** Replace `FakeIdentityQueries` with:

```csharp
internal sealed class FakeIdentityQueries : IIdentityQueries
{
    public Dictionary<Guid, UserProfile> Profiles { get; } = [];

    /// <summary>Platform grants per user.</summary>
    public Dictionary<Guid, IReadOnlyCollection<string>> Permissions { get; } = [];

    /// <summary>Grants naming one tenant, per (user, tenant).</summary>
    public Dictionary<(Guid UserId, Guid TenantId), IReadOnlyCollection<string>> TenantPermissions { get; } = [];

    public Dictionary<Guid, UserSecurityIdentity> Identities { get; } = [];

    public Task<UserProfile?> GetProfileAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Profiles.TryGetValue(userId, out UserProfile? profile) ? profile : null);

    public Task<UserSecurityIdentity?> GetSecurityIdentityAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Identities.TryGetValue(userId, out UserSecurityIdentity? identity) ? identity : null);

    public Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        GetEffectivePermissionsAsync(userId, tenantId: null, cancellationToken);

    public Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        IEnumerable<string> platform = Permissions.TryGetValue(userId, out IReadOnlyCollection<string>? held) ? held : [];
        IEnumerable<string> tenant = tenantId is { } acting
            && TenantPermissions.TryGetValue((userId, acting), out IReadOnlyCollection<string>? scoped)
                ? scoped
                : [];

        return Task.FromResult<IReadOnlyCollection<string>>(
            platform.Union(tenant, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToList());
    }
}
```

Then `grep -rn "Snapshots\|GetSecuritySnapshotAsync\|UserSecuritySnapshot(" tests src --include=*.cs` and fix every remaining reference (expected: none in tests; `Snapshots` was unused).

- [ ] **Step 8: Infrastructure unit tests.** Create `tests/GymOS.Platform.Infrastructure.UnitTests/PermissionEvaluatorTests.cs`:

```csharp
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Infrastructure.Tenancy;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Infrastructure.Identity;
using Xunit;

namespace GymOS.Platform.Infrastructure.UnitTests;

/// <summary>
/// The tenant-aware half of authorization (gym-scoped-grants spec D3/D4). These are the cheap proofs
/// of the shape; the escalation itself is proved end to end in <c>TenantResolutionTests</c>.
/// </summary>
public sealed class PermissionEvaluatorTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GymA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid GymB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task APlatformRouteEvaluatesPlatformGrantsOnly()
    {
        var queries = new RecordingIdentityQueries { Status = "Active" };
        queries.Platform.Add("audit.read");
        queries.ForTenant[GymA] = ["gyms.read"];
        var evaluator = Evaluator(queries, new TenantContext(), out _);

        IReadOnlyCollection<string> held = await evaluator.GetPermissionsAsync(CancellationToken.None);

        Assert.Equal(["audit.read"], held);
        Assert.Equal([(UserId, (Guid?)null)], queries.PermissionReads);
    }

    [Fact]
    public async Task AGymRouteEvaluatesTheUnionForThatGym()
    {
        var queries = new RecordingIdentityQueries { Status = "Active" };
        queries.Platform.Add("audit.read");
        queries.ForTenant[GymA] = ["gyms.read"];
        queries.ForTenant[GymB] = ["billing.read"];
        var tenant = new TenantContext();
        tenant.SetTenant(GymA);
        var evaluator = Evaluator(queries, tenant, out _);

        IReadOnlyCollection<string> held = await evaluator.GetPermissionsAsync(CancellationToken.None);

        Assert.Equal(["audit.read", "gyms.read"], held);
        Assert.DoesNotContain("billing.read", held);
    }

    [Fact]
    public async Task ASuspendedUserHoldsNothingWhateverTheTenant()
    {
        var queries = new RecordingIdentityQueries { Status = "Suspended" };
        queries.Platform.Add("audit.read");
        queries.ForTenant[GymA] = ["gyms.read"];
        var tenant = new TenantContext();
        tenant.SetTenant(GymA);
        var evaluator = Evaluator(queries, tenant, out _);

        Assert.Empty(await evaluator.GetPermissionsAsync(CancellationToken.None));
        Assert.False(await evaluator.HasPermissionAsync("gyms.read", CancellationToken.None));
    }

    [Fact]
    public async Task AnAnonymousCallerHoldsNothingAndReadsNothing()
    {
        var queries = new RecordingIdentityQueries { Status = "Active" };
        var evaluator = new PermissionEvaluator(new StubCurrentUser(null), new TenantContext(), new CallerSnapshot(queries));

        Assert.Empty(await evaluator.GetPermissionsAsync(CancellationToken.None));
        Assert.Equal(0, queries.IdentityReads);
        Assert.Empty(queries.PermissionReads);
    }

    [Fact]
    public async Task TheIdentityIsReadOnceForTheRequest()
    {
        // The bearer handler reads identity during authentication; authorization reads the snapshot
        // later. One user row, not two.
        var queries = new RecordingIdentityQueries { Status = "Active" };
        var snapshot = new CallerSnapshot(queries);

        Assert.NotNull(await snapshot.GetIdentityAsync(UserId, CancellationToken.None));
        Assert.NotNull(await snapshot.GetAsync(UserId, GymA, CancellationToken.None));

        Assert.Equal(1, queries.IdentityReads);
        Assert.Single(queries.PermissionReads);
    }

    [Fact]
    public async Task PermissionsAreCachedPerUserAndTenant()
    {
        var queries = new RecordingIdentityQueries { Status = "Active" };
        var snapshot = new CallerSnapshot(queries);

        await snapshot.GetAsync(UserId, GymA, CancellationToken.None);
        await snapshot.GetAsync(UserId, GymA, CancellationToken.None);
        Assert.Single(queries.PermissionReads);

        // A different tenant in the same request is a different set. It cannot happen through the
        // pipeline today; it is what stops gym A's set answering for gym B if that ever changes.
        await snapshot.GetAsync(UserId, GymB, CancellationToken.None);
        Assert.Equal([(UserId, (Guid?)GymA), (UserId, (Guid?)GymB)], queries.PermissionReads);
    }

    [Fact]
    public async Task AnUnknownUserIsNullWithoutAPermissionRead()
    {
        var queries = new RecordingIdentityQueries { Status = "Active", UserExists = false };
        var snapshot = new CallerSnapshot(queries);

        Assert.Null(await snapshot.GetAsync(UserId, GymA, CancellationToken.None));
        Assert.Empty(queries.PermissionReads);
    }

    private static PermissionEvaluator Evaluator(RecordingIdentityQueries queries, TenantContext tenant, out CallerSnapshot snapshot)
    {
        snapshot = new CallerSnapshot(queries);
        return new PermissionEvaluator(new StubCurrentUser(UserId), tenant, snapshot);
    }

    private sealed class StubCurrentUser : ICurrentUser
    {
        public StubCurrentUser(Guid? userId) => UserId = userId;

        public Guid? UserId { get; }

        public string? Email => UserId is null ? null : "operator@gymos.test";

        public bool IsAuthenticated => UserId is not null;

        public string? IpAddress => "127.0.0.1";

        public string? UserAgent => "xunit";
    }

    private sealed class RecordingIdentityQueries : IIdentityQueries
    {
        public string Status { get; init; } = "Active";

        public bool UserExists { get; init; } = true;

        public HashSet<string> Platform { get; } = new(StringComparer.Ordinal);

        public Dictionary<Guid, string[]> ForTenant { get; } = [];

        public int IdentityReads { get; private set; }

        public List<(Guid UserId, Guid? TenantId)> PermissionReads { get; } = [];

        public Task<UserProfile?> GetProfileAsync(Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<UserSecurityIdentity?> GetSecurityIdentityAsync(Guid userId, CancellationToken cancellationToken)
        {
            IdentityReads++;
            return Task.FromResult(UserExists ? new UserSecurityIdentity(userId, Guid.NewGuid(), Status) : null);
        }

        public Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(Guid userId, CancellationToken cancellationToken) =>
            GetEffectivePermissionsAsync(userId, null, cancellationToken);

        public Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(Guid userId, Guid? tenantId, CancellationToken cancellationToken)
        {
            PermissionReads.Add((userId, tenantId));
            IEnumerable<string> scoped = tenantId is { } acting && ForTenant.TryGetValue(acting, out string[]? held) ? held : [];
            return Task.FromResult<IReadOnlyCollection<string>>(
                Platform.Union(scoped, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToList());
        }
    }
}
```

`ICurrentUser` is `UserId`, `Email`, `IsAuthenticated`, `IpAddress`, `UserAgent` — all covered above.

- [ ] **Step 9: Build and run**

Run: `dotnet build GymOS.Backend.slnx -c Release` then `dotnet test tests/GymOS.Platform.Infrastructure.UnitTests --filter FullyQualifiedName~PermissionEvaluatorTests` then `dotnet test tests/GymOS.Platform.Application.UnitTests` then `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~TenantResolutionTests|FullyQualifiedName~AuthenticationFlowTests|FullyQualifiedName~MaintenanceModeTests|FullyQualifiedName~PlatformAdminApiTests"`
Expected: PASS, all — including both `AGymScopedGrant*` tests.

- [ ] **Step 10: Format and commit**

```bash
dotnet format GymOS.Backend.slnx --verify-no-changes
git add src tests
git commit -m "identity: permissions are evaluated for the acting tenant, and the caller snapshot is keyed by (user, tenant)

Grants spec D3/D4. The bearer handler now reads identity only, so a gym route costs the same two
queries a platform route does. AGymScopedGrantHoldsNothingOnAPlatformRoute was red (200) before this.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 3: `SuperAdminSeeder` under the seeding lock; the lock's doc says what it guarantees

**Files:**
- Modify: `src/Platform/GymOS.Platform.Infrastructure/Persistence/Seeding/SuperAdminSeeder.cs`
- Modify: `src/Platform/GymOS.Platform.Infrastructure/Persistence/Seeding/SeedingLocks.cs`
- Test: `tests/GymOS.IntegrationTests/SuperAdminSeedingTests.cs`

**Interfaces:**
- Consumes: `IUnitOfWork.BeginTransactionAsync` / `SaveChangesAsync`, `IAdvisoryLock.AcquireAsync(name, ct)` (`SqlServerAdvisoryLock`: `sp_getapplock @LockOwner='Transaction'`, throws if no transaction is open), `SeedingLocks.CatalogSeed`. Both services are already registered scoped; `SuperAdminSeeder` is resolved from the startup scope in `Program.cs:275`.

- [ ] **Step 1: Write the failing test.** In `SuperAdminSeedingTests`, after `ASecondStartupWithADifferentPassword_LeavesTheExistingAccountUntouched`:

```csharp
    [Fact]
    public async Task TwoConcurrentStartups_SeedOneSuperAdmin()
    {
        // Two replicas booting against one empty database — the M4.1 close-out review's unprotected
        // check-then-act. Without the lock both pass the exists-check and both insert; the unique index
        // on normalized_email then takes one host down with a bare DbUpdateException instead of the
        // "already exists" log line. Same shape as the identity and catalogue seeders' race tests.
        await using SqlServerFixture.DatabaseLease lease = await _sqlServer.LeaseDatabaseAsync("seed_race");

        await using AuthenticationHost first = AuthenticationHost.Create(lease.ConnectionString);
        await using AuthenticationHost second = AuthenticationHost.Create(lease.ConnectionString);

        // WebApplicationFactory builds the host — and runs the startup scope — on first use.
        await Task.WhenAll(
            Task.Run(() => first.CreateClient().Dispose()),
            Task.Run(() => second.CreateClient().Dispose()));

        await using AsyncServiceScope scope = first.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(await dbContext.Set<User>().ToListAsync());
    }
```

- [ ] **Step 2: Run it**

Run: `dotnet test tests/GymOS.IntegrationTests --filter FullyQualifiedName~SuperAdminSeedingTests.TwoConcurrentStartups_SeedOneSuperAdmin`
Expected: FAIL — either one host throws `DbUpdateException` on start-up, or (if the two starts happen to serialise) PASS. Run it three times; report what you saw. A race that passes by luck is still worth pinning: the assertion is right, and the lock makes it deterministic.

- [ ] **Step 3: The seeder.** Add fields and constructor parameters `IUnitOfWork unitOfWork` and `IAdvisoryLock advisoryLock` (after `AppDbContext dbContext`), `using GymOS.BuildingBlocks.Application.Abstractions;` is already imported. Then in `SeedAsync`, immediately after the pending-migrations guard and before `string email = …`:

```csharp
        // The same lock, transaction and reason as PlatformIdentitySeeder and FeatureCatalogSeeder:
        // the exists-check and the insert below are one atomic step against the same seeder in another
        // replica, so two hosts booting against one empty database produce one account and one
        // "already exists" log line — not a DbUpdateException from ux on normalized_email.
        await using IUnitOfWorkTransaction transaction =
            await _unitOfWork.BeginTransactionAsync(cancellationToken);
        await _advisoryLock.AcquireAsync(SeedingLocks.CatalogSeed, cancellationToken);
```

Replace the tail `_dbContext.Set<User>().Add(user); await _dbContext.SaveChangesAsync(cancellationToken);` with:

```csharp
        _dbContext.Set<User>().Add(user);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
```

Every early `return` between the two now leaves the transaction to dispose uncommitted — nothing was written on those paths, so nothing is lost.

- [ ] **Step 4: The lock's doc.** Replace the whole `SeedingLocks.cs` with:

```csharp
using GymOS.BuildingBlocks.Application.Abstractions;

namespace GymOS.Platform.Infrastructure.Persistence.Seeding;

/// <summary>
/// The <see cref="IAdvisoryLock"/> names the startup-scope seeders serialize on.
///
/// <para>One constant, not one literal string typed identically in each seeder: two copies of a
/// name that must match, with nothing enforcing the match, is a silent-failure mode — an edit to
/// one and not the other stops excluding those seeders from each other with no test failing, and
/// the boot race this lock exists to close comes back unnoticed.</para>
///
/// <para><b>What the lock guarantees, precisely.</b> <c>SqlServerAdvisoryLock</c> takes
/// <c>sp_getapplock</c> with <c>@LockOwner = 'Transaction'</c>: the lock is held by the transaction
/// that acquired it and released when that transaction commits or rolls back. Each seeder opens its
/// own transaction and acquires inside it, so the guarantee is <em>per seeder</em>: one seeder's
/// check-then-insert in one replica is atomic against the same seeder in another replica. It does
/// not span the seeders and it does not cover the whole start-up window — nor does it need to: each
/// seeder is idempotent on its own, and each one's race is only with itself. (An earlier version of
/// this comment claimed a whole-window guarantee; the M4.1 close-out review corrected it.)</para>
/// </summary>
internal static class SeedingLocks
{
    /// <summary>
    /// Shared by <see cref="PlatformIdentitySeeder"/>, <see cref="FeatureCatalogSeeder"/> and
    /// <see cref="SuperAdminSeeder"/>. One name rather than three: the seeders run back to back in one
    /// start-up scope, so a second replica booting at the same moment queues behind whichever seeder
    /// the first replica is in, and each seeder sees the previous one's rows committed — which matters
    /// for <see cref="SuperAdminSeeder"/>, whose insert needs the role the identity seeder wrote.
    /// </summary>
    internal const string CatalogSeed = "platform.catalog-seed";
}
```

- [ ] **Step 5: Run the seeding classes**

Run: `dotnet build GymOS.Backend.slnx -c Release && dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~SuperAdminSeedingTests|FullyQualifiedName~IdentitySeedingTests|FullyQualifiedName~FeatureCatalogSeedingTests|FullyQualifiedName~MigrateOnlyTests"`
Expected: PASS, all; the new test three times in a row.

- [ ] **Step 6: Format and commit**

```bash
dotnet format GymOS.Backend.slnx --verify-no-changes
git add src/Platform/GymOS.Platform.Infrastructure/Persistence/Seeding tests/GymOS.IntegrationTests/SuperAdminSeedingTests.cs
git commit -m "seeding: SuperAdminSeeder takes the seeding lock, and the lock's doc claims only what a transaction-scoped applock gives

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 4: One source for the dashboard's population and billing queries; ordered `mrr`; name-based navigation config

**Files:**
- Create: `src/Platform/GymOS.Platform.Infrastructure/Analytics/PlatformPopulationQueries.cs`
- Modify: `src/Platform/GymOS.Platform.Infrastructure/Persistence/Queries/DashboardQueries.cs:22-56`
- Modify: `src/Platform/GymOS.Platform.Infrastructure/Analytics/DailyStatsCompiler.cs:66-90,137-145`
- Modify: `src/Platform/GymOS.Platform.Infrastructure/Persistence/Configurations/DailyPlatformStatsConfiguration.cs:22-29`
- Test: `tests/GymOS.IntegrationTests/DashboardApiTests.cs`

**Interfaces:**
- Produces (internal to Infrastructure):

```csharp
internal static class PlatformPopulationQueries
{
    public static IQueryable<Gym> LivingGyms(AppDbContext dbContext);
    public static Task<IReadOnlyDictionary<GymStatus, int>> CountLivingGymsByStatusAsync(AppDbContext dbContext, CancellationToken cancellationToken);
    public static Task<IReadOnlyList<MrrContribution>> BillingContributionsAsync(AppDbContext dbContext, CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write the ordering test.** In `DashboardApiTests`, after `TheSummarysMrrIsPerCurrencyAndExcludesTrials`. `PlatformApiHelpers.CreatePlanAsync` prices a plan in EUR only and returns `PlanCreated(Id, RowVersion)`; the GBP price is added with the same request `CreatePlanAsync` itself sends (`PUT /plans/{id}/prices` with `If-Match`):

```csharp
    [Fact]
    public async Task TheSummarysMrrIsOrderedByCurrencyCode()
    {
        // /analytics/revenue sorts each day's currencies ordinally; the live summary did not sort at all,
        // so the two surfaces could disagree on order for the same numbers. GBP is assigned first so an
        // unsorted projection (insertion order) is caught.
        PlatformApiHelpers.PlanCreated plan = await PlatformApiHelpers.CreatePlanAsync(_client, "DASHTWO", amount: 20m);
        await AddPriceAsync(plan, "GBP", 30m);
        Guid gbpGym = await PlatformApiHelpers.CreateGymAsync(_client, "gbp-gym");
        Guid eurGym = await PlatformApiHelpers.CreateGymAsync(_client, "eur-gym");
        await PlatformApiHelpers.AssignAsync(_client, gbpGym, plan.Id, currency: "GBP");
        await PlatformApiHelpers.AssignAsync(_client, eurGym, plan.Id);

        JsonElement mrr = (await SummaryAsync()).GetProperty("mrr");

        Assert.Equal(
            ["EUR", "GBP"],
            mrr.EnumerateArray().Select(entry => entry.GetProperty("currency").GetString()).ToList());
    }

    private async Task AddPriceAsync(PlatformApiHelpers.PlanCreated plan, string currency, decimal amount)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, new Uri($"/api/v1/plans/{plan.Id}/prices", UriKind.Relative))
        {
            Content = JsonContent.Create(new { billingCycle = "Monthly", currency, amount }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{plan.RowVersion}\"");

        using HttpResponseMessage priced = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, priced.StatusCode);
    }
```

`PlatformApiHelpers.PlanCreated` is the nested record `CreatePlanAsync` returns. `AssignAsync` already takes `currency`, and `ResolveTerms` picks the price by the requested currency without consulting the gym, so both gyms can stay EUR.

- [ ] **Step 2: Run it**

Run: `dotnet test tests/GymOS.IntegrationTests --filter FullyQualifiedName~DashboardApiTests.TheSummarysMrrIsOrderedByCurrencyCode`
Expected: FAIL (order `["GBP","EUR"]`) — or PASS if the database happens to return EUR first. Either way, record it; the `OrderBy` is what makes it deterministic.

- [ ] **Step 3: The shared queries.** Create `PlatformPopulationQueries.cs`:

```csharp
using GymOS.Platform.Domain.Analytics;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Analytics;

/// <summary>
/// The reads the live dashboard (<c>DashboardQueries</c>) and the nightly compiler
/// (<see cref="DailyStatsCompiler"/>) must agree on. One definition each, so "living gym" and
/// "paying subscription" cannot drift apart between the number an operator sees now and the number
/// recorded for yesterday — the M5 whole-branch review found both duplicated verbatim.
/// </summary>
internal static class PlatformPopulationQueries
{
    /// <summary>Every gym that is not soft-deleted — the population every count is taken over.</summary>
    public static IQueryable<Gym> LivingGyms(AppDbContext dbContext) =>
        dbContext.Set<Gym>()
            .AsNoTracking()
            .Where(gym => gym.Status != GymStatus.Deleted);

    /// <summary>Living gyms grouped by status, one round trip. A status with no gyms is an absent key.</summary>
    public static async Task<IReadOnlyDictionary<GymStatus, int>> CountLivingGymsByStatusAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken) =>
        await LivingGyms(dbContext)
            .GroupBy(gym => gym.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Status, row => row.Count, cancellationToken);

    /// <summary>
    /// One <see cref="MrrContribution"/> per subscription in a billing status — the input to
    /// <see cref="MonthlyRecurringRevenue.Calculate"/>. Materialised deliberately: MRR is domain
    /// arithmetic (AD-42), and the row count is the number of paying gyms, which is the platform's own
    /// size.
    /// </summary>
    public static async Task<IReadOnlyList<MrrContribution>> BillingContributionsAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.Set<GymSubscription>()
            .AsNoTracking()
            .Where(subscription => SubscriptionStatusTransitions.Billing.Contains(subscription.Status))
            .Select(subscription => new { subscription.Status, subscription.BillingCycle, subscription.Price })
            .ToListAsync(cancellationToken);

        return rows.Select(row => new MrrContribution(row.Status, row.BillingCycle, row.Price)).ToList();
    }
}
```

- [ ] **Step 4: `DashboardQueries.GetSummaryAsync`.** Replace the method body (lines 24–56) with:

```csharp
        // One round trip for the four population counts rather than four — and the same definition of
        // "living" the nightly compiler uses, from the same place.
        IReadOnlyDictionary<GymStatus, int> population =
            await PlatformPopulationQueries.CountLivingGymsByStatusAsync(_dbContext, cancellationToken);
        int Living(GymStatus status) => population.TryGetValue(status, out int count) ? count : 0;

        int liveSubscriptions = await _dbContext.Set<GymSubscription>()
            .AsNoTracking()
            .CountAsync(subscription => SubscriptionStatusTransitions.Live.Contains(subscription.Status), cancellationToken);

        IReadOnlyList<Money> mrr = MonthlyRecurringRevenue.Calculate(
            await PlatformPopulationQueries.BillingContributionsAsync(_dbContext, cancellationToken));

        return new DashboardSummary(
            population.Values.Sum(),
            Living(GymStatus.Active),
            Living(GymStatus.Suspended),
            Living(GymStatus.Provisioning),
            liveSubscriptions,

            // Ordinal by code, the order /analytics/revenue already uses, so the two surfaces agree.
            mrr.OrderBy(amount => amount.Currency.Code, StringComparer.Ordinal)
                .Select(amount => new MoneyDto(amount.Amount, amount.Currency.Code))
                .ToList());
```

Add `using GymOS.Platform.Infrastructure.Analytics;`.

- [ ] **Step 5: `DailyStatsCompiler`.** Replace the `livingGyms` declaration and the `population` query (lines 66–69 and the `Dictionary<GymStatus, int> population = await livingGyms…ToDictionaryAsync(…)` statement) with:

```csharp
        IReadOnlyDictionary<GymStatus, int> population =
            await PlatformPopulationQueries.CountLivingGymsByStatusAsync(_dbContext, cancellationToken);
```

Keep the "two families of count" comment (edit its `livingGyms` mention to `PlatformPopulationQueries.LivingGyms`). Replace the `contributions` query and the `MonthlyRecurringRevenue.Calculate(contributions.Select(...))` call with:

```csharp
        IReadOnlyList<Money> mrr = MonthlyRecurringRevenue.Calculate(
            await PlatformPopulationQueries.BillingContributionsAsync(_dbContext, cancellationToken));
```

Remove now-unused usings (`GymOS.Platform.Domain.Gyms` stays — `GymStatus` is still used).

- [ ] **Step 6: The navigation.** In `DailyPlatformStatsConfiguration.Configure`, delete the `builder.Metadata.FindNavigation(nameof(DailyPlatformStats.Mrr))!.SetPropertyAccessMode(PropertyAccessMode.Field);` statement and add after the `HasMany(...).OnDelete(...)` chain:

```csharp
        // Field access through the navigation builder, after HasMany, so it does not depend on
        // convention discovery having already found the navigation — the `FindNavigation(...)!` this
        // replaces threw NullReferenceException if HasMany had not run first.
        builder.Navigation(stats => stats.Mrr).UsePropertyAccessMode(PropertyAccessMode.Field);
```

- [ ] **Step 7: Prove the model did not change and the numbers did not move**

Run: `dotnet build GymOS.Backend.slnx -c Release && dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~MigrationTests|FullyQualifiedName~DashboardApiTests|FullyQualifiedName~PlatformStatsJobTests|FullyQualifiedName~PlatformStatsPersistenceTests|FullyQualifiedName~AnalyticsApiTests"`
Expected: PASS, all. `MigrationTests` includes the pending-model-changes check; if it reports a model change from Step 6, the navigation configuration is wrong — revert to investigating, do not add a migration.

- [ ] **Step 8: Format and commit**

```bash
dotnet format GymOS.Backend.slnx --verify-no-changes
git add src/Platform/GymOS.Platform.Infrastructure tests/GymOS.IntegrationTests/DashboardApiTests.cs
git commit -m "analytics: one source for the population and billing reads, one round trip for the four counts, mrr in currency order

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 5: Tests that could not fail — analytics positive controls, the null-features fake, JPY, the delete-gym maintainer call

**Files:**
- Modify: `tests/GymOS.IntegrationTests/AnalyticsApiTests.cs`, `tests/GymOS.IntegrationTests/DashboardApiTests.cs`
- Modify: `tests/GymOS.Platform.Application.UnitTests/Fakes/GymFakes.cs:136-140`, `tests/GymOS.Platform.Application.UnitTests/GymTestContext.cs`, `tests/GymOS.Platform.Application.UnitTests/GymManagementTests.cs`
- Modify: `tests/GymOS.Platform.Domain.UnitTests/MonthlyRecurringRevenueTests.cs:82-94`

**Interfaces:**
- Consumes: `PlatformApiHelpers.SignedInAsync(host, adminClient, roleName, email)`; `FakeFeatureQueries(FakeFeatureRepository, FakePlanRepository, FakeSubscriptionRepository)`; `FakeFeatureExceptionMaintainer.CalledForGymIds` / `CalledWithLivePlanIds`; `GymDetail.Features` (`IReadOnlyList<ResolvedFeatureDetail>?`).

- [ ] **Step 1: Analytics positive controls.** In `AnalyticsApiTests`, after `SupportAgentCannotReadTheSeries`:

```csharp
    /// <summary>
    /// The control <c>SupportAgentCannotReadTheSeries</c> lacked: deleting the <c>analytics.read</c>
    /// grant from any of these roles left every M5 test green, because nothing asserted a role that
    /// holds it gets through. SuperAdmin is not a control — it grants everything and proves nothing
    /// about the row.
    /// </summary>
    [Theory]
    [InlineData("PlatformAdmin")]
    [InlineData("BillingAdmin")]
    [InlineData("ReadOnly")]
    public async Task ARoleHoldingAnalyticsReadCanReadTheSeries(string role)
    {
        using HttpClient holder = await PlatformApiHelpers.SignedInAsync(
            _host, _client, role, $"{role.ToLowerInvariant()}.series@gymos.test");

        using HttpResponseMessage response = await holder.GetAsync(
            new Uri("/api/v1/analytics/churn?from=2026-08-01&to=2026-08-01", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
```

In `DashboardApiTests`, after `SupportAgentIsRefusedTheDashboardButKeepsTheAuditFeed`, the same Theory named `ARoleHoldingAnalyticsReadCanReadTheSummary`, GETting `Summary` and using emails `$"{role.ToLowerInvariant()}.summary@gymos.test"`.

- [ ] **Step 2: Prove they bite.** Temporarily delete `PlatformPermissions.AnalyticsRead,` from the `ReadOnly` role in `src/Platform/GymOS.Platform.Domain/Identity/SystemRoles.cs`; run `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~ARoleHoldingAnalyticsRead"`; expect the two `ReadOnly` cases to FAIL (403). Restore the line (`git checkout -- src/Platform/GymOS.Platform.Domain/Identity/SystemRoles.cs`); re-run; PASS.

- [ ] **Step 3: The fake stops lying.** In `GymFakes.GetAsync`, replace

```csharp
        IReadOnlyList<ResolvedFeatureDetail>? features = !includeFeatures || FeatureQueries is null
            ? null
            : await FeatureQueries.ResolveForGymAsync(gymId, cancellationToken);
```

with

```csharp
        // The real projection always resolves features when asked. Returning null here instead was a
        // shape production never produces, and it let the features.read gate on every gym write route go
        // unexercised (M4.1 close-out): wire the feature fake or do not ask.
        IReadOnlyList<ResolvedFeatureDetail>? features = null;
        if (includeFeatures)
        {
            IFeatureQueries featureQueries = FeatureQueries ?? throw new InvalidOperationException(
                "The test asked GymDetail to include features but wired no IFeatureQueries fake. " +
                "Call GymTestContext.WithFeatureCatalogue() or set FakeGymQueries.FeatureQueries.");
            features = await featureQueries.ResolveForGymAsync(gymId, cancellationToken);
        }
```

Update the `FeatureQueries` property's doc: "Left null in tests that never grant `features.read`; a test that does must wire it, or the fake throws."

- [ ] **Step 4: `GymTestContext.WithFeatureCatalogue()`.** Add two fakes and a fluent method:

```csharp
    public FakeFeatureRepository FeatureCatalogue { get; } = new();

    public FakePlanRepository Plans { get; } = new();

    /// <summary>
    /// Wires the feature fake so a caller holding <c>features.read</c> gets features back on a write
    /// route's read-back. Without this, granting <c>features.read</c> makes <see cref="FakeGymQueries"/>
    /// throw — on purpose.
    /// </summary>
    public GymTestContext WithFeatureCatalogue()
    {
        GymQueries.FeatureQueries = new FakeFeatureQueries(FeatureCatalogue, Plans, Subscriptions);
        return this;
    }
```

- [ ] **Step 5: The six write routes, both ways.** In `GymManagementTests`, add a region `// ── The features.read gate on write routes ──` with:

```csharp
    private const string FeaturesRead = PlatformPermissions.FeaturesRead;

    public static TheoryData<string, Func<GymTestContext, Gym, ICommand<Result<GymDetail>>>> WriteRoutes() => new()
    {
        { "provision", (_, _) => Provision("features-gym") },
        { "update", (_, gym) => new UpdateGymCommand(gym.Id, "Acme Fitness", "Grace", "grace@acme.test", null, "GR", "Europe/Athens", "EUR", "1") },
        { "activate", (context, _) => new ActivateGymCommand(context.GivenProvisioningGym("activate-gym").Id, null) },
        { "suspend", (_, gym) => new SuspendGymCommand(gym.Id, "Non-payment", null) },
        { "reactivate", (context, _) => new ReactivateGymCommand(context.GivenSuspendedGym("reactivate-gym").Id, null) },
        { "branding", (_, gym) => new UpdateGymBrandingCommand(gym.Id, "logo.png", null, "#1A2B3C", null, null, null, "1") },
    };

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task AWriterHoldingFeaturesRead_GetsTheResolvedFeaturesBack(
        string route, Func<GymTestContext, Gym, ICommand<Result<GymDetail>>> command)
    {
        // GymWrites.ReadBackAsync asks IPermissionEvaluator for features.read and includes the resolved
        // catalogue only when it is held. Until H1 the fake returned null whether or not it was asked,
        // so this branch was never exercised on any write route.
        var context = new GymTestContext().ActingAs(Create, Update, Suspend, FeaturesRead).WithFeatureCatalogue();
        Gym gym = context.GivenActiveGym();

        Result<GymDetail> result = await context.Dispatcher.SendAsync(command(context, gym), CancellationToken.None);

        Assert.True(result.IsSuccess, route);
        Assert.NotNull(result.Value.Features);
    }

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task AWriterWithoutFeaturesRead_GetsNullFeatures(
        string route, Func<GymTestContext, Gym, ICommand<Result<GymDetail>>> command)
    {
        // The other half of the gate: no features.read, no features — and the fake would throw if the
        // handler asked for them anyway, so a null here is a real null.
        var context = new GymTestContext().ActingAs(Create, Update, Suspend);
        Gym gym = context.GivenActiveGym();

        Result<GymDetail> result = await context.Dispatcher.SendAsync(command(context, gym), CancellationToken.None);

        Assert.True(result.IsSuccess, route);
        Assert.Null(result.Value.Features);
    }
```

`ICommand<T>` is `GymOS.BuildingBlocks.Application.Messaging.ICommand<TResponse>` (already imported). `IDispatcher.SendAsync` is generic over the response; if its signature requires the concrete command type rather than `ICommand<Result<GymDetail>>`, read `IDispatcher` and adapt the delegate type — the six routes and the two assertions are the requirement, not the exact generic shape.

- [ ] **Step 6: JPY.** Replace `AZeroDecimalCurrencyRoundsToWholeUnits` with:

```csharp
    [Fact]
    public void AZeroDecimalCurrencyRoundsToWholeUnits()
    {
        // JPY has no minor units. 12,005 a year is not divisible by 12 (1,000.4166…), so a normalisation
        // that kept hundredths, or rounded to them, produces an amount Money refuses to hold for JPY.
        // The M5 fixture used 12,000, which divides exactly and so could not fail.
        IReadOnlyList<Money> mrr = MonthlyRecurringRevenue.Calculate(
            [new MrrContribution(SubscriptionStatus.Active, BillingCycle.Yearly, Money.Of(12_005m, Currency.Of("JPY")))]);

        Assert.Equal(Money.Of(1_000m, Currency.Of("JPY")), Assert.Single(mrr));
    }

    [Fact]
    public void AZeroDecimalCurrencyRoundsHalfEvenToTheWholeUnit()
    {
        // 12,006 / 12 = 1,000.5 exactly. Half-even lands on 1,000; away-from-zero would say 1,001. Pins
        // the direction MonthlyRecurringRevenue documents.
        IReadOnlyList<Money> mrr = MonthlyRecurringRevenue.Calculate(
            [new MrrContribution(SubscriptionStatus.Active, BillingCycle.Yearly, Money.Of(12_006m, Currency.Of("JPY")))]);

        Assert.Equal(Money.Of(1_000m, Currency.Of("JPY")), Assert.Single(mrr));
    }
```

- [ ] **Step 7: The delete-gym unit test.** In `GymManagementTests`, after `DeletingAGym_EndsTheSubscriptionItWasPayingFor`:

```csharp
    [Fact]
    public async Task DeletingAGym_AsksTheMaintainerToWithdrawWhatThePlanMadeRedundant()
    {
        var context = new GymTestContext().ActingAs(Delete);
        Gym gym = context.GivenActiveGym();

        Result result = await context.Dispatcher.SendAsync(
            new DeleteGymCommand(gym.Id, "acme-gym", null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        // With no live plan after deletion — so every override is judged against the defaults alone.
        // GymFeatureLifecycleTests sees the outcome; this pins that the handler asked, once, correctly.
        Assert.Equal(gym.Id, Assert.Single(context.FeatureExceptions.CalledForGymIds));
        Assert.Null(Assert.Single(context.FeatureExceptions.CalledWithLivePlanIds));
    }
```

- [ ] **Step 8: Run**

Run: `dotnet test tests/GymOS.Platform.Application.UnitTests && dotnet test tests/GymOS.Platform.Domain.UnitTests && dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~AnalyticsApiTests|FullyQualifiedName~DashboardApiTests"`
Expected: PASS, all. If an existing Application test now throws from the fake (it granted `features.read` without wiring the catalogue), add `.WithFeatureCatalogue()` to that test — that is the fake doing its job.

- [ ] **Step 9: Format and commit**

```bash
dotnet format GymOS.Backend.slnx --verify-no-changes
git add tests
git commit -m "tests: the assertions their names promised — analytics positive controls, a features fake that cannot lie, a JPY fixture that can fail, the delete-gym maintainer call

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 6: A transaction and a locked plan read in `AssignSubscriptionCommandHandler`; `DiscardTrackedChanges` banned outside its one site

**Files:**
- Modify: `src/Platform/GymOS.Platform.Application/Subscriptions/SubscriptionCommands.cs:127,280-290`
- Modify: `tests/GymOS.Platform.Application.UnitTests/Fakes/SubscriptionFakes.cs:28-29`, `tests/GymOS.Platform.Application.UnitTests/SubscriptionAssignmentTests.cs`
- Modify: `BannedSymbols.txt`, `src/Platform/GymOS.Platform.Application/Features/GymFeatureCommands.cs:193`, `src/BuildingBlocks/GymOS.BuildingBlocks.Application/Abstractions/IUnitOfWork.cs:41`

**Interfaces:**
- Consumes: `IPlanRepository.FindByIdForUpdateAsync`, `IUnitOfWork.BeginTransactionAsync`, `FakeUnitOfWork.TransactionCount`.
- Produces: `FakePlanRepository.LockedReads` (`List<Guid>`).

- [ ] **Step 1: Record locked reads in the fake.** In `SubscriptionFakes.FakePlanRepository`, add `public List<Guid> LockedReads { get; } = [];` and change `FindByIdForUpdateAsync` to:

```csharp
    public Task<SubscriptionPlan?> FindByIdForUpdateAsync(Guid planId, CancellationToken cancellationToken)
    {
        LockedReads.Add(planId); // in-memory: no rows, no locks — but a test can see the intent
        return FindByIdAsync(planId, cancellationToken);
    }
```

- [ ] **Step 2: Write the failing test.** In `SubscriptionAssignmentTests`, after `AssigningAPlan_StartsASubscriptionAtTheAgreedTerms`:

```csharp
    [Fact]
    public async Task AssigningAPlan_DecidesRedundancyAgainstALockedPlanInsideOneTransaction()
    {
        // What counts as a "redundant" override is decided against this plan's features, and the
        // withdraw runs in the same use case — so the plan is read under UPDLOCK inside a transaction
        // that spans both (the SetGymFeatures precedent). An unlocked read let a concurrent plan feature
        // edit change the answer between the read and the withdraw (M5 whole-branch review).
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        SubscriptionPlan plan = context.GivenPlan(amount: 49.99m);

        Result<SubscriptionDetail> result = await context.Dispatcher.SendAsync(
            new AssignSubscriptionCommand(gym.Id, plan.Id, "Monthly", "EUR", null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, context.UnitOfWork.TransactionCount);
        Assert.Equal(plan.Id, Assert.Single(context.Plans.LockedReads));
        Assert.Equal(plan.Id, Assert.Single(context.FeatureExceptions.CalledWithLivePlanIds));
    }
```

Run: `dotnet test tests/GymOS.Platform.Application.UnitTests --filter FullyQualifiedName~AssigningAPlan_DecidesRedundancyAgainstALockedPlanInsideOneTransaction`
Expected: FAIL (`TransactionCount` 0, `LockedReads` empty).

- [ ] **Step 3: The handler.** In `AssignSubscriptionCommandHandler.HandleAsync`, replace

```csharp
        SubscriptionPlan? plan = await _plans.FindByIdAsync(command.PlanId, cancellationToken);
```

with

```csharp
        // One transaction from the plan read to the save: WithdrawRedundantAsync below decides what is
        // "redundant" against this plan's features, so the plan is read under UPDLOCK and a concurrent
        // plan feature edit waits until this commits (the SetGymFeaturesCommandHandler precedent). Every
        // early return below disposes the transaction uncommitted — nothing has been written by then.
        await using IUnitOfWorkTransaction transaction =
            await _unitOfWork.BeginTransactionAsync(cancellationToken);

        SubscriptionPlan? plan = await _plans.FindByIdForUpdateAsync(command.PlanId, cancellationToken);
```

and, after the `await _unitOfWork.SaveChangesAsync(cancellationToken);` that follows the `WithdrawRedundantAsync` call, add `await transaction.CommitAsync(cancellationToken);` before the `ReadBackAsync` return. Run the Step 2 test: PASS.

- [ ] **Step 4: Ban `DiscardTrackedChanges`.** Append to `BannedSymbols.txt`:

```
M:GymOS.BuildingBlocks.Application.Abstractions.IUnitOfWork.DiscardTrackedChanges;Clears the whole scope's change tracker, including rows AuditTrail and other components staged — only SetGymFeaturesCommandHandler's retry may call it (GymFeatureCommands.cs); see IUnitOfWork
```

At `GymFeatureCommands.cs:193`, wrap the call on the `SystemClock` precedent:

```csharp
#pragma warning disable RS0030 // Banned API: the one sanctioned call site — the retry must start from committed state, see the comment above and BannedSymbols.txt.
            _unitOfWork.DiscardTrackedChanges();
#pragma warning restore RS0030
```

Add to the `DiscardTrackedChanges` XML doc in `IUnitOfWork.cs`: `/// <para><b>Banned outside <c>SetGymFeaturesCommandHandler</c>'s retry</b> (BannedSymbols.txt, RS0030): it clears every tracked entity in the scope, not only the caller's, so any other use would silently drop rows another component staged into the same context.</para>`

Build: `dotnet build GymOS.Backend.slnx -c Release`. If RS0030 fires on the implementations (`UnitOfWork.DiscardTrackedChanges` in Infrastructure, `FakeUnitOfWork` in tests) — it should not, implementing a member is not a reference to it — suppress each with the same pragma pair and the reason `implements the interface member; the ban is on callers`.

- [ ] **Step 5: Run**

Run: `dotnet test tests/GymOS.Platform.Application.UnitTests && dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~SubscriptionApiTests|FullyQualifiedName~GymFeatureLifecycleTests|FullyQualifiedName~GymFeatureApiTests|FullyQualifiedName~InvoiceApiTests"`
Expected: PASS, all.

- [ ] **Step 6: Format and commit**

```bash
dotnet format GymOS.Backend.slnx --verify-no-changes
git add BannedSymbols.txt src tests
git commit -m "subscriptions: assign reads the plan under UPDLOCK inside one transaction; DiscardTrackedChanges is banned outside its one retry

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 7: API-reference launcher — an injectable container check, an honest `Process.Start` null, an honest doc

**Files:**
- Modify: `src/Host/GymOS.WebApi/Extensions/OpenApiExtensions.cs:110-145`
- Modify: `src/Host/GymOS.WebApi/Extensions/ApiReferenceBrowserLauncher.cs:72-92`
- Test: `tests/GymOS.IntegrationTests/ApiReferenceBrowserRegistrationTests.cs`

**Interfaces:**
- Produces: `AddGymOSApiReferenceBrowser(this IServiceCollection, IConfiguration, IHostEnvironment, Func<string, string?>? readEnvironmentVariable = null)`. `Program.cs:160` compiles unchanged.

- [ ] **Step 1: Write the failing tests.** In `ApiReferenceBrowserRegistrationTests`, add after `AnExplicitTrue_OverridesTheEnvironmentDefault`:

```csharp
    /// <summary>
    /// The one deployment where opening a browser is not merely useless but confusing. Set by the
    /// official .NET base images; read through a seam so this test does not depend on where CI runs.
    /// </summary>
    [Fact]
    public void InsideAContainer_NothingIsRegisteredByDefault() =>
        Assert.False(IsRegistered(Environments.Development, inContainer: true));

    [Fact]
    public void InsideAContainer_AnExplicitTrueStillWins() =>
        Assert.True(IsRegistered(
            Environments.Development,
            settings: new Dictionary<string, string?> { ["OpenApi:LaunchBrowser"] = "true" },
            inContainer: true));
```

Change `IsRegistered` to `private static bool IsRegistered(string environment, Dictionary<string, string?>? settings = null, bool inContainer = false)` and its registration call to:

```csharp
        services.AddGymOSApiReferenceBrowser(
            configuration,
            new StubEnvironment(environment),
            readEnvironmentVariable: name =>
                string.Equals(name, "DOTNET_RUNNING_IN_CONTAINER", StringComparison.Ordinal) && inContainer ? "true" : null);
```

Fix the doc comment on `AnExplicitTrue_OverridesTheEnvironmentDefault`: it says an explicit `true` "cannot rescue a containerised host" — the code does the opposite (the container check feeds the default only), and the new test proves it. Rewrite to: "The override works in both directions, including inside a container: the three guards decide the default, and an explicit setting replaces the default."

Run: `dotnet test tests/GymOS.IntegrationTests --filter FullyQualifiedName~ApiReferenceBrowserRegistrationTests`
Expected: compile error (no `readEnvironmentVariable` parameter).

- [ ] **Step 2: The seam and the doc.** In `OpenApiExtensions.AddGymOSApiReferenceBrowser`, add the fourth parameter `Func<string, string?>? readEnvironmentVariable = null`, and change the container check to:

```csharp
        readEnvironmentVariable ??= Environment.GetEnvironmentVariable;

        // Set by the official .NET base images; the check costs nothing and removes the one
        // deployment where opening a browser is not merely useless but confusing. Read through the
        // seam so the decision is testable wherever the tests themselves happen to run.
        bool inContainer = string.Equals(
            readEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);
```

Replace the method's second `<para>` with:

```csharp
    /// <para>Three guards decide the <em>default</em>: the reference has to be published, the
    /// environment has to be Development, and the process must not be containerised
    /// (<c>DOTNET_RUNNING_IN_CONTAINER</c>). An explicit <c>OpenApi:LaunchBrowser</c> replaces that
    /// default in both directions — that is the switch a developer flips on purpose, including inside
    /// a container they have arranged a browser for. When the answer is "no", nothing is registered,
    /// so there is nothing that can misfire.</para>
```

- [ ] **Step 3: The null process.** In `ApiReferenceBrowserLauncher.Launch`, replace

```csharp
            using Process? browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            LogOpened(_logger, url);
```

with

```csharp
            using Process? browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            if (browser is null)
            {
                // With UseShellExecute, null means the OS handed the URL to a handler that was already
                // running — the usual case on Windows when a browser is open — or that nothing accepted
                // it. Neither is "opened"; say what is actually known.
                LogHandedToExistingHandler(_logger, url);
                return;
            }

            LogOpened(_logger, url);
```

and add beside the other messages:

```csharp
    [LoggerMessage(EventId = 6103, Level = LogLevel.Information, Message = "API reference at {Url} was handed to the default handler; no new browser process was started")]
    private static partial void LogHandedToExistingHandler(ILogger logger, string url);
```

- [ ] **Step 4: Run**

Run: `dotnet build GymOS.Backend.slnx -c Release && dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~ApiReferenceBrowserRegistrationTests|FullyQualifiedName~OpenApiExposureTests"`
Expected: PASS, all seven registration tests.

- [ ] **Step 5: Format and commit**

```bash
dotnet format GymOS.Backend.slnx --verify-no-changes
git add src/Host/GymOS.WebApi/Extensions tests/GymOS.IntegrationTests/ApiReferenceBrowserRegistrationTests.cs
git commit -m "openapi: the browser launcher's container check is injectable, a null Process.Start is not 'opened', and the doc says what the override does

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 8: Small items — withdrawn-count logging, `StoredException` suppression, nullable `EntityType`, loopback ports

**Files:**
- Create: `src/Platform/GymOS.Platform.Application/Features/FeatureExceptionLogging.cs`
- Modify: `src/Platform/GymOS.Platform.Application/Gyms/GymLifecycleCommands.cs` (`DeleteGymCommandHandler`), `src/Platform/GymOS.Platform.Application/Subscriptions/SubscriptionCommands.cs` (`AssignSubscriptionCommandHandler`, `SubscriptionStateCommandHandlers`), `src/Platform/GymOS.Platform.Application/Subscriptions/SubscriptionRenewalService.cs`, `tests/GymOS.IntegrationTests/GymFeatureLifecycleTests.cs:359-364`
- Modify: `src/Platform/GymOS.Platform.Domain/Features/GymFeatureExceptions.cs:19`, `.editorconfig:37-39`
- Modify: `src/Platform/GymOS.Platform.Application/Analytics/Abstractions/IDashboardQueries.cs:23-24`, `src/Platform/GymOS.Platform.Infrastructure/Persistence/Queries/DashboardQueries.cs:66-72`, `contracts/openapi-v1.json` (regenerated)
- Modify: `docker-compose.override.yml` (every `ports:` entry), `docs/Infrastructure/DOCKER.md` (one paragraph)

**Interfaces:**
- Produces: `FeatureExceptionLogging.LogOverridesWithdrawn(ILogger, int count, Guid gymId, string cause)`; `ActivityEntry(DateTimeOffset OccurredAtUtc, string Action, string? EntityType, string? UserEmail)`.

- [ ] **Step 1: The shared log message.** Create `FeatureExceptionLogging.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace GymOS.Platform.Application.Features;

/// <summary>
/// The one line every synchronous caller of <c>IFeatureExceptionMaintainer.WithdrawRedundantAsync</c>
/// writes when it withdrew something. Four call sites discarded the count entirely (M5 review); a
/// withdrawal is an operator-visible change to what a gym can do, and it should be findable in the log
/// by gym id and by what caused it.
/// </summary>
internal static partial class FeatureExceptionLogging
{
    [LoggerMessage(Level = LogLevel.Information, Message =
        "Withdrew {Count} feature override(s) for gym {GymId} made redundant by {Cause}.")]
    public static partial void LogOverridesWithdrawn(ILogger logger, int count, Guid gymId, string cause);
}
```

- [ ] **Step 2: The four callers.** Each handler gains an `ILogger<T>` constructor parameter and field (unit-test contexts already register `ILogger<>` → `NullLogger<>`; the DI container in production provides it). At each site, capture the count and log when positive:

`DeleteGymCommandHandler` (`GymLifecycleCommands.cs:310`):
```csharp
        int withdrawn = await _featureExceptions.WithdrawRedundantAsync(command.GymId, null, cancellationToken);
        if (withdrawn > 0)
        {
            FeatureExceptionLogging.LogOverridesWithdrawn(_logger, withdrawn, command.GymId, "gym deletion");
        }
```
`AssignSubscriptionCommandHandler` (`SubscriptionCommands.cs:280`): cause `"subscription assignment"`.
`SubscriptionStateCommandHandlers` (`SubscriptionCommands.cs:532`): cause `"subscription state change"`.
`SubscriptionRenewalService` (line 206): the count is already captured; add the log line inside the existing `if (withdrawn > 0)` before the save, cause `"renewal"`. This class is constructed by hand in `GymFeatureLifecycleTests.cs:359` — add `NullLogger<SubscriptionRenewalService>.Instance` as the new last argument there (`using Microsoft.Extensions.Logging.Abstractions;`).

- [ ] **Step 3: `StoredException`.** Delete the `[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:…")]` line in `GymFeatureExceptions.cs`. In `.editorconfig` replace lines 37–39 with:

```
# CA1711 reserves the 'Permission' and 'Exception' suffixes for System.Security / System.Exception
# types; in GymOS, Permission is the ubiquitous-language domain concept (AD-5/AD-22), and
# GymFeatureExceptions.StoredException is a stored feature exception (an override), not a throwable.
dotnet_code_quality.CA1711.allowed_suffixes = Permission|Exception
```

- [ ] **Step 4: `ActivityEntry.EntityType`.** In `IDashboardQueries.cs`, change the record to `string? EntityType`. In `DashboardQueries.GetRecentActivityAsync`, replace the `log.EntityType ?? string.Empty,` line and its two-line comment with `log.EntityType,` and the comment `// Null for explicit audit entries (a failed sign-in, for instance) — the same shape AuditLogQueries projects, so the feed and the log agree.` Regenerate the snapshot:

Run: `UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/GymOS.IntegrationTests --filter FullyQualifiedName~OpenApiContractTests` (PowerShell: `$env:UPDATE_OPENAPI_SNAPSHOT='1'; dotnet test …; Remove-Item Env:UPDATE_OPENAPI_SNAPSHOT`), then `git diff --stat contracts/openapi-v1.json` and `git diff contracts/openapi-v1.json`: the only change is `ActivityEntry.entityType` gaining `"null"` in its type array. Anything else in the diff is a defect to investigate, not to commit.

- [ ] **Step 5: Loopback ports.** In `docker-compose.override.yml`, prefix every published port with `127.0.0.1:` — the six `ports:` entries at lines ~40 (`'127.0.0.1:${MSSQL_PORT:-1433}:1433'`), ~142 (api), ~201–202 (mailpit), ~223 (redis), ~231–232 (seq), ~241–242 (minio) — and add above the first one:

```yaml
    # Every published port in this overlay binds to loopback. The credentials in this file are public,
    # so a dev stack reachable from the LAN is a dev stack anyone on the LAN can sign in to. A developer
    # who needs another machine to reach a service overrides that one line in a docker-compose.local.yml
    # (`- '0.0.0.0:5080:8080'`) rather than widening it for everyone.
```

In `DOCKER.md`, in the row for `docker-compose.override.yml` (line 61), change "published ports" to "ports published on loopback only", and add one sentence after the table: "Every development port binds to `127.0.0.1`; to reach a service from another machine, override that one mapping in a `docker-compose.local.yml` and pass it with `-f` — do not widen the committed overlay."

- [ ] **Step 6: Run**

Run: `dotnet build GymOS.Backend.slnx -c Release && dotnet test tests/GymOS.Platform.Application.UnitTests && dotnet test tests/GymOS.Platform.Domain.UnitTests && dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~OpenApiContractTests|FullyQualifiedName~DashboardApiTests|FullyQualifiedName~GymFeatureLifecycleTests|FullyQualifiedName~RenewalSweepTests"` and `docker compose config --quiet`
Expected: PASS, all; compose config valid.

- [ ] **Step 7: Format and commit**

```bash
dotnet format GymOS.Backend.slnx --verify-no-changes
git add .editorconfig docker-compose.override.yml contracts/openapi-v1.json src tests docs/Infrastructure/DOCKER.md
git commit -m "housekeeping: log withdrawn overrides, allow the Exception suffix by rule not by attribute, a nullable EntityType on the activity feed, dev ports on loopback

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 9: Docs that say what the code does, the full suite, STATUS

**Files:**
- Modify: `docs/BusinessRules/Analytics.md` §11 (lines 298–336), `docs/BusinessRules/TenantIsolation.md` (lines 313–328), `docs/Infrastructure/DOCKER.md` (line 30 diagram; collation paragraph ~line 80), `src/BuildingBlocks/GymOS.BuildingBlocks.Infrastructure/Idempotency/IdempotencyRecordConfiguration.cs:7-12`, `src/Platform/GymOS.Platform.Application/Analytics/Abstractions/IDashboardHealthCheck` doc (in `IDashboardQueries.cs:38-48`), `docs/superpowers/specs/2026-08-15-gym-scoped-grants-design.md` §6, `docs/RELEASE-NOTES.md`, `docs/STATUS.md`

- [ ] **Step 1: Full suite once**: `dotnet test GymOS.Backend.slnx` — record the per-project totals for STATUS (M7's were 287/131/453/92/107/153/375 = 1,598; expect roughly +25).

- [ ] **Step 2: Analytics.md §11.** Replace the two limitation entries (from "**An expiry recorded late by `RenewalJob`…**" through the end of the "different clocks" paragraph) with:

> **Both churn columns key on when the event was recorded — and did before this document said so.** `expired_gyms` counts subscriptions whose `ExpiredAtUtc` falls inside the day, exactly as `canceled_gyms` counts `CanceledAtUtc`: `RenewalJob` stamps `ExpiredAtUtc` when it applies the expiry (`7672bb6`, 2026-08-14; `AnExpiryIsCountedOnTheDayItWasApplied_NotTheDayThePeriodEnded` pins it), and both columns have filtered indexes. Earlier versions of this section described `expired_gyms` as keyed on `CurrentPeriodEndUtc` and called an `ExpiredAtUtc` column "the real fix"; that fix had shipped, and the section was not updated until H1 (2026-09-19).
>
> **What that definition means.** A subscription whose period ends while the sweep is disabled (`Jobs:Enabled=false` for a deploy window) is counted as churn on the day the sweep catches up and records it, not on the day the period ended. That is the stated definition — churn is dated by when the platform recorded it — not a defect; it is also what makes a stats row safe to write once, because the recording moment cannot be earlier than the row that would count it.

- [ ] **Step 3: TenantIsolation.md.** In the bullet beginning "**No gym-scoped grant can be issued through the API yet…**" (line 313), after the sentence ending "always `RoleScope.Platform`.", replace "**Designed, not built:** …" through "The Members increment builds it." with:

> **Steps 1–2 built in H1 (2026-09-19):** `TenantResolutionMiddleware` runs between `UseAuthentication()` and `UseAuthorization()` and lets anonymous callers through to the challenge; `IIdentityQueries.GetEffectivePermissionsAsync(userId, tenantId)` returns platform grants ∪ grants naming the acting tenant; `CallerSnapshot` caches identity per user and permissions per (user, tenant). `TenantResolutionTests` proves a SupportAgent grant scoped to one gym holds nothing on `/audit-logs` and `gyms.read` on that gym only. Steps 3–5 (per-gym roles, `SetGymUserRolesCommand`) remain designed, not built — `docs/superpowers/specs/2026-08-15-gym-scoped-grants-design.md` — and belong to Members.

Also correct the earlier sentence in that bullet "and `TenantResolutionMiddleware` runs after `UseAuthorization()`, so …" to past tense ("ran … until H1").

- [ ] **Step 4: DOCKER.md.** Diagram line 30: move `mailpit` out of the "profiles (off by default)" line — it becomes `│   ║   profiles (off by default):  redis · seq · minio      ║` and a new line above it `│   ║   always on (since ADR-53):    mailpit                 ║` (keep the box's column alignment; count characters). In the collation paragraph (~line 80), append: "The fifth behavioural difference from PostgreSQL, undocumented until H1: the client-supplied `idempotency_key` compares case-insensitively, so two keys differing only in case are one key here and were two under `lc-collate=C`. Negligible for random keys; worth knowing before choosing a key format."

- [ ] **Step 5: Two code comments.** `IdempotencyRecordConfiguration` summary → "Maps platform.idempotency_records. The unique claim index is declared in raw SQL in `20260823193131_RuntimeDatabaseProgrammability` (see the comment at its `CREATE UNIQUE INDEX`), not here: EF can express it, but SQL Server treats NULLs as equal in a unique index, so a plain index over the nullable tenant_id already makes two platform-scope claims with one key collide — the sentinel the PostgreSQL version needed is gone." `IDashboardHealthCheck` remarks: replace "— the one place that boundary is allowed to be crossed —" with "— one of the two places the host wires framework types into Application seams, the other being the health-check registration itself —".

- [ ] **Step 6: Grants spec §6.** Append to `2026-08-15-gym-scoped-grants-design.md` §6: "**2026-09-19 (H1):** steps 1 and 2 built on `development`; the `UseRouting` claim is confirmed by `TenantResolutionTests.ARouteWithAGymIdSetsThatTenantForTheRequest` passing with the middleware in its new position, without an explicit `UseRouting()` call. Test 5's status changed on inspection: anonymous gym routes were already 401 by accident of order; the early return is what keeps them 401 after the reorder."

- [ ] **Step 7: Release notes.** Above `## M7 — Settings & Hardening` add `## H1 — Hardening` (suggested version `0.9.1`, status complete) with four short paragraphs: tenant resolution before authorization and tenant-aware permissions (what a gym-scoped grant now means, what it does not yet — no API issues one); the seeder race and the shared analytics reads; the tests that could not fail, by name; the small items (dev ports on loopback is the one operators notice — the override recipe in one line). One "Corrected in the documents" paragraph naming the churn finding that was already fixed.

- [ ] **Step 8: STATUS.md** — do all of these:
  1. Header paragraph (line 3) and the resume section: "**H1 — Hardening — is implemented at the head of `development`** (commits `<first>..<last>`, nine, against `docs/superpowers/specs/2026-09-19-h1-hardening-design.md`)", the suite total from Step 1, and **next**: the owed list that remains — the maintainer's unlocked plan read (call-site audit), value-vs-intent (owner), GET /features subqueries (pinned), the plan-edit race hook, the UTC-midnight `IClock` seam, dependency majors, M6's constructor-throw follow-up, the integration teardown cost, Key Vault and the other owner items.
  2. Add `### H1 — what landed` after `### M7 — what landed` (line 346): the file map in prose, the two facts the design changed on inspection (the bearer handler's snapshot read; test 5 already 401), and every deviation the ledger records.
  3. Rewrite lines 3932–3951 (the two churn paragraphs) as one paragraph: **resolved at `7672bb6` (2026-08-14)** — the finding was recorded after the fix had landed; the doc was corrected in H1.
  4. Line 3610's "`canceled_at_utc` is in no index at all" → "(`canceled_at_utc` gained a filtered index in `7672bb6`)".
  5. Lines 3563–3565 and 3603–3605 (`/dashboard/health` gating): mark **kept by decision (H1)**: the gate stops an unauthenticated caller probing database state; the cost is a 500 instead of "Unhealthy" when the database is down, and `/health/ready` is the anonymous signal for that case.
  6. Lines 4036–4039 (reset-password): **stale — implemented**; point at `UserEndpoints.cs` and `docs/Engineering/ACCOUNT-ACTIVATION.md`.
  7. Lines 3076–3090 ("Do this first"): items 2 and 3 are done (H1); item 1 (review D7/D9) remains and now belongs to Members.
  8. Line 3129 "Roughly 19 findings remain": rewrite to what remains after H1 (the deferred list from item 1) with the count.
  9. Every other paragraph H1 closed (seeder lock docs 3628/3661–3663; `features: null` 3629–3630/3666–3667; `DiscardTrackedChanges` 3630–3631/3667–3668/3892–3896; `livingGyms`/contributions/round trips/`mrr`/three tests 3565–3569/3606–3611; `FindNavigation` 3567; API-ref 3676–3681; JPY 3144–3145/3587–3588; withdrawn count 3887–3891; `StoredException` 3905–3907; delete-gym test 3909–3911; `EntityType` 3920–3922; `IDashboardHealthCheck` 3927–3930; dev ports 4009–4014; collation 3380–3385; idempotency doc 3387–3391; Mailpit 3393–3396; assign race 3964–3982): prefix each with **Resolved (H1, 2026-09-19):** and one clause saying how. Do not delete the paragraphs — the file keeps its reasoning.

- [ ] **Step 9: Commit (do not push — the controller pushes after the final review):**

```bash
git add docs src/BuildingBlocks/GymOS.BuildingBlocks.Infrastructure/Idempotency/IdempotencyRecordConfiguration.cs src/Platform/GymOS.Platform.Application/Analytics/Abstractions/IDashboardQueries.cs
git commit -m "docs: H1 hardening — Analytics §11 and STATUS say what the code does, TenantIsolation records steps 1–2, release notes, small comment fixes

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

## Self-review

**Spec coverage.** §2.1 (order, anonymous early return, §6 proof, raw grant check) → Task 1. §2.2/§2.3 (split snapshot, D3 predicate, D4 keying, bearer handler, fakes) → Task 2. §2.4 tests 1–5 → Tasks 1–2 (test 3 pre-exists; test 4 is the existing observer test). §3.1 → Task 3. §3.2 (shared queries, one round trip, `mrr` order, navigation) → Task 4. §3.3 (positive controls, fake, six routes, JPY, delete test) → Task 5. §3.4 (assign transaction + locked read, ban) → Task 6. §3.5 → Task 7. §3.6 (logging, suffix, `EntityType`, ports) → Task 8. §3.7 (every document) → Task 9, except the SeedingLocks doc (Task 3) and DOCKER.md's ports sentence (Task 8). §4 out-of-scope items appear only in STATUS's remaining list. §5 verification: each named check has a step above.

**Placeholders.** Two adaptive instructions remain by design: Task 4 Step 1 tells the implementer to add the GBP price through the same request shape `CreatePlanAsync` uses (the body is quoted), and Task 5 Step 5 allows the delegate type to follow `IDispatcher.SendAsync<TResponse>(ICommand<TResponse>)`, which is the actual signature, so no adaptation should be needed. No TBDs.

**Type consistency.** `UserSecurityIdentity`/`UserSecuritySnapshot`/`ICallerSnapshot`/`IIdentityQueries` are identical in Task 2's Interfaces block, Step 3 and Step 8's recording fake. `PlatformPopulationQueries` members match between Task 4's Interfaces block and Step 3, and both callers (Steps 4–5) use them by those names. `FakePlanRepository.LockedReads` is produced in Task 6 Step 1 and consumed in Step 2. `FeatureExceptionLogging.LogOverridesWithdrawn(ILogger, int, Guid, string)` matches all four call sites. `GymTestContext.WithFeatureCatalogue()` is defined in Task 5 Step 4 and used in Step 5; the fake's error message names it.

**Order dependencies.** Task 2 depends on Task 1 (the tenant must be set before authorization for test 2 to pass). Task 8's logging touches `AssignSubscriptionCommandHandler` after Task 6 changed it — both edit different lines; do Task 6 first as numbered. Tasks 3, 4, 5, 7 are independent of each other and of Tasks 1–2.
