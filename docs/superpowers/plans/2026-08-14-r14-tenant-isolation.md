# R14 — Tenant isolation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and prove PostgreSQL row-level tenant isolation, plus the startup validation R14 names, so the Members domain inherits a control that has been observed working.

**Architecture:** A scoped `ITenantContext` set from the route, written onto every database connection as `app.tenant_id`, read by an RLS policy on every `ITenantScoped` table. A startup validator refuses to boot when a tenant-scoped table lacks the column, RLS, `FORCE`, or a policy. No production table becomes tenant-scoped in this increment — the mechanism is proven against a table that exists only in the integration-test schema.

**Tech Stack:** .NET 10, EF Core 10 with Npgsql, PostgreSQL 17, xUnit + Testcontainers.

**Spec:** [`docs/superpowers/specs/2026-08-14-r14-tenant-isolation-design.md`](../specs/2026-08-14-r14-tenant-isolation-design.md) — read it first. Every task below cites the decision (D1–D8) it implements. Where this plan and the spec disagree, the spec wins.

## Global Constraints

- **No new dependencies.** Everything here uses EF Core, Npgsql and xUnit, all already present.
- **`FORCE ROW LEVEL SECURITY` on every tenant-scoped table** (D5). The app owns its tables, and an owner is exempt from its own policies without it. The validator asserts `relforcerowsecurity`, not merely `relrowsecurity`.
- **The session variable is written on EVERY connection open, unconditionally** (D6) — including an empty value for platform scope. Never "set when present, reset when absent".
- **Unset must resolve to no rows, never all rows** (D4).
- **No bypass role, no "run as platform" flag, ever** (D3).
- **The tenant is NOT added to `ICurrentUser`** (D1).
- **`DateTimeOffset.UtcNow` is banned** by an analyzer (rule R2 / AD-40) — inject `IClock`, or use a literal in tests.
- **Gate before every commit:** `dotnet build GymOS.Backend.slnx` (0 warnings; warnings are errors), `dotnet format --verify-no-changes GymOS.Backend.slnx`, and the test projects the task touches.
- **A leftover `dotnet run` host locks the build output** (MSB3027). If a build cannot copy DLLs, stop the running API first.

---

### Task 1: The tenant context and its marker

Implements **D1** (tenant is not identity) and **D7** (a marker the architecture test can see).

**Files:**

- Create: `src/BuildingBlocks/GymOS.BuildingBlocks.Application/Abstractions/ITenantContext.cs`
- Create: `src/BuildingBlocks/GymOS.BuildingBlocks.Application/Abstractions/ITenantScoped.cs`
- Create: `src/BuildingBlocks/GymOS.BuildingBlocks.Infrastructure/Tenancy/TenantContext.cs`
- Create: `tests/GymOS.BuildingBlocks.UnitTests/TenantContextTests.cs`

**Interfaces:**

- Produces:
  - `interface ITenantContext { Guid? TenantId { get; } bool IsPlatformScope { get; } }`
  - `interface ITenantContextSetter { void SetTenant(Guid tenantId); }`
  - `interface ITenantScoped { Guid TenantId { get; } }`
  - `public sealed class TenantContext : ITenantContext, ITenantContextSetter`

**Read and write are separate interfaces on purpose.** Reading the acting tenant is something any
handler may do; setting it is something exactly two callers may do — the middleware and a job's
`TenantScope`. Splitting them means a handler cannot re-point its own scope even by accident, and the
two write sites are greppable. `TenantContext` itself is `public` because the middleware lives in
`GymOS.WebApi` and the tests in their own assembly; there is no `InternalsVisibleTo` in this
repository, so an `internal` class would simply not compile from either.

- [ ] **Step 1: Write the failing test**

Create `tests/GymOS.BuildingBlocks.UnitTests/TenantContextTests.cs`:

```csharp
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Infrastructure.Tenancy;
using Xunit;

namespace GymOS.BuildingBlocks.UnitTests;

public sealed class TenantContextTests
{
    private static readonly Guid Gym = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void AFreshContextIsPlatformScope()
    {
        // Platform scope is the default, and it is not the same as "any tenant": a platform-scope
        // request reads no tenant rows at all (D4). Nothing is implicitly allowed.
        var context = new TenantContext();

        Assert.Null(context.TenantId);
        Assert.True(context.IsPlatformScope);
    }

    [Fact]
    public void SettingTheTenantMakesItTenantScope()
    {
        var context = new TenantContext();

        context.SetTenant(Gym);

        Assert.Equal(Gym, context.TenantId);
        Assert.False(context.IsPlatformScope);
    }

    [Fact]
    public void TheTenantCanBeSetOnlyOnce()
    {
        // One request acts for one tenant. A second set would mean something upstream changed its
        // mind mid-request, and the query that already ran did so under the old value — so this
        // throws rather than quietly re-pointing the context.
        var context = new TenantContext();
        context.SetTenant(Gym);

        Assert.Throws<InvalidOperationException>(
            () => context.SetTenant(Guid.Parse("22222222-2222-2222-2222-222222222222")));
    }

    [Fact]
    public void SettingTheSameTenantTwiceIsHarmless()
    {
        // Idempotent, so a middleware that runs twice on an internal redirect is not a failure.
        var context = new TenantContext();
        context.SetTenant(Gym);
        context.SetTenant(Gym);

        Assert.Equal(Gym, context.TenantId);
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/GymOS.BuildingBlocks.UnitTests --filter "FullyQualifiedName~TenantContextTests"`
Expected: FAIL to compile — `ITenantContext` and `TenantContext` do not exist.

- [ ] **Step 3: Write the abstractions**

Create `ITenantContext.cs`:

```csharp
namespace GymOS.BuildingBlocks.Application.Abstractions;

/// <summary>
/// The tenant this scope is acting for, or platform scope when there is none.
/// </summary>
/// <remarks>
/// <para>Deliberately separate from <see cref="ICurrentUser"/>. Who the caller is and which tenant
/// they are acting for are different facts, and a platform administrator operating on one gym has
/// both at once. Collapsing them into one object creates the natural home for the line that ends
/// this control — <c>if (user.IsPlatformAdmin) skipFilter()</c> — so they stay apart.</para>
/// <para>Platform scope is not "every tenant". A platform-scope request reads no tenant-scoped rows
/// at all; to read a tenant's data you name that tenant.</para>
/// </remarks>
public interface ITenantContext
{
    Guid? TenantId { get; }

    bool IsPlatformScope { get; }
}
```

Create `ITenantContextSetter.cs`:

```csharp
namespace GymOS.BuildingBlocks.Application.Abstractions;

/// <summary>
/// The write half of <see cref="ITenantContext"/>, for the two places allowed to name the tenant.
/// </summary>
/// <remarks>
/// Reading the acting tenant is something any handler may do; setting it belongs to the request
/// middleware and to a job iterating gyms, and nowhere else. Separating the capabilities means a
/// handler cannot re-point its own scope even by accident, and the write sites stay greppable.
/// </remarks>
public interface ITenantContextSetter
{
    void SetTenant(Guid tenantId);
}
```

Create `ITenantScoped.cs`:

```csharp
namespace GymOS.BuildingBlocks.Application.Abstractions;

/// <summary>
/// Marks an entity whose rows belong to one tenant, and which must therefore carry a
/// <c>tenant_id</c> column protected by a row-level security policy (R14).
/// </summary>
/// <remarks>
/// A marker interface rather than an EF annotation so an architecture test can see it too: an entity
/// in a future tenant module that forgets this fails a test, instead of silently opting out of
/// isolation and passing every other gate. <c>TenantIsolationValidator</c> keys on it at startup.
/// </remarks>
public interface ITenantScoped
{
    Guid TenantId { get; }
}
```

- [ ] **Step 4: Write the implementation**

Create `TenantContext.cs`:

```csharp
using GymOS.BuildingBlocks.Application.Abstractions;

namespace GymOS.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>Scoped, set once per request (or per job iteration), read many times after.</summary>
public sealed class TenantContext : ITenantContext, ITenantContextSetter
{
    public Guid? TenantId { get; private set; }

    public bool IsPlatformScope => TenantId is null;

    /// <summary>
    /// Names the tenant this scope acts for.
    /// </summary>
    /// <remarks>
    /// Re-pointing a scope at a different tenant is refused. One request acts for one tenant, and a
    /// second call would mean any query that already ran did so under the previous value — a
    /// difference the caller cannot see and the database will not report.
    /// </remarks>
    public void SetTenant(Guid tenantId)
    {
        if (TenantId is { } existing && existing != tenantId)
        {
            throw new InvalidOperationException(
                $"This scope already acts for tenant {existing} and cannot be re-pointed at {tenantId}.");
        }

        TenantId = tenantId;
    }
}
```

- [ ] **Step 5: Run it and watch it pass**

Run: `dotnet test tests/GymOS.BuildingBlocks.UnitTests --filter "FullyQualifiedName~TenantContextTests"`
Expected: PASS, 4 tests.

- [ ] **Step 6: Gate and commit**

```bash
dotnet build GymOS.Backend.slnx && dotnet format --verify-no-changes GymOS.Backend.slnx
git add -A
git commit -m "The tenant context, kept separate from identity

Who the caller is and which tenant they act for are different facts, and a
platform administrator operating on a gym has both. Keeping them in separate
objects means there is no natural place to write the line that ends this
control. Re-pointing a scope at a second tenant is refused: any query that
already ran did so under the previous value."
```

---

### Task 2: The connection interceptor

Implements **D6** (written on every open) and **D4** (unset means nothing).

**Files:**

- Create: `src/Platform/GymOS.Platform.Infrastructure/Tenancy/TenantSessionInterceptor.cs`
- Modify: `src/Platform/GymOS.Platform.Infrastructure/DependencyInjection/PlatformInfrastructureServiceCollectionExtensions.cs`

**Interfaces:**

- Consumes: `ITenantContext` (Task 1)
- Produces: `sealed class TenantSessionInterceptor : DbConnectionInterceptor`, registered scoped and added to the `AppDbContext` interceptor chain.

- [ ] **Step 1: Write the interceptor**

There is no unit test for this one — its whole behaviour is a real connection's session state, which Task 5 proves against real PostgreSQL. Write it, then Task 5's tests are its verification.

Create `TenantSessionInterceptor.cs`:

```csharp
using System.Data.Common;
using GymOS.BuildingBlocks.Application.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace GymOS.Platform.Infrastructure.Tenancy;

/// <summary>
/// Writes the acting tenant onto every database connection, so the row-level security policies can
/// read it.
/// </summary>
/// <remarks>
/// <para><b>Written on every open, unconditionally — including an empty value for platform scope.</b>
/// Npgsql pools physical connections, so a value set with a plain <c>SET</c> outlives the logical
/// connection that set it and is inherited by whichever request borrows that physical connection
/// next. That bug is intermittent, invisible, and exposes MORE data rather than less.</para>
/// <para>Writing unconditionally makes inheritance impossible: nothing is ever read that this
/// connection's current user did not just write. The shape this deliberately avoids is "set it when
/// there is a tenant, reset it when there is not" — that has an execution path where neither branch
/// runs, which is exactly the leak above.</para>
/// <para><c>SET LOCAL</c> would also be immune, being transaction-scoped, and is not used because EF
/// issues plain reads outside a transaction: it would either silently not apply or force a
/// transaction around every query.</para>
/// </remarks>
internal sealed class TenantSessionInterceptor : DbConnectionInterceptor
{
    private readonly ITenantContext _tenant;

    public TenantSessionInterceptor(ITenantContext tenant) => _tenant = tenant;

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await WriteTenantAsync(connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        WriteTenantAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
        base.ConnectionOpened(connection, eventData);
    }

    private async Task WriteTenantAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        // An empty string, not NULL and not a skipped write: `current_setting('app.tenant_id', true)`
        // then yields '', which casts to NULL for the comparison, so a tenant-scoped table matches no
        // rows under platform scope. Fail closed (D4).
        string value = _tenant.TenantId?.ToString() ?? string.Empty;

        await using NpgsqlCommand command = ((NpgsqlConnection)connection).CreateCommand();
        command.CommandText = "SELECT set_config('app.tenant_id', $1, false)";
        command.Parameters.Add(new NpgsqlParameter { Value = value });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
```

- [ ] **Step 2: Register it**

In `PlatformInfrastructureServiceCollectionExtensions.cs`, beside the other scoped interceptors (around line 41):

```csharp
        services.TryAddScoped<TenantSessionInterceptor>();
```

and add it to the `AddInterceptors(...)` call in `AddDbContext` (around line 99), **last** in the list:

```csharp
            .AddInterceptors(
                provider.GetRequiredService<AuditableTimestampsInterceptor>(),
                provider.GetRequiredService<AuditSaveChangesInterceptor>(),
                provider.GetRequiredService<OutboxSaveChangesInterceptor>(),
                // Connection-level, not SaveChanges-level: it must run on every open, including the
                // opens that only read.
                provider.GetRequiredService<TenantSessionInterceptor>()));
```

Also register the context itself, beside it:

```csharp
        services.TryAddScoped<TenantContext>();
        services.TryAddScoped<ITenantContext>(provider => provider.GetRequiredService<TenantContext>());
        services.TryAddScoped<ITenantContextSetter>(provider => provider.GetRequiredService<TenantContext>());
```

Both registrations resolve the same instance per scope, so middleware can set it and the interceptor can read it.

- [ ] **Step 3: Verify it builds and breaks nothing**

Run: `dotnet build GymOS.Backend.slnx` then `dotnet test tests/GymOS.IntegrationTests --no-build`
Expected: 0 errors; the existing 289 integration tests still pass. They are all platform-scope, so every connection now writes an empty `app.tenant_id` and no policy exists yet to read it — behaviour is unchanged, which is what this step confirms.

- [ ] **Step 4: Commit**

```bash
dotnet format --verify-no-changes GymOS.Backend.slnx
git add -A
git commit -m "Write the acting tenant onto every connection, unconditionally

Npgsql pools physical connections, so a plain SET outlives the logical
connection and the next borrower inherits the previous tenant — intermittent,
invisible, and it exposes more data rather than less. Writing on every open,
including an empty value for platform scope, makes inheritance impossible:
nothing is read that this user did not just write.

No policy reads it yet. The existing suite passing is the point: platform-scope
behaviour is unchanged."
```

---

### Task 3: The startup validator

Implements **D7** and **D8** (fails the boot; does not swallow).

**Files:**

- Create: `src/Platform/GymOS.Platform.Infrastructure/Tenancy/TenantIsolationValidator.cs`
- Modify: `src/Platform/GymOS.Platform.Infrastructure/DependencyInjection/PlatformInfrastructureServiceCollectionExtensions.cs`
- Modify: `src/Host/GymOS.WebApi/Program.cs`

**Interfaces:**

- Consumes: `AppDbContext`, `ITenantScoped` (Task 1)
- Produces: `sealed class TenantIsolationValidator { Task ValidateAsync(CancellationToken) }`, throwing `InvalidOperationException` naming every offending table.

- [ ] **Step 1: Write the validator**

Its negative test is Task 5's test 5, which runs the real validator against a deliberately unprotected table — so it is written here and proven there.

Create `TenantIsolationValidator.cs`:

```csharp
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Tenancy;

/// <summary>
/// R14's startup persistence validation: every tenant-scoped table carries <c>tenant_id</c> and is
/// protected by a forced row-level security policy.
/// </summary>
/// <remarks>
/// <para><b>Fails the boot.</b> A platform that will not start is a visible outage; a platform serving
/// cross-tenant reads is an invisible breach. <c>Program.cs</c> logs a startup exception as Fatal and
/// rethrows, so the host dies and the container restarts.</para>
/// <para><b>It does not swallow.</b> <c>AuditLogPartitionMaintainer</c> and
/// <c>IdempotencyRecordMaintainer</c> catch <c>NpgsqlException</c> so a schemaless database can boot;
/// this must not copy them. It skips only while migrations are genuinely pending — a real first-boot
/// state, and the guard <c>SuperAdminSeeder</c> already has. Anything else is fatal, because a
/// security control that swallows "the table isn't there" passes on an empty database and on every
/// misconfiguration that resembles one.</para>
/// </remarks>
internal sealed class TenantIsolationValidator
{
    private readonly AppDbContext _dbContext;

    public TenantIsolationValidator(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        if ((await _dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
        {
            return; // first boot: the tables do not exist yet, and the migration will create them protected
        }

        var problems = new List<string>();

        foreach (var (schema, table) in TenantScopedTables())
        {
            problems.AddRange(await InspectAsync(schema, table, cancellationToken));
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "R14 (tenant isolation) is not satisfied, so the host will not start:" +
                Environment.NewLine + string.Join(Environment.NewLine, problems));
        }
    }

    /// <summary>Every mapped entity implementing <see cref="ITenantScoped"/>, with its table.</summary>
    private IEnumerable<(string Schema, string Table)> TenantScopedTables() =>
        _dbContext.Model.GetEntityTypes()
            .Where(entity => typeof(ITenantScoped).IsAssignableFrom(entity.ClrType))
            .Select(entity => (
                Schema: entity.GetSchema() ?? "public",
                Table: entity.GetTableName() ?? entity.ClrType.Name))
            .Distinct();

    private async Task<IReadOnlyList<string>> InspectAsync(
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        var problems = new List<string>();

        // One round trip per table, straight from pg_catalog. `relforcerowsecurity` is the one that
        // matters most: this application connects as the owner of its tables, and PostgreSQL exempts
        // an owner from its own policies, so checking `relrowsecurity` alone passes on a table whose
        // policies never filter a single row (D5).
        await using var command = _dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            """
            SELECT
                c.relrowsecurity,
                c.relforcerowsecurity,
                (SELECT count(*) FROM pg_policy p WHERE p.polrelid = c.oid) AS policies,
                (SELECT count(*) FROM information_schema.columns col
                 WHERE col.table_schema = n.nspname
                   AND col.table_name = c.relname
                   AND col.column_name = 'tenant_id') AS has_tenant_id
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table
            """;

        var schemaParameter = command.CreateParameter();
        schemaParameter.ParameterName = "schema";
        schemaParameter.Value = schema;
        command.Parameters.Add(schemaParameter);

        var tableParameter = command.CreateParameter();
        tableParameter.ParameterName = "table";
        tableParameter.Value = table;
        command.Parameters.Add(tableParameter);

        await _dbContext.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                problems.Add($"  {schema}.{table}: the table does not exist.");
                return problems;
            }

            if (reader.GetInt64(3) == 0)
            {
                problems.Add($"  {schema}.{table}: no tenant_id column.");
            }

            if (!reader.GetBoolean(0))
            {
                problems.Add($"  {schema}.{table}: row-level security is not enabled.");
            }

            if (!reader.GetBoolean(1))
            {
                problems.Add(
                    $"  {schema}.{table}: row-level security is not FORCED, so the table owner — " +
                    "which is how this application connects — bypasses every policy on it.");
            }

            if (reader.GetInt64(2) == 0)
            {
                problems.Add($"  {schema}.{table}: no row-level security policy.");
            }
        }
        finally
        {
            await _dbContext.Database.CloseConnectionAsync();
        }

        return problems;
    }
}
```

- [ ] **Step 2: Register and invoke it**

Register beside the other scoped services in `PlatformInfrastructureServiceCollectionExtensions.cs`:

```csharp
        services.TryAddScoped<TenantIsolationValidator>();
```

In `Program.cs`, immediately **after** the `MigrateAsync()` block (around line 164) and **before** `PlatformIdentitySeeder`:

```csharp
        // R14, before anything reads or writes: a tenant-scoped table without a forced policy means
        // isolation is off, and the only safe response is not to serve traffic. Fatal by design —
        // see TenantIsolationValidator.
        await startupScope.ServiceProvider
            .GetRequiredService<TenantIsolationValidator>()
            .ValidateAsync(CancellationToken.None);
```

- [ ] **Step 3: Verify the host still starts**

Run: `dotnet build GymOS.Backend.slnx` then `dotnet test tests/GymOS.IntegrationTests --no-build`
Expected: 0 errors, 289 tests pass. No entity implements `ITenantScoped` yet, so the validator finds nothing to check and every host boots exactly as before. That is the correct behaviour for an empty subject, and Task 5 proves it is not merely vacuous.

- [ ] **Step 4: Commit**

```bash
dotnet format --verify-no-changes GymOS.Backend.slnx
git add -A
git commit -m "R14's startup validation, which never existed

The rule names 'startup persistence validation' as its enforcement and there
was none, because no tenant-scoped table has ever existed. It checks four
things per table: the tenant_id column, RLS enabled, RLS FORCED, and a policy.

FORCED is the one that matters. This application connects as the owner of its
tables and PostgreSQL exempts an owner from its own policies, so checking
relrowsecurity alone passes on a table whose policies never filter a row.

It fails the boot rather than logging, and unlike the existing startup
maintainers it does not swallow NpgsqlException — it skips only while
migrations are pending. A security control that swallows 'the table isn't
there' passes on an empty database and on everything that resembles one."
```

---

### Task 4: The test-only tenant table and its policy

Implements the spec's §2 scope boundary — the mechanism is proven against a table that exists only for the tests.

**Files:**

- Create: `tests/GymOS.IntegrationTests/Tenancy/TenantProbe.cs`
- Create: `tests/GymOS.IntegrationTests/Tenancy/TenantProbeSchema.cs`

**Interfaces:**

- Consumes: `ITenantScoped` (Task 1)
- Produces:
  - `sealed class TenantProbe : ITenantScoped { Guid Id; Guid TenantId; string Note; }`
  - `static class TenantProbeSchema { const string CreateSql; const string CreateWithoutPolicySql; }`

- [ ] **Step 1: Write the entity**

Create `TenantProbe.cs`:

```csharp
using GymOS.BuildingBlocks.Application.Abstractions;

namespace GymOS.IntegrationTests.Tenancy;

/// <summary>
/// A tenant-scoped row that exists only for the isolation tests.
/// </summary>
/// <remarks>
/// R14's mechanism is built before the first real tenant-scoped table (Members) so that isolation is
/// never retrofitted onto populated data. This is the subject it is proven against: minimal on
/// purpose, because the properties under test — a policy filters, an owner does not bypass, an unset
/// tenant reads nothing — are properties of the mechanism, not of any domain.
/// </remarks>
public sealed class TenantProbe : ITenantScoped
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public string Note { get; init; } = string.Empty;
}
```

- [ ] **Step 2: Write the schema SQL**

Create `TenantProbeSchema.cs`:

```csharp
namespace GymOS.IntegrationTests.Tenancy;

/// <summary>The probe table, in the two shapes the isolation tests need.</summary>
internal static class TenantProbeSchema
{
    /// <summary>Correctly protected: RLS on, FORCED, one policy keyed on the session variable.</summary>
    public const string CreateSql =
        """
        CREATE TABLE IF NOT EXISTS platform.tenant_probe (
            id uuid PRIMARY KEY,
            tenant_id uuid NOT NULL,
            note text NOT NULL
        );

        ALTER TABLE platform.tenant_probe ENABLE ROW LEVEL SECURITY;

        -- Without FORCE the owner — which is how this application connects — bypasses the policy
        -- below entirely, and every isolation test would pass while filtering nothing (D5).
        ALTER TABLE platform.tenant_probe FORCE ROW LEVEL SECURITY;

        DROP POLICY IF EXISTS tenant_probe_isolation ON platform.tenant_probe;

        -- NULLIF turns the empty string a platform-scope request writes into NULL, and
        -- `tenant_id = NULL` matches no rows: unset reads nothing rather than everything (D4).
        CREATE POLICY tenant_probe_isolation ON platform.tenant_probe
            USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
            WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
        """;

    /// <summary>The same table with no policy — the subject of the validator's negative test.</summary>
    public const string CreateWithoutPolicySql =
        """
        CREATE TABLE IF NOT EXISTS platform.tenant_probe (
            id uuid PRIMARY KEY,
            tenant_id uuid NOT NULL,
            note text NOT NULL
        );
        """;

    public const string DropSql = "DROP TABLE IF EXISTS platform.tenant_probe;";
}
```

- [ ] **Step 3: Verify it compiles**

Run: `dotnet build GymOS.Backend.slnx`
Expected: 0 errors. Nothing uses these yet — Task 5 does.

- [ ] **Step 4: Commit**

```bash
dotnet format --verify-no-changes GymOS.Backend.slnx
git add -A
git commit -m "A tenant-scoped table that exists only for the isolation tests

R14's mechanism is built before the first real tenant-scoped table so isolation
is never retrofitted onto populated data. This is what it is proven against.

Two shapes: correctly protected, and deliberately unprotected for the
validator's negative test. The policy uses NULLIF so the empty string a
platform-scope request writes becomes NULL, and tenant_id = NULL matches no
rows — unset reads nothing rather than everything."
```

---

### Task 5: The six tests that are the point

Implements the spec's §6. RLS is easy to install and hard to notice not working, so this task is the increment's value.

**Files:**

- Create: `tests/GymOS.IntegrationTests/Tenancy/TenantIsolationTests.cs`

**Interfaces:**

- Consumes: `TenantProbe`, `TenantProbeSchema` (Task 4), `TenantContext`, `ITenantContext` (Task 1), `TenantIsolationValidator` (Task 3)

- [ ] **Step 1: Read the harness first**

Read `tests/GymOS.IntegrationTests/PostgresFixture.cs` and `AuthenticationHost.cs`. Use `PostgresFixture.CreateIsolatedDatabaseAsync("tenant_isolation")` for a database of this suite's own, and follow how `FeatureOverrideSweepTests` builds a host and resolves services from a scope.

**The connection string for this suite must set `Maximum Pool Size=1`.** Test 6 is otherwise untestable by luck: you cannot normally guarantee which physical connection you get, so a leak test passes on a pool that happened not to reuse and proves nothing. One connection makes reuse certain and leakage deterministic.

- [ ] **Step 2: Write all six tests**

Create `TenantIsolationTests.cs`. Each test opens a scope, sets the tenant on that scope's `TenantContext`, and queries through `AppDbContext`:

```csharp
[Fact]
public async Task ATenantSeesOnlyItsOwnRows()
{
    await SeedAsync(TenantA, "a-note");
    await SeedAsync(TenantB, "b-note");

    IReadOnlyList<string> notes = await NotesAsAsync(TenantA);

    Assert.Equal(["a-note"], notes);
}

[Fact]
public async Task AWriteCannotBeForgedForAnotherTenant()
{
    // WITH CHECK on the policy: acting as A, inserting a row stamped B is refused outright rather
    // than silently landing where the caller cannot see it.
    await Assert.ThrowsAnyAsync<DbUpdateException>(() => SeedAsAsync(actingAs: TenantA, stampedWith: TenantB));
}

[Fact]
public async Task TheOwnerDoesNotBypassThePolicy()
{
    // The test that justifies FORCE. This application connects as the owner of its tables, and
    // PostgreSQL exempts an owner from its own policies — so WITHOUT `FORCE ROW LEVEL SECURITY`
    // this passes while filtering nothing, and every other test here passes with it.
    //
    // Confirm the connected role really is the owner, or the test proves nothing about the risk.
    Assert.True(await ConnectedRoleOwnsProbeTableAsync());

    await SeedAsync(TenantA, "a-note");
    await SeedAsync(TenantB, "b-note");

    Assert.Single(await NotesAsAsync(TenantA));
}

[Fact]
public async Task PlatformScopeReadsNothing_NotEverything()
{
    // Fail-closed, asserted rather than assumed. A code path that forgets to set the tenant reads
    // no rows — a visible, empty failure somebody reports — instead of every tenant's rows.
    await SeedAsync(TenantA, "a-note");
    await SeedAsync(TenantB, "b-note");

    Assert.Empty(await NotesWithNoTenantAsync());
}

[Fact]
public async Task AProtectedTablePassesValidation_AnUnprotectedOneFailsTheBoot()
{
    // The validator's own negative test, running the REAL validator rather than a copy of its
    // query — so relaxing the boot check fails here too.
    await CreateProbeAsync(TenantProbeSchema.CreateSql);
    await ValidatorAsync().ValidateAsync(CancellationToken.None); // does not throw

    await DropProbeAsync();
    await CreateProbeAsync(TenantProbeSchema.CreateWithoutPolicySql);

    InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
        () => ValidatorAsync().ValidateAsync(CancellationToken.None));

    Assert.Contains("tenant_probe", error.Message, StringComparison.Ordinal);
    Assert.Contains("policy", error.Message, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public async Task TheTenantDoesNotLeakIntoTheNextUseOfAPooledConnection()
{
    // Maximum Pool Size=1 makes this deterministic: both scopes below are guaranteed the same
    // physical connection, so if the setting survived the first scope the second would read A's
    // rows. It writes on every open precisely so it cannot (D6).
    await SeedAsync(TenantA, "a-note");

    IReadOnlyList<string> asA = await NotesAsAsync(TenantA);
    IReadOnlyList<string> asPlatform = await NotesWithNoTenantAsync();

    Assert.Single(asA);
    Assert.Empty(asPlatform);
}
```

Write the helpers (`SeedAsync`, `SeedAsAsync`, `NotesAsAsync`, `NotesWithNoTenantAsync`, `CreateProbeAsync`, `DropProbeAsync`, `ValidatorAsync`, `ConnectedRoleOwnsProbeTableAsync`) against the fixture. `ConnectedRoleOwnsProbeTableAsync` runs:

```sql
SELECT pg_get_userbyid(c.relowner) = current_user
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'platform' AND c.relname = 'tenant_probe'
```

- [ ] **Step 3: Run them**

Run: `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~TenantIsolationTests"`
Expected: PASS, 6 tests.

- [ ] **Step 4: NEGATIVE CONTROL — prove test 3 can fail**

This step is mandatory and its output goes in the commit message.

Temporarily remove `ALTER TABLE platform.tenant_probe FORCE ROW LEVEL SECURITY;` from `TenantProbeSchema.CreateSql`, then:

Run: `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~TheOwnerDoesNotBypassThePolicy"`
Expected: **FAIL** — two rows come back where one was expected, because the owner bypassed the policy.

Then restore the line and re-run: PASS.

A control that cannot be shown failing is indistinguishable from one that does nothing, and this session found four such guards already.

- [ ] **Step 5: Full suite**

Run: `dotnet test tests/GymOS.IntegrationTests --no-build`
Expected: 295 passed (289 + 6).

- [ ] **Step 6: Commit**

```bash
dotnet format --verify-no-changes GymOS.Backend.slnx
git add -A
git commit -m "Six tests that prove tenant isolation actually isolates

RLS is easy to install and hard to notice not working, so these are the
increment: a tenant sees only its own rows, a write cannot be forged for
another tenant, platform scope reads nothing rather than everything, an
unprotected table fails the boot, and the setting does not survive into the
next use of a pooled connection.

Two needed specific construction to be real. The owner-bypass test was proved
by negative control — with FORCE removed it reports two rows where one was
expected, which is exactly the silent failure it exists to catch. And the
pooling test sets Maximum Pool Size=1, because otherwise it passes on a pool
that happened not to reuse and proves nothing.

The validator test calls the real validator, not a copy of its query, so
relaxing the boot check fails here too."
```

---

### Task 6: Route-based tenant resolution

Implements **D2** (the tenant comes from the route, checked against the caller's grants) — the piece the spec's §2 puts in scope and §4 names `TenantResolutionMiddleware`.

**Files:**

- Create: `src/Host/GymOS.WebApi/Middleware/TenantResolutionMiddleware.cs`
- Create: `tests/GymOS.IntegrationTests/Tenancy/TenantResolutionTests.cs`
- Modify: `src/Host/GymOS.WebApi/Program.cs`

**Interfaces:**

- Consumes: `ITenantContextSetter.SetTenant(Guid)` (Task 1), `ICurrentUser`, and the caller's grants — `UserRole.TenantId`, where **null means platform-wide** and a value means a grant for that one gym.
- Produces: `sealed class TenantResolutionMiddleware`, wired after `UseAuthorization()`.

**Why it is built now rather than with the first tenant route:** it is the security half of the seam, and building it alongside a domain is how it ends up shaped by delivery pressure. Wired now it is inert — no route has a `{gymId}` that reaches a tenant-scoped table yet — so the Members increment adds a route, not a security decision.

- [ ] **Step 1: Write the failing tests**

Create `tests/GymOS.IntegrationTests/Tenancy/TenantResolutionTests.cs`, driving real HTTP against the test host. Four cases:

```csharp
[Fact]
public async Task ARouteWithNoGymIdLeavesTheRequestInPlatformScope()
{
    // Every endpoint built so far is platform-scope, and must stay exactly as it was.
    using HttpResponseMessage response = await _client.GetAsync(new Uri("/api/v1/plans", UriKind.Relative));

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
}

[Fact]
public async Task ARouteWithAGymIdSetsThatTenantForTheRequest()
{
    Guid gym = await PlatformApiHelpers.CreateGymAsync(_client, "tenant-route-gym");

    // A SuperAdmin holds a platform-wide grant, so it may act for any gym — and still acts for ONE.
    using HttpResponseMessage response = await _client.GetAsync(
        new Uri($"/api/v1/gyms/{gym}", UriKind.Relative));

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
}

[Fact]
public async Task AGymTheCallerHasNoGrantForIsNotFound_NotForbidden()
{
    // 404, deliberately. "You may not see this gym" and "this gym does not exist" must be
    // indistinguishable, or the status code becomes an oracle for enumerating tenants.
    Guid gym = await PlatformApiHelpers.CreateGymAsync(_client, "someone-elses-gym");
    HttpClient scoped = await SignedInWithGrantForAsync(Guid.NewGuid()); // a grant for a DIFFERENT gym

    using HttpResponseMessage response = await scoped.GetAsync(
        new Uri($"/api/v1/gyms/{gym}", UriKind.Relative));

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
}

[Fact]
public async Task AMalformedGymIdIsNotFound_AndSetsNoTenant()
{
    // A route value that is not a Guid must not fall through to platform scope, which would be a
    // silent widening — it is simply not a gym.
    using HttpResponseMessage response = await _client.GetAsync(
        new Uri("/api/v1/gyms/not-a-guid", UriKind.Relative));

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
}
```

Write `SignedInWithGrantForAsync` against the fixture: create a user, assign it a gym-scoped role via `SetRolesAsync`, sign in, return that client. Follow `PlatformApiHelpers.SignedInAsync` for the shape.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~TenantResolutionTests"`
Expected: the two grant tests FAIL — with no middleware, a caller without a grant currently gets 200.

- [ ] **Step 3: Write the middleware**

Create `TenantResolutionMiddleware.cs`. It runs **after** `UseAuthorization()` (it reads the authenticated caller's grants) and:

1. reads `gymId` from `HttpContext.Request.RouteValues`; if absent, does nothing — platform scope;
2. if present but not a `Guid`, responds **404**;
3. if the caller holds a platform-wide grant (`UserRole.TenantId is null`) **or** a grant for that gym, calls `TenantContext.SetTenant(gymId)`;
4. otherwise responds **404**, never 403 — see the test above for why.

Resolve `ITenantContextSetter` from `HttpContext.RequestServices` — the same scoped instance the interceptor reads through `ITenantContext`, since both registrations resolve the one `TenantContext`.

- [ ] **Step 4: Wire it**

In `Program.cs`, immediately after `app.UseAuthorization();`:

```csharp
    // After authorization, because it reads the caller's grants; before anything opens a connection,
    // because TenantSessionInterceptor writes whatever this leaves on the context. Getting the order
    // wrong fails closed — no tenant, no rows — which is the safe direction and looks like an empty
    // screen rather than an error.
    app.UseMiddleware<TenantResolutionMiddleware>();
```

- [ ] **Step 5: Run them and watch them pass**

Run: `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~TenantResolutionTests"`
Expected: PASS, 4 tests.

- [ ] **Step 6: Confirm nothing platform-scope regressed**

Run: `dotnet test tests/GymOS.IntegrationTests --no-build`
Expected: 299 passed (295 + 4). Every existing endpoint is platform-scope or gym-routed with a SuperAdmin caller, so all of them must still pass — this step is the check that the middleware is inert where it should be.

- [ ] **Step 7: Gate and commit**

```bash
dotnet format --verify-no-changes GymOS.Backend.slnx
git add -A
git commit -m "Resolve the acting tenant from the route, checked against grants

The security half of the seam, built now rather than with the first
tenant-scoped route: alongside a domain it gets shaped by delivery pressure,
and the Members increment should be adding a route, not a security decision.
Inert today, since no gym-routed endpoint reaches a tenant-scoped table yet,
which is why the whole existing suite passing is the real assertion here.

A gym the caller has no grant for answers 404, not 403. 'You may not see this
gym' and 'this gym does not exist' must be indistinguishable, or the status
code becomes an oracle for enumerating tenants. A malformed id is 404 too,
rather than falling through to platform scope, which would be a silent
widening."
```

---
### Task 7: Close out

**Files:**

- Create: `docs/BusinessRules/TenantIsolation.md`
- Modify: `docs/STATUS.md`

- [ ] **Step 1: Write the business rules**

Create `docs/BusinessRules/TenantIsolation.md` covering: the rule in one line (`tenant_id = the session's tenant, or no rows`); where the tenant comes from (route, checked against grants); why a caller without a grant gets **404 rather than 403** — so the status code is not a tenant-enumeration oracle; why there is no bypass role; why `FORCE` is mandatory; and the fail-closed property. Follow `docs/BusinessRules/Features.md` for voice and structure.

- [ ] **Step 2: Update STATUS.md**

Record R14 as having a subject and a passing validation for the first time, that no production table is tenant-scoped yet, and that Members is next and will be the first. Note that `ARCHITECTURE-ADDENDUM.md`'s R14 row can be marked enforced.

- [ ] **Step 3: Run every gate**

```bash
dotnet build GymOS.Backend.slnx
dotnet format --verify-no-changes GymOS.Backend.slnx
dotnet test tests/GymOS.IntegrationTests --no-build
```

Record the exact numbers; do not round or estimate them.

- [ ] **Step 4: Commit and push**

```bash
git add -A && git commit -m "R14 has a subject, a validation, and tests that prove it"
git push origin development
```

---

## Notes for the executor

- **The tenant middleware is NOT in this plan.** Task 1's context and Task 2's interceptor are the seam; resolving `{gymId}` from a route and checking grants arrives with the first tenant-scoped endpoint, in the Members increment, because there is no tenant-scoped route to attach it to yet. The spec's D2 records the decision so that increment implements it rather than reinventing it.
- **If a task reveals the spec is wrong**, stop and say so rather than implementing around it.
- **`Maximum Pool Size=1` is confined to this suite's own host.** Do not put it on the shared fixture.
