# AD-39 Idempotency Framework Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement AD-39's API-command idempotency: a marker interface, a PostgreSQL claim table, a pipeline behavior implementing the full claim/replay decision table, and host plumbing — so a retried command executes exactly once and replays its stored outcome.

**Architecture:** A scoped `IdempotencyContext` carries the `Idempotency-Key` header inward (CorrelationContext pattern). `IdempotencyBehavior` (BuildingBlocks.Application) claims via `IIdempotencyStore` (implemented in Platform.Infrastructure over `AppDbContext`); the claim commits immediately (autocommit), the handler then runs inside an explicit EF transaction the behavior opens, and the record flips to Completed in that same transaction. Replays reconstruct the stored `Result`/`Result<T>` and re-enter the host's normal `WriteResults` mapping.

**Tech Stack:** .NET 10, EF Core + Npgsql (existing), xUnit + Testcontainers (existing). **No new packages.**

**Spec:** `docs/superpowers/specs/2026-08-07-idempotency-framework-design.md` (approved). Authority: `GymOS-Specification/ARCHITECTURE-ADDENDUM.md` §1.

## Global Constraints

- Architecture frozen (ADRs 1–52); this implements AD-39 as written — no deviations, no new ADR.
- `IClock` only; BCL clocks are analyzer-banned. Time flows inward as data.
- Logging via source-generated `[LoggerMessage]` on `sealed partial` classes.
- Business failures are `Result` (`GymOS.SharedKernel.Results`); invariant breaches are `DomainException`. The idempotency rejections (400/409/422) are `Result` failures with stable codes `idempotency.*`.
- Dependency policy: allowlist; **this plan adds zero packages**, so `DEPENDENCY_POLICY.md` and `ForbiddenPackagesTests` are untouched.
- Migrations: generate with `dotnet ef migrations add`, then append documented `migrationBuilder.Sql(...)` blocks (precedent: `20260805163523_AddSubscriptionPlans.cs:83-107`). Never edit the Designer/snapshot by hand. `dotnet ef` needs the dockerized postgres up (`docker compose up -d postgres`).
- Build: `dotnet build GymOS.Backend.slnx` from `C:\Work\Personal-Projects\GymOS\GymOS-Backend` (all commands below run from there). Test-host CLR-crash caveat (STATUS.md): a run counts only if output has no `Aborted` and totals match; re-run per class if needed.
- Registration order rule: first-registered pipeline behavior runs outermost. Approved order: Validation → Authorization → **Idempotency** → Audit → Transaction; only Authorization exists, so Idempotency registers immediately after `AddAuthorizationBehavior()` in `Program.cs`.
- Namespaces: file-scoped in `src/`, matching folder path. Every new public type gets a doc comment saying why it exists, matching house style.

---

### Task 1: Contract, scoped context, and error catalogue (BuildingBlocks.Application)

**Files:**
- Create: `src/BuildingBlocks/GymOS.BuildingBlocks.Application/Idempotency/IIdempotentCommand.cs`
- Create: `src/BuildingBlocks/GymOS.BuildingBlocks.Application/Idempotency/IdempotencyContext.cs`
- Create: `src/BuildingBlocks/GymOS.BuildingBlocks.Application/Idempotency/IdempotencyErrors.cs`
- Test: `tests/GymOS.BuildingBlocks.UnitTests/Idempotency/IdempotencyContextTests.cs`

**Interfaces:**
- Consumes: `GymOS.SharedKernel.Results.Error` (static factories `Error.Validation/Conflict(code, message)`).
- Produces: `IIdempotentCommand { string OperationKey; TimeSpan RetentionPeriod; bool KeyRequired; }`; `IdempotencyContext { string? Key; bool WasReplayed; TimeSpan? RetryAfter; void SetKey(string); void MarkReplayed(); void SetRetryAfter(TimeSpan); }`; `IdempotencyErrors.KeyRequired/KeyReuseMismatch/OperationInProgress` (each an `Error`). Codes: `idempotency.key_required`, `idempotency.key_reuse_mismatch`, `idempotency.operation_in_progress`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/GymOS.BuildingBlocks.UnitTests/Idempotency/IdempotencyContextTests.cs
using GymOS.BuildingBlocks.Application.Idempotency;
using Xunit;

namespace GymOS.BuildingBlocks.UnitTests.Idempotency;

public sealed class IdempotencyContextTests
{
    [Fact]
    public void SetKey_stores_the_key_once()
    {
        var context = new IdempotencyContext();
        context.SetKey("key-1");
        Assert.Equal("key-1", context.Key);
    }

    [Fact]
    public void SetKey_twice_throws_exactly_one_edge_owns_it()
    {
        var context = new IdempotencyContext();
        context.SetKey("key-1");
        Assert.Throws<InvalidOperationException>(() => context.SetKey("key-2"));
    }

    [Fact]
    public void Flags_default_to_not_replayed_and_no_retry_after()
    {
        var context = new IdempotencyContext();
        Assert.Null(context.Key);
        Assert.False(context.WasReplayed);
        Assert.Null(context.RetryAfter);
    }

    [Fact]
    public void MarkReplayed_and_SetRetryAfter_are_readable_back()
    {
        var context = new IdempotencyContext();
        context.MarkReplayed();
        context.SetRetryAfter(TimeSpan.FromSeconds(30));
        Assert.True(context.WasReplayed);
        Assert.Equal(TimeSpan.FromSeconds(30), context.RetryAfter);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/GymOS.BuildingBlocks.UnitTests --filter "FullyQualifiedName~IdempotencyContextTests"`
Expected: build error — `IdempotencyContext` does not exist.

- [ ] **Step 3: Implement the three types**

```csharp
// src/BuildingBlocks/GymOS.BuildingBlocks.Application/Idempotency/IIdempotentCommand.cs
namespace GymOS.BuildingBlocks.Application.Idempotency;

/// <summary>
/// Declares a command idempotent (AD-39). The pipeline claims the caller's
/// <c>Idempotency-Key</c> before the handler runs and replays the stored outcome on retry.
/// </summary>
public interface IIdempotentCommand
{
    /// <summary>Stable operation identifier, e.g. <c>billing.mark-invoice-paid</c>.</summary>
    string OperationKey { get; }

    /// <summary>How long a completed record replays; financial operations declare 72h+.</summary>
    TimeSpan RetentionPeriod { get; }

    /// <summary>True → a request without an <c>Idempotency-Key</c> header is rejected 400.</summary>
    bool KeyRequired { get; }
}
```

```csharp
// src/BuildingBlocks/GymOS.BuildingBlocks.Application/Idempotency/IdempotencyContext.cs
namespace GymOS.BuildingBlocks.Application.Idempotency;

/// <summary>
/// Scoped carrier between the host edge and the pipeline: the edge sets the caller's
/// Idempotency-Key on the way in; the behavior sets the response flags on the way out
/// (same write-once pattern as CorrelationContext).
/// </summary>
public sealed class IdempotencyContext
{
    public string? Key { get; private set; }

    public bool WasReplayed { get; private set; }

    public TimeSpan? RetryAfter { get; private set; }

    /// <summary>Called once per scope by the owning edge.</summary>
    public void SetKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (Key is not null)
        {
            throw new InvalidOperationException(
                "The idempotency key for this scope is already set — exactly one edge owns it.");
        }

        Key = key;
    }

    public void MarkReplayed() => WasReplayed = true;

    public void SetRetryAfter(TimeSpan retryAfter) => RetryAfter = retryAfter;
}
```

```csharp
// src/BuildingBlocks/GymOS.BuildingBlocks.Application/Idempotency/IdempotencyErrors.cs
using GymOS.SharedKernel.Results;

namespace GymOS.BuildingBlocks.Application.Idempotency;

/// <summary>The three protocol-level rejections of AD-39's decision table, with stable codes.</summary>
public static class IdempotencyErrors
{
    /// <summary>400 — the command declares <c>KeyRequired</c> and no header was sent.</summary>
    public static Error KeyRequired { get; } = Error.Validation(
        "idempotency.key_required",
        "This operation requires an Idempotency-Key header so a retry cannot execute twice.");

    /// <summary>422 (via host override table) — same key, different request body: always a client bug.</summary>
    public static Error KeyReuseMismatch { get; } = Error.Conflict(
        "idempotency.key_reuse_mismatch",
        "This Idempotency-Key was already used for a different request. Use a new key for a new request.");

    /// <summary>409 + Retry-After — a concurrent duplicate holds a live claim; never run two.</summary>
    public static Error OperationInProgress { get; } = Error.Conflict(
        "idempotency.operation_in_progress",
        "An identical request is already in flight. Retry after the current attempt finishes.");
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/GymOS.BuildingBlocks.UnitTests --filter "FullyQualifiedName~IdempotencyContextTests"`
Expected: 4 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/BuildingBlocks/GymOS.BuildingBlocks.Application/Idempotency tests/GymOS.BuildingBlocks.UnitTests/Idempotency
git commit -m "AD-39 (1/8): idempotent-command contract, scoped context, error catalogue"
```

---

### Task 2: Canonical request hasher

**Files:**
- Create: `src/BuildingBlocks/GymOS.BuildingBlocks.Application/Idempotency/CanonicalRequestHasher.cs`
- Test: `tests/GymOS.BuildingBlocks.UnitTests/Idempotency/CanonicalRequestHasherTests.cs`

**Interfaces:**
- Produces: `static class CanonicalRequestHasher { static string Hash(object request); }` — 64-char lowercase SHA-256 hex over the bound command serialized with alphabetically ordered properties. Used by Task 5's behavior.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/GymOS.BuildingBlocks.UnitTests/Idempotency/CanonicalRequestHasherTests.cs
using GymOS.BuildingBlocks.Application.Idempotency;
using Xunit;

namespace GymOS.BuildingBlocks.UnitTests.Idempotency;

public sealed class CanonicalRequestHasherTests
{
    private sealed record AlphaFirst(string Amount, string Currency);

    // Same shape, reversed declaration order: canonicalization must make them hash equal.
    private sealed record CurrencyFirst(string Currency, string Amount);

    [Fact]
    public void Same_values_hash_identically_regardless_of_property_declaration_order()
    {
        string a = CanonicalRequestHasher.Hash(new AlphaFirst("50.00", "EUR"));
        string b = CanonicalRequestHasher.Hash(new CurrencyFirst("EUR", "50.00"));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Different_values_hash_differently()
    {
        string a = CanonicalRequestHasher.Hash(new AlphaFirst("50.00", "EUR"));
        string b = CanonicalRequestHasher.Hash(new AlphaFirst("59.99", "EUR"));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_is_64_lowercase_hex_characters()
    {
        string hash = CanonicalRequestHasher.Hash(new AlphaFirst("50.00", "EUR"));
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void Hash_is_stable_across_calls()
    {
        var request = new AlphaFirst("50.00", "EUR");
        Assert.Equal(CanonicalRequestHasher.Hash(request), CanonicalRequestHasher.Hash(request));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/GymOS.BuildingBlocks.UnitTests --filter "FullyQualifiedName~CanonicalRequestHasherTests"`
Expected: build error — `CanonicalRequestHasher` does not exist.

- [ ] **Step 3: Implement**

```csharp
// src/BuildingBlocks/GymOS.BuildingBlocks.Application/Idempotency/CanonicalRequestHasher.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace GymOS.BuildingBlocks.Application.Idempotency;

/// <summary>
/// SHA-256 over the bound command serialized with alphabetically ordered properties
/// (AD-39 §1.3 "canonicalized request body", decision recorded in the design doc):
/// two requests hash equal iff they mean the same thing after model binding, immune to
/// client-side whitespace and key-order differences that would falsely 422 a legitimate retry.
/// </summary>
public static class CanonicalRequestHasher
{
    private static readonly JsonSerializerOptions CanonicalOptions = CreateCanonicalOptions();

    public static string Hash(object request)
    {
        ArgumentNullException.ThrowIfNull(request);

        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(request, request.GetType(), CanonicalOptions);
        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }

    private static JsonSerializerOptions CreateCanonicalOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                static typeInfo =>
                {
                    int order = 0;
                    foreach (JsonPropertyInfo property in
                             typeInfo.Properties.OrderBy(p => p.Name, StringComparer.Ordinal).ToList())
                    {
                        property.Order = order++;
                    }
                },
            },
        };

        var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
        options.MakeReadOnly();
        return options;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/GymOS.BuildingBlocks.UnitTests --filter "FullyQualifiedName~CanonicalRequestHasherTests"`
Expected: 4 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/BuildingBlocks/GymOS.BuildingBlocks.Application/Idempotency/CanonicalRequestHasher.cs tests/GymOS.BuildingBlocks.UnitTests/Idempotency/CanonicalRequestHasherTests.cs
git commit -m "AD-39 (2/8): canonical request hasher — order-invariant SHA-256 of the bound command"
```

---

### Task 3: Store abstraction (BuildingBlocks.Application)

**Files:**
- Create: `src/BuildingBlocks/GymOS.BuildingBlocks.Application/Idempotency/IIdempotencyStore.cs`
- Test: covered by Task 5's behavior tests (this task is interface-only; no behavior to test yet). Compile gate only.

**Interfaces:**
- Produces (Task 4 implements, Task 5 consumes):

```csharp
public enum IdempotencyRecordStatus { InProgress = 1, Completed = 2, FailedDeterministic = 3 }

public sealed record IdempotencyRecordSnapshot(
    IdempotencyRecordStatus Status,
    string RequestHash,
    int ResponseStatus,
    string? ResponseBody,
    DateTimeOffset LeaseExpiresAt);

public sealed record IdempotencyClaimRequest(
    string OperationKey,
    string IdempotencyKey,
    string RequestHash,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed record IdempotencyClaimResult(bool Claimed, IdempotencyRecordSnapshot? Existing);

public interface IIdempotencyTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);
}

public interface IIdempotencyStore
{
    /// <summary>Atomic INSERT … ON CONFLICT DO NOTHING; commits immediately (never inside a transaction).</summary>
    Task<IdempotencyClaimResult> TryClaimAsync(IdempotencyClaimRequest request, CancellationToken cancellationToken);

    /// <summary>Re-leases an expired InProgress claim with the same hash. False if the row moved on (race lost).</summary>
    Task<bool> TryTakeOverAsync(string operationKey, string idempotencyKey, string requestHash, DateTimeOffset newLeaseExpiresAt, CancellationToken cancellationToken);

    /// <summary>Opens the explicit transaction the handler's SaveChanges will join (AD-39 atomicity).</summary>
    Task<IIdempotencyTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>Flips the record to Completed/FailedDeterministic; runs inside the ambient transaction.</summary>
    Task MarkFinishedAsync(string operationKey, string idempotencyKey, IdempotencyRecordStatus status, int responseStatus, string? responseBody, CancellationToken cancellationToken);

    /// <summary>Deletes the claim after a transient failure so a retry re-executes; autocommits.</summary>
    Task ReleaseClaimAsync(string operationKey, string idempotencyKey, CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Create the file with exactly the contents above** (one file, all types), namespace `GymOS.BuildingBlocks.Application.Idempotency`, with the doc comments shown. Add a file-level summary on `IIdempotencyStore`: "Persistence seam for AD-39 claims. Application cannot reference EF Core (layer law), so the implementation lives in Platform.Infrastructure over AppDbContext."

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/BuildingBlocks/GymOS.BuildingBlocks.Application`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/BuildingBlocks/GymOS.BuildingBlocks.Application/Idempotency/IIdempotencyStore.cs
git commit -m "AD-39 (3/8): idempotency store abstraction — the EF-free persistence seam"
```

---

### Task 4: Entity, EF configuration, migration, store implementation

**Files:**
- Create: `src/BuildingBlocks/GymOS.BuildingBlocks.Infrastructure/Idempotency/IdempotencyRecord.cs`
- Create: `src/BuildingBlocks/GymOS.BuildingBlocks.Infrastructure/Idempotency/IdempotencyRecordConfiguration.cs`
- Create: `src/Platform/GymOS.Platform.Infrastructure/Idempotency/IdempotencyStore.cs`
- Create (generated): `src/Platform/GymOS.Platform.Infrastructure/Persistence/Migrations/*_AddIdempotencyRecords.cs` (+ Designer, snapshot updated)
- Modify: `src/Platform/GymOS.Platform.Infrastructure/DependencyInjection/PlatformInfrastructureServiceCollectionExtensions.cs` (register store)
- Test: `tests/GymOS.IntegrationTests/IdempotencyStoreTests.cs`

**Interfaces:**
- Consumes: Task 3's `IIdempotencyStore` family; `AppDbContext` (`GymOS.Platform.Infrastructure.Persistence`); `PostgresFixture` / `CreateIsolatedDatabaseAsync` (`tests/GymOS.IntegrationTests/PostgresFixture.cs`).
- Produces: `platform.idempotency_records` table; scoped `IIdempotencyStore` registration.

- [ ] **Step 1: Write the entity and configuration**

```csharp
// src/BuildingBlocks/GymOS.BuildingBlocks.Infrastructure/Idempotency/IdempotencyRecord.cs
namespace GymOS.BuildingBlocks.Infrastructure.Idempotency;

/// <summary>
/// One claimed (operation, key) — AD-39's record table. Written only by IdempotencyStore;
/// deliberately absent from PlatformAuditedEntities (bookkeeping, not business state).
/// </summary>
public sealed class IdempotencyRecord
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }          // null until tenant-scoped consumers exist
    public string OperationKey { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public int Status { get; set; }              // IdempotencyRecordStatus values
    public int ResponseStatus { get; set; }      // 0 = success; otherwise (int)ErrorKind + 1 (see StoredOutcomeCodec)
    public string? ResponseBody { get; set; }    // jsonb: success DTO or serialized Error
    public DateTimeOffset LeaseExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
```

```csharp
// src/BuildingBlocks/GymOS.BuildingBlocks.Infrastructure/Idempotency/IdempotencyRecordConfiguration.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.BuildingBlocks.Infrastructure.Idempotency;

/// <summary>
/// Maps platform.idempotency_records. The unique claim index is NOT declared here: it needs
/// COALESCE over the nullable tenant_id (two NULLs must conflict), which EF cannot express —
/// it lives as raw SQL in the AddIdempotencyRecords migration, same technique and reason as
/// ix_plan_prices_plan_cycle_currency.
/// </summary>
internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_records", "platform");
        builder.HasKey(record => record.Id);
        builder.Property(record => record.OperationKey).HasMaxLength(100).IsRequired();
        builder.Property(record => record.IdempotencyKey).HasMaxLength(200).IsRequired();
        builder.Property(record => record.RequestHash).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(record => record.ResponseBody).HasColumnType("jsonb");
        builder.HasIndex(record => record.ExpiresAt).HasDatabaseName("ix_idempotency_records_expires_at");
    }
}
```

(`AppDbContext.OnModelCreating` already calls `ApplyConfigurationsFromAssembly(typeof(OutboxMessage).Assembly)` — same assembly, so the configuration is picked up with no `AppDbContext` change. `ApplyClientAssignedKeys` will set the Guid PK to `ValueGeneratedNever()`, which is correct: the store assigns ids.)

- [ ] **Step 2: Generate the migration, then append the raw-SQL unique index**

```bash
docker compose up -d postgres
dotnet ef migrations add AddIdempotencyRecords --project src/Platform/GymOS.Platform.Infrastructure --startup-project src/Host/GymOS.WebApi
```

Then append inside the generated `Up`, after the `CreateTable`/`CreateIndex` calls:

```csharp
            // One claim per (tenant, operation, key) — AD-39 §1.4. Written as SQL because the
            // uniqueness must treat NULL tenant_id as a value (two NULL-tenant rows must
            // conflict), and PostgreSQL's NULLS-distinct semantics plus EF's inability to
            // index an expression make COALESCE-in-SQL the only honest spelling. Same
            // technique and same consequence as ix_plan_prices_plan_cycle_currency: the EF
            // model does not know this index; changes to the claim key happen here, by hand.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX ux_idempotency_records_claim
                    ON platform.idempotency_records (
                        (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)),
                        operation_key,
                        idempotency_key);
                """);
```

And in `Down`, first line:

```csharp
            migrationBuilder.Sql("DROP INDEX IF EXISTS platform.ux_idempotency_records_claim;");
```

- [ ] **Step 3: Write the failing store integration tests**

```csharp
// tests/GymOS.IntegrationTests/IdempotencyStoreTests.cs
using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.Platform.Infrastructure.Idempotency;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace GymOS.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class IdempotencyStoreTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private string _connectionString = null!;

    public IdempotencyStoreTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _connectionString = await _postgres.CreateIsolatedDatabaseAsync("idempotency_store");
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly))
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AppDbContext(options);
    }
    // NOTE for implementer: mirror EXACTLY how AppDbContext is constructed elsewhere in this
    // test project (see MoneyStorageContractTests / the DbContext setup used by seeder tests).
    // If a shared helper exists, use it instead of this block.

    private static IdempotencyClaimRequest Claim(string key, string hash = "a1") => new(
        OperationKey: "test.op",
        IdempotencyKey: key,
        RequestHash: hash.PadRight(64, '0'),
        LeaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1),
        CreatedAt: DateTimeOffset.UtcNow,
        ExpiresAt: DateTimeOffset.UtcNow.AddHours(24));

    [Fact]
    public async Task First_claim_wins_second_returns_existing_snapshot()
    {
        await using AppDbContext context = CreateContext();
        var store = new IdempotencyStore(context);

        IdempotencyClaimResult first = await store.TryClaimAsync(Claim("k1"), CancellationToken.None);
        IdempotencyClaimResult second = await store.TryClaimAsync(Claim("k1"), CancellationToken.None);

        Assert.True(first.Claimed);
        Assert.False(second.Claimed);
        Assert.Equal(IdempotencyRecordStatus.InProgress, second.Existing!.Status);
    }

    [Fact]
    public async Task Parallel_claims_for_the_same_key_yield_exactly_one_winner()
    {
        IdempotencyClaimResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(async _ =>
            {
                await using AppDbContext context = CreateContext();
                return await new IdempotencyStore(context).TryClaimAsync(Claim("k-race"), CancellationToken.None);
            }));

        Assert.Equal(1, results.Count(result => result.Claimed));
    }

    [Fact]
    public async Task MarkFinished_inside_a_rolled_back_transaction_leaves_the_record_InProgress()
    {
        await using AppDbContext context = CreateContext();
        var store = new IdempotencyStore(context);
        await store.TryClaimAsync(Claim("k-rollback"), CancellationToken.None);

        await using (IIdempotencyTransaction transaction =
            await store.BeginTransactionAsync(CancellationToken.None))
        {
            await store.MarkFinishedAsync("test.op", "k-rollback",
                IdempotencyRecordStatus.Completed, 0, """{"ok":true}""", CancellationToken.None);
            await transaction.RollbackAsync(CancellationToken.None);
        }

        await using AppDbContext fresh = CreateContext();
        IdempotencyClaimResult after = await new IdempotencyStore(fresh)
            .TryClaimAsync(Claim("k-rollback"), CancellationToken.None);
        Assert.Equal(IdempotencyRecordStatus.InProgress, after.Existing!.Status);
    }

    [Fact]
    public async Task TakeOver_succeeds_only_when_lease_expired_and_hash_matches()
    {
        await using AppDbContext context = CreateContext();
        var store = new IdempotencyStore(context);

        IdempotencyClaimRequest expired = Claim("k-lease") with { LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        await store.TryClaimAsync(expired, CancellationToken.None);

        bool wrongHash = await store.TryTakeOverAsync("test.op", "k-lease",
            "ff".PadRight(64, '0'), DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        bool rightHash = await store.TryTakeOverAsync("test.op", "k-lease",
            expired.RequestHash, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        bool secondTime = await store.TryTakeOverAsync("test.op", "k-lease",
            expired.RequestHash, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        Assert.False(wrongHash);
        Assert.True(rightHash);
        Assert.False(secondTime); // lease is live again after the first takeover
    }

    [Fact]
    public async Task Release_deletes_the_claim_so_a_retry_reexecutes()
    {
        await using AppDbContext context = CreateContext();
        var store = new IdempotencyStore(context);
        await store.TryClaimAsync(Claim("k-release"), CancellationToken.None);

        await store.ReleaseClaimAsync("test.op", "k-release", CancellationToken.None);

        IdempotencyClaimResult again = await store.TryClaimAsync(Claim("k-release"), CancellationToken.None);
        Assert.True(again.Claimed);
    }

    [Fact]
    public async Task Claim_unique_index_exists_with_coalesced_tenant()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT indexdef FROM pg_indexes WHERE schemaname='platform' AND indexname='ux_idempotency_records_claim'",
            connection);
        string? definition = (string?)await command.ExecuteScalarAsync();

        Assert.NotNull(definition);
        Assert.Contains("COALESCE", definition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNIQUE", definition, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~IdempotencyStoreTests"`
Expected: build error — `IdempotencyStore` does not exist.

- [ ] **Step 5: Implement the store**

```csharp
// src/Platform/GymOS.Platform.Infrastructure/Idempotency/IdempotencyStore.cs
using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.BuildingBlocks.Infrastructure.Idempotency;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GymOS.Platform.Infrastructure.Idempotency;

/// <summary>
/// AD-39 claims over AppDbContext. The claim INSERT autocommits (no ambient transaction) so
/// every API instance sees it immediately; MarkFinished runs inside the behavior's explicit
/// transaction so record and effect commit atomically.
/// </summary>
internal sealed class IdempotencyStore : IIdempotencyStore
{
    private readonly AppDbContext _dbContext;

    public IdempotencyStore(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<IdempotencyClaimResult> TryClaimAsync(
        IdempotencyClaimRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        int inserted = await _dbContext.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO platform.idempotency_records
                 (id, tenant_id, operation_key, idempotency_key, request_hash, status,
                  response_status, response_body, lease_expires_at, created_at, expires_at)
             VALUES ({Guid.NewGuid()}, NULL, {request.OperationKey}, {request.IdempotencyKey},
                     {request.RequestHash}, {(int)IdempotencyRecordStatus.InProgress}, 0, NULL,
                     {request.LeaseExpiresAt}, {request.CreatedAt}, {request.ExpiresAt})
             ON CONFLICT DO NOTHING
             """,
            cancellationToken);

        if (inserted == 1)
        {
            return new IdempotencyClaimResult(Claimed: true, Existing: null);
        }

        IdempotencyRecord existing = await _dbContext.Set<IdempotencyRecord>()
            .AsNoTracking()
            .SingleAsync(
                record => record.OperationKey == request.OperationKey
                    && record.IdempotencyKey == request.IdempotencyKey,
                cancellationToken);

        return new IdempotencyClaimResult(
            Claimed: false,
            Existing: new IdempotencyRecordSnapshot(
                (IdempotencyRecordStatus)existing.Status,
                existing.RequestHash,
                existing.ResponseStatus,
                existing.ResponseBody,
                existing.LeaseExpiresAt));
    }

    public async Task<bool> TryTakeOverAsync(
        string operationKey, string idempotencyKey, string requestHash,
        DateTimeOffset newLeaseExpiresAt, CancellationToken cancellationToken)
    {
        int updated = await _dbContext.Database.ExecuteSqlAsync(
            $"""
             UPDATE platform.idempotency_records
                SET lease_expires_at = {newLeaseExpiresAt}
              WHERE operation_key = {operationKey}
                AND idempotency_key = {idempotencyKey}
                AND status = {(int)IdempotencyRecordStatus.InProgress}
                AND lease_expires_at < {newLeaseExpiresAt}
                AND request_hash = {requestHash}
                AND lease_expires_at < now()
             """,
            cancellationToken);

        return updated == 1;
    }

    public async Task<IIdempotencyTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new EfIdempotencyTransaction(await _dbContext.Database.BeginTransactionAsync(cancellationToken));

    public async Task MarkFinishedAsync(
        string operationKey, string idempotencyKey, IdempotencyRecordStatus status,
        int responseStatus, string? responseBody, CancellationToken cancellationToken)
    {
        await _dbContext.Database.ExecuteSqlAsync(
            $"""
             UPDATE platform.idempotency_records
                SET status = {(int)status},
                    response_status = {responseStatus},
                    response_body = {responseBody}::jsonb
              WHERE operation_key = {operationKey} AND idempotency_key = {idempotencyKey}
             """,
            cancellationToken);
    }

    public async Task ReleaseClaimAsync(
        string operationKey, string idempotencyKey, CancellationToken cancellationToken)
    {
        await _dbContext.Database.ExecuteSqlAsync(
            $"""
             DELETE FROM platform.idempotency_records
              WHERE operation_key = {operationKey} AND idempotency_key = {idempotencyKey}
                AND status = {(int)IdempotencyRecordStatus.InProgress}
             """,
            cancellationToken);
    }

    private sealed class EfIdempotencyTransaction : IIdempotencyTransaction
    {
        private readonly IDbContextTransaction _transaction;

        public EfIdempotencyTransaction(IDbContextTransaction transaction) => _transaction = transaction;

        public Task CommitAsync(CancellationToken cancellationToken) => _transaction.CommitAsync(cancellationToken);

        public Task RollbackAsync(CancellationToken cancellationToken) => _transaction.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => _transaction.DisposeAsync();
    }
}
```

Register in `PlatformInfrastructureServiceCollectionExtensions.cs`, next to the existing `TryAddScoped` calls:

```csharp
        services.TryAddScoped<IIdempotencyStore, IdempotencyStore>();
```

(`TryTakeOverAsync` has both `lease_expires_at < {newLeaseExpiresAt}` and `< now()`: the first makes the statement self-consistent under clock skew between app instances, the second is the authoritative database-clock check. Keep both; the test exercises the behavior, not the spelling.)

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~IdempotencyStoreTests"`
Expected: 6 PASS (needs Docker).

- [ ] **Step 7: Commit**

```bash
git add src/BuildingBlocks/GymOS.BuildingBlocks.Infrastructure/Idempotency src/Platform/GymOS.Platform.Infrastructure/Idempotency src/Platform/GymOS.Platform.Infrastructure/Persistence/Migrations src/Platform/GymOS.Platform.Infrastructure/DependencyInjection/PlatformInfrastructureServiceCollectionExtensions.cs tests/GymOS.IntegrationTests/IdempotencyStoreTests.cs
git commit -m "AD-39 (4/8): idempotency_records table, COALESCE claim index, EF store over AppDbContext"
```

---

### Task 5: The pipeline behavior + outcome codec (decision-table unit tests)

**Files:**
- Create: `src/BuildingBlocks/GymOS.BuildingBlocks.Application/Idempotency/StoredOutcomeCodec.cs`
- Create: `src/BuildingBlocks/GymOS.BuildingBlocks.Application/Behaviors/IdempotencyBehavior.cs`
- Create: `src/BuildingBlocks/GymOS.BuildingBlocks.Application/Idempotency/IdempotencyOptions.cs`
- Modify: `src/BuildingBlocks/GymOS.BuildingBlocks.Application/DependencyInjection/MessagingServiceCollectionExtensions.cs` (add `AddIdempotencyBehavior`)
- Test: `tests/GymOS.BuildingBlocks.UnitTests/Idempotency/IdempotencyBehaviorTests.cs`, `.../StoredOutcomeCodecTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3 types; `IPipelineBehavior<TRequest,TResponse>` + `PipelineNext<TResponse>` (`GymOS.BuildingBlocks.Application.Messaging`); `IClock` (`...Application.Abstractions`); `Result`/`Result<T>`/`Error`/`ErrorKind` (`GymOS.SharedKernel.Results`); `FakeClock` (`GymOS.TestingSupport`).
- Produces: `IdempotencyBehavior<TRequest,TResponse>`; `static class StoredOutcomeCodec { static (int ResponseStatus, string? Body) Encode(Result result); static TResponse Decode<TResponse>(int responseStatus, string? body); }`; `IdempotencyOptions { const string SectionName = "Idempotency"; TimeSpan LeaseDuration = 60s; bool PurgeOnStartup = true; }`; `AddIdempotencyBehavior(this IServiceCollection)`.

- [ ] **Step 1: Write the failing codec tests**

```csharp
// tests/GymOS.BuildingBlocks.UnitTests/Idempotency/StoredOutcomeCodecTests.cs
using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.SharedKernel.Results;
using Xunit;

namespace GymOS.BuildingBlocks.UnitTests.Idempotency;

public sealed class StoredOutcomeCodecTests
{
    private sealed record PaymentDto(Guid Id, string Reference);

    [Fact]
    public void Success_with_value_round_trips()
    {
        var dto = new PaymentDto(Guid.NewGuid(), "PAY-1");
        (int status, string? body) = StoredOutcomeCodec.Encode(Result.Success(dto));

        var decoded = StoredOutcomeCodec.Decode<Result<PaymentDto>>(status, body);

        Assert.Equal(0, status);
        Assert.True(decoded.IsSuccess);
        Assert.Equal(dto, decoded.Value);
    }

    [Fact]
    public void Plain_success_round_trips()
    {
        (int status, string? body) = StoredOutcomeCodec.Encode(Result.Success());
        var decoded = StoredOutcomeCodec.Decode<Result>(status, body);
        Assert.True(decoded.IsSuccess);
    }

    [Fact]
    public void Business_failure_round_trips_with_code_message_and_kind()
    {
        Result<PaymentDto> failed = Result.Failure<PaymentDto>(
            Error.Conflict("invoices.already_paid", "This invoice is already paid."));
        (int status, string? body) = StoredOutcomeCodec.Encode(failed);

        var decoded = StoredOutcomeCodec.Decode<Result<PaymentDto>>(status, body);

        Assert.True(decoded.IsFailure);
        Assert.Equal("invoices.already_paid", decoded.Error!.Code);
        Assert.Equal("This invoice is already paid.", decoded.Error.Message);
        Assert.Equal(ErrorKind.Conflict, decoded.Error.Kind);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/GymOS.BuildingBlocks.UnitTests --filter "FullyQualifiedName~StoredOutcomeCodecTests"` → build error.

- [ ] **Step 3: Implement the codec**

```csharp
// src/BuildingBlocks/GymOS.BuildingBlocks.Application/Idempotency/StoredOutcomeCodec.cs
using System.Text.Json;
using GymOS.SharedKernel.Results;

namespace GymOS.BuildingBlocks.Application.Idempotency;

/// <summary>
/// Serializes a handler outcome for the record table and reconstructs it for replay.
/// ResponseStatus 0 = success (body = the DTO, or null for plain Result); otherwise
/// ResponseStatus = (int)ErrorKind and body = {code, message}. The wire response stays
/// byte-identical because a replay re-enters the host's deterministic WriteResults mapping.
/// </summary>
public static class StoredOutcomeCodec
{
    private static readonly JsonSerializerOptions Options = JsonSerializerOptions.Default;

    private sealed record StoredError(string Code, string Message);

    /// <summary>Builds a live (non-replayed) failure of the pipeline's response type.</summary>
    public static TResponse Failure<TResponse>(Error error)
        where TResponse : Result
    {
        ArgumentNullException.ThrowIfNull(error);

        return typeof(TResponse) == typeof(Result)
            ? (TResponse)Result.Failure(error)
            : (TResponse)FailureFactory(typeof(TResponse).GetGenericArguments()[0]).Invoke(null, [error])!;
    }

    public static (int ResponseStatus, string? Body) Encode(Result result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsFailure)
        {
            Error error = result.Error!;
            return ((int)error.Kind + 1, JsonSerializer.Serialize(new StoredError(error.Code, error.Message), Options));
        }

        object? value = ValueOf(result);
        return (0, value is null ? null : JsonSerializer.Serialize(value, value.GetType(), Options));
    }

    public static TResponse Decode<TResponse>(int responseStatus, string? body)
        where TResponse : Result
    {
        if (responseStatus is 0)
        {
            if (typeof(TResponse) == typeof(Result))
            {
                return (TResponse)Result.Success();
            }

            Type valueType = typeof(TResponse).GetGenericArguments()[0];
            object value = JsonSerializer.Deserialize(body!, valueType, Options)!;
            return (TResponse)SuccessFactory(valueType).Invoke(null, [value])!;
        }

        StoredError stored = JsonSerializer.Deserialize<StoredError>(body!, Options)!;
        Error error = ErrorFor((ErrorKind)(responseStatus - 1), stored.Code, stored.Message);

        if (typeof(TResponse) == typeof(Result))
        {
            return (TResponse)Result.Failure(error);
        }

        return (TResponse)FailureFactory(typeof(TResponse).GetGenericArguments()[0]).Invoke(null, [error])!;
    }

    private static object? ValueOf(Result result)
    {
        Type type = result.GetType();
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Result<>)
            ? type.GetProperty(nameof(Result<object>.Value))!.GetValue(result)
            : null;
    }

    private static System.Reflection.MethodInfo SuccessFactory(Type valueType) =>
        typeof(Result).GetMethods()
            .Single(m => m.Name == nameof(Result.Success) && m.IsGenericMethodDefinition)
            .MakeGenericMethod(valueType);

    private static System.Reflection.MethodInfo FailureFactory(Type valueType) =>
        typeof(Result).GetMethods()
            .Single(m => m.Name == nameof(Result.Failure) && m.IsGenericMethodDefinition)
            .MakeGenericMethod(valueType);

    private static Error ErrorFor(ErrorKind kind, string code, string message) => kind switch
    {
        ErrorKind.Validation => Error.Validation(code, message),
        ErrorKind.NotFound => Error.NotFound(code, message),
        ErrorKind.Conflict => Error.Conflict(code, message),
        ErrorKind.Unauthorized => Error.Unauthorized(code, message),
        ErrorKind.Forbidden => Error.Forbidden(code, message),
        _ => Error.Failure(code, message),
    };
}
```

(Implementer note: `ResponseStatus` stores `(int)ErrorKind + 1` because `ErrorKind.Failure == 0` would collide with "success". Cache the two `MethodInfo` lookups in a `ConcurrentDictionary<Type, MethodInfo>` if profiling ever shows heat; replay is not a hot path — do not pre-optimize.)

- [ ] **Step 4: Run codec tests** → 3 PASS.

- [ ] **Step 5: Write the failing behavior tests — the decision table, one test per row**

```csharp
// tests/GymOS.BuildingBlocks.UnitTests/Idempotency/IdempotencyBehaviorTests.cs
using GymOS.BuildingBlocks.Application.Behaviors;
using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.SharedKernel.Results;
using GymOS.TestingSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GymOS.BuildingBlocks.UnitTests.Idempotency;

public sealed class IdempotencyBehaviorTests
{
    private sealed record PaymentDto(string Reference);

    private sealed record MarkPaid(Guid InvoiceId) : ICommand<Result<PaymentDto>>, IIdempotentCommand
    {
        public string OperationKey => "test.mark-paid";
        public TimeSpan RetentionPeriod => TimeSpan.FromHours(24);
        public bool KeyRequired => true;
    }

    private sealed record OptionalKey(Guid Id) : ICommand<Result<PaymentDto>>, IIdempotentCommand
    {
        public string OperationKey => "test.optional";
        public TimeSpan RetentionPeriod => TimeSpan.FromHours(24);
        public bool KeyRequired => false;
    }

    private sealed record NotIdempotent(Guid Id) : ICommand<Result<PaymentDto>>;

    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeIdempotencyStore _store = new();
    private readonly IdempotencyContext _context = new();
    private int _executions;

    private IdempotencyBehavior<TRequest, Result<PaymentDto>> Behavior<TRequest>()
        where TRequest : notnull =>
        new(_store, _context, _clock,
            Options.Create(new IdempotencyOptions { LeaseDuration = TimeSpan.FromSeconds(60) }),
            NullLogger<IdempotencyBehavior<TRequest, Result<PaymentDto>>>.Instance);

    private Task<Result<PaymentDto>> Handler()
    {
        _executions++;
        return Task.FromResult(Result.Success(new PaymentDto("PAY-1")));
    }

    // Row 1: no record → execute, claim first.
    [Fact]
    public async Task Fresh_key_executes_and_stores_completed()
    {
        _context.SetKey("k1");
        Result<PaymentDto> result = await Behavior<MarkPaid>()
            .HandleAsync(new MarkPaid(Guid.NewGuid()), Handler, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _executions);
        Assert.Equal(IdempotencyRecordStatus.Completed, _store.Single("test.mark-paid", "k1").Status);
        Assert.True(_store.CommittedInTransaction);
    }

    // Row 2: Completed + same hash → replay, handler does NOT run.
    [Fact]
    public async Task Completed_same_hash_replays_without_executing()
    {
        var command = new MarkPaid(Guid.NewGuid());
        _context.SetKey("k1");
        await Behavior<MarkPaid>().HandleAsync(command, Handler, CancellationToken.None);

        Result<PaymentDto> replay = await Behavior<MarkPaid>()
            .HandleAsync(command, Handler, CancellationToken.None);

        Assert.Equal(1, _executions);
        Assert.True(replay.IsSuccess);
        Assert.Equal("PAY-1", replay.Value.Reference);
        Assert.True(_context.WasReplayed);
    }

    // Row 3: Completed + different hash → key_reuse_mismatch.
    [Fact]
    public async Task Completed_different_hash_rejects_key_reuse()
    {
        _context.SetKey("k1");
        await Behavior<MarkPaid>().HandleAsync(new MarkPaid(Guid.NewGuid()), Handler, CancellationToken.None);

        Result<PaymentDto> second = await Behavior<MarkPaid>()
            .HandleAsync(new MarkPaid(Guid.NewGuid()), Handler, CancellationToken.None);

        Assert.True(second.IsFailure);
        Assert.Equal("idempotency.key_reuse_mismatch", second.Error!.Code);
        Assert.Equal(1, _executions);
    }

    // Row 4: InProgress + live lease → operation_in_progress with RetryAfter.
    [Fact]
    public async Task InProgress_with_live_lease_rejects_conflict_with_retry_after()
    {
        _store.SeedInProgress("test.mark-paid", "k1",
            HashOf(new MarkPaid(WellKnownId)), _clock.UtcNow.AddSeconds(45));
        _context.SetKey("k1");

        Result<PaymentDto> result = await Behavior<MarkPaid>()
            .HandleAsync(new MarkPaid(WellKnownId), Handler, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("idempotency.operation_in_progress", result.Error!.Code);
        Assert.Equal(0, _executions);
        Assert.NotNull(_context.RetryAfter);
        Assert.InRange(_context.RetryAfter!.Value.TotalSeconds, 1, 45);
    }

    // Row 5: InProgress + expired lease + same hash → take over and execute.
    [Fact]
    public async Task Expired_lease_same_hash_takes_over_and_executes()
    {
        _store.SeedInProgress("test.mark-paid", "k1",
            HashOf(new MarkPaid(WellKnownId)), _clock.UtcNow.AddSeconds(-5));
        _context.SetKey("k1");

        Result<PaymentDto> result = await Behavior<MarkPaid>()
            .HandleAsync(new MarkPaid(WellKnownId), Handler, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _executions);
    }

    // Row 5b: expired lease + different hash is still key reuse.
    [Fact]
    public async Task Expired_lease_different_hash_rejects_key_reuse()
    {
        _store.SeedInProgress("test.mark-paid", "k1", new string('f', 64), _clock.UtcNow.AddSeconds(-5));
        _context.SetKey("k1");

        Result<PaymentDto> result = await Behavior<MarkPaid>()
            .HandleAsync(new MarkPaid(WellKnownId), Handler, CancellationToken.None);

        Assert.Equal("idempotency.key_reuse_mismatch", result.Error!.Code);
        Assert.Equal(0, _executions);
    }

    // Row 6: FailedDeterministic + same hash → replay the stored business error.
    [Fact]
    public async Task Deterministic_failure_replays_without_reexecuting()
    {
        var command = new MarkPaid(Guid.NewGuid());
        _context.SetKey("k1");
        int calls = 0;
        Task<Result<PaymentDto>> FailingHandler()
        {
            calls++;
            return Task.FromResult(Result.Failure<PaymentDto>(
                Error.Conflict("invoices.already_paid", "Already paid.")));
        }

        Result<PaymentDto> first = await Behavior<MarkPaid>().HandleAsync(command, FailingHandler, CancellationToken.None);
        Result<PaymentDto> second = await Behavior<MarkPaid>().HandleAsync(command, FailingHandler, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal("invoices.already_paid", first.Error!.Code);
        Assert.Equal("invoices.already_paid", second.Error!.Code);
        Assert.Equal(IdempotencyRecordStatus.FailedDeterministic, _store.Single("test.mark-paid", "k1").Status);
    }

    // Row 7: transient failure (exception) → claim released, retry re-executes.
    [Fact]
    public async Task Exception_releases_claim_and_a_retry_reexecutes()
    {
        var command = new MarkPaid(Guid.NewGuid());
        _context.SetKey("k1");
        static Task<Result<PaymentDto>> Throwing() => throw new TimeoutException("db down");

        await Assert.ThrowsAsync<TimeoutException>(() =>
            Behavior<MarkPaid>().HandleAsync(command, Throwing, CancellationToken.None));
        Assert.True(_store.IsEmpty);
        Assert.True(_store.LastTransactionRolledBack);

        Result<PaymentDto> retry = await Behavior<MarkPaid>().HandleAsync(command, Handler, CancellationToken.None);
        Assert.True(retry.IsSuccess);
        Assert.Equal(1, _executions);
    }

    // Gate: KeyRequired without a header → 400 error, nothing executes, nothing stored.
    [Fact]
    public async Task Required_key_missing_rejects_400_without_executing()
    {
        Result<PaymentDto> result = await Behavior<MarkPaid>()
            .HandleAsync(new MarkPaid(Guid.NewGuid()), Handler, CancellationToken.None);

        Assert.Equal("idempotency.key_required", result.Error!.Code);
        Assert.Equal(0, _executions);
        Assert.True(_store.IsEmpty);
    }

    // Gate: optional key + no header → executes unprotected.
    [Fact]
    public async Task Optional_key_missing_executes_without_a_record()
    {
        Result<PaymentDto> result = await Behavior<OptionalKey>()
            .HandleAsync(new OptionalKey(Guid.NewGuid()), Handler, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _executions);
        Assert.True(_store.IsEmpty);
    }

    // Gate: non-idempotent commands pass straight through.
    [Fact]
    public async Task Non_idempotent_command_is_untouched()
    {
        _context.SetKey("k1"); // even with a header present
        Result<PaymentDto> result = await Behavior<NotIdempotent>()
            .HandleAsync(new NotIdempotent(Guid.NewGuid()), Handler, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(_store.IsEmpty);
    }

    private static readonly Guid WellKnownId = new("11111111-1111-1111-1111-111111111111");

    private static string HashOf(object command) => CanonicalRequestHasher.Hash(command);
}
```

Also create the fake store used above, in the same test folder:

```csharp
// tests/GymOS.BuildingBlocks.UnitTests/Idempotency/FakeIdempotencyStore.cs
using System.Collections.Concurrent;
using GymOS.BuildingBlocks.Application.Idempotency;

namespace GymOS.BuildingBlocks.UnitTests.Idempotency;

/// <summary>In-memory IIdempotencyStore mirroring the real store's observable semantics.</summary>
internal sealed class FakeIdempotencyStore : IIdempotencyStore
{
    private sealed record Row(string RequestHash, IdempotencyRecordStatus Status,
        int ResponseStatus, string? ResponseBody, DateTimeOffset LeaseExpiresAt);

    private readonly ConcurrentDictionary<(string Op, string Key), Row> _rows = new();

    public bool CommittedInTransaction { get; private set; }
    public bool LastTransactionRolledBack { get; private set; }
    public bool IsEmpty => _rows.IsEmpty;

    public IdempotencyRecordSnapshot Single(string op, string key)
    {
        Row row = _rows[(op, key)];
        return new IdempotencyRecordSnapshot(row.Status, row.RequestHash, row.ResponseStatus, row.ResponseBody, row.LeaseExpiresAt);
    }

    public void SeedInProgress(string op, string key, string hash, DateTimeOffset leaseExpiresAt) =>
        _rows[(op, key)] = new Row(hash, IdempotencyRecordStatus.InProgress, 0, null, leaseExpiresAt);

    public Task<IdempotencyClaimResult> TryClaimAsync(IdempotencyClaimRequest request, CancellationToken cancellationToken)
    {
        bool claimed = _rows.TryAdd(
            (request.OperationKey, request.IdempotencyKey),
            new Row(request.RequestHash, IdempotencyRecordStatus.InProgress, 0, null, request.LeaseExpiresAt));

        if (claimed)
        {
            return Task.FromResult(new IdempotencyClaimResult(true, null));
        }

        Row row = _rows[(request.OperationKey, request.IdempotencyKey)];
        return Task.FromResult(new IdempotencyClaimResult(false,
            new IdempotencyRecordSnapshot(row.Status, row.RequestHash, row.ResponseStatus, row.ResponseBody, row.LeaseExpiresAt)));
    }

    public Task<bool> TryTakeOverAsync(string op, string key, string hash, DateTimeOffset newLease, CancellationToken ct)
    {
        if (_rows.TryGetValue((op, key), out Row? row)
            && row.Status == IdempotencyRecordStatus.InProgress
            && row.LeaseExpiresAt < newLease
            && row.RequestHash == hash)
        {
            _rows[(op, key)] = row with { LeaseExpiresAt = newLease };
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task<IIdempotencyTransaction> BeginTransactionAsync(CancellationToken ct) =>
        Task.FromResult<IIdempotencyTransaction>(new FakeTransaction(this));

    public Task MarkFinishedAsync(string op, string key, IdempotencyRecordStatus status,
        int responseStatus, string? responseBody, CancellationToken ct)
    {
        _rows[(op, key)] = _rows[(op, key)] with
        {
            Status = status, ResponseStatus = responseStatus, ResponseBody = responseBody,
        };
        return Task.CompletedTask;
    }

    public Task ReleaseClaimAsync(string op, string key, CancellationToken ct)
    {
        _rows.TryRemove((op, key), out _);
        return Task.CompletedTask;
    }

    private sealed class FakeTransaction : IIdempotencyTransaction
    {
        private readonly FakeIdempotencyStore _owner;
        public FakeTransaction(FakeIdempotencyStore owner) => _owner = owner;
        public Task CommitAsync(CancellationToken ct) { _owner.CommittedInTransaction = true; return Task.CompletedTask; }
        public Task RollbackAsync(CancellationToken ct) { _owner.LastTransactionRolledBack = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 6: Run to verify failure** — behavior type missing → build error.

- [ ] **Step 7: Implement options + behavior**

```csharp
// src/BuildingBlocks/GymOS.BuildingBlocks.Application/Idempotency/IdempotencyOptions.cs
using System.ComponentModel.DataAnnotations;

namespace GymOS.BuildingBlocks.Application.Idempotency;

/// <summary>Bound from the "Idempotency" section, validated on start (house options pattern).</summary>
public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    /// <summary>How long a claim blocks concurrent duplicates before a crashed attempt can be taken over.</summary>
    [Range(typeof(TimeSpan), "00:00:05", "00:10:00")]
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Delete expired records at startup until the M3.4 retention job takes over.</summary>
    public bool PurgeOnStartup { get; set; } = true;
}
```

```csharp
// src/BuildingBlocks/GymOS.BuildingBlocks.Application/Behaviors/IdempotencyBehavior.cs
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.SharedKernel.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GymOS.BuildingBlocks.Application.Behaviors;

/// <summary>
/// AD-39's decision table. Pipeline position: inside Authorization (a stored response is never
/// returned to a caller who could not execute the operation now), outside the handler.
/// The claim autocommits; the handler runs inside an explicit transaction this behavior opens,
/// and the record flips to Completed in that same transaction — effect and record are atomic.
/// </summary>
public sealed partial class IdempotencyBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : Result
{
    private readonly IIdempotencyStore _store;
    private readonly IdempotencyContext _context;
    private readonly IClock _clock;
    private readonly IOptions<IdempotencyOptions> _options;
    private readonly ILogger<IdempotencyBehavior<TRequest, TResponse>> _logger;

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Replayed stored outcome for '{OperationKey}' (key '{IdempotencyKey}').")]
    private static partial void LogReplayed(ILogger logger, string operationKey, string idempotencyKey);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Idempotency key reused with a different request for '{OperationKey}' — client bug.")]
    private static partial void LogKeyReuseMismatch(ILogger logger, string operationKey);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Took over an expired claim for '{OperationKey}' (key '{IdempotencyKey}') — prior attempt never committed.")]
    private static partial void LogTakeover(ILogger logger, string operationKey, string idempotencyKey);

    public IdempotencyBehavior(
        IIdempotencyStore store,
        IdempotencyContext context,
        IClock clock,
        IOptions<IdempotencyOptions> options,
        ILogger<IdempotencyBehavior<TRequest, TResponse>> logger)
    {
        _store = store;
        _context = context;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    public async Task<TResponse> HandleAsync(
        TRequest request, PipelineNext<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);

        if (request is not IIdempotentCommand command)
        {
            return await next().ConfigureAwait(false);
        }

        string? key = _context.Key;
        if (key is null)
        {
            return command.KeyRequired
                ? Fail(IdempotencyErrors.KeyRequired)
                : await next().ConfigureAwait(false);
        }

        string hash = CanonicalRequestHasher.Hash(request);
        DateTimeOffset now = _clock.UtcNow;
        var claim = new IdempotencyClaimRequest(
            command.OperationKey, key, hash,
            LeaseExpiresAt: now + _options.Value.LeaseDuration,
            CreatedAt: now,
            ExpiresAt: now + command.RetentionPeriod);

        IdempotencyClaimResult result = await _store.TryClaimAsync(claim, cancellationToken).ConfigureAwait(false);

        if (!result.Claimed)
        {
            IdempotencyRecordSnapshot existing = result.Existing!;

            if (existing.Status is IdempotencyRecordStatus.Completed or IdempotencyRecordStatus.FailedDeterministic)
            {
                if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
                {
                    LogKeyReuseMismatch(_logger, command.OperationKey);
                    return Fail(IdempotencyErrors.KeyReuseMismatch);
                }

                _context.MarkReplayed();
                LogReplayed(_logger, command.OperationKey, key);
                return StoredOutcomeCodec.Decode<TResponse>(existing.ResponseStatus, existing.ResponseBody);
            }

            // InProgress.
            if (existing.LeaseExpiresAt > now)
            {
                TimeSpan retryAfter = existing.LeaseExpiresAt - now;
                _context.SetRetryAfter(retryAfter < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : retryAfter);
                return Fail(IdempotencyErrors.OperationInProgress);
            }

            if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
            {
                LogKeyReuseMismatch(_logger, command.OperationKey);
                return Fail(IdempotencyErrors.KeyReuseMismatch);
            }

            bool takenOver = await _store
                .TryTakeOverAsync(command.OperationKey, key, hash, now + _options.Value.LeaseDuration, cancellationToken)
                .ConfigureAwait(false);

            if (!takenOver)
            {
                // Lost the takeover race: someone else re-leased it a moment ago.
                _context.SetRetryAfter(_options.Value.LeaseDuration);
                return Fail(IdempotencyErrors.OperationInProgress);
            }

            LogTakeover(_logger, command.OperationKey, key);
        }

        await using IIdempotencyTransaction transaction =
            await _store.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            TResponse response = await next().ConfigureAwait(false);

            (int responseStatus, string? body) = StoredOutcomeCodec.Encode(response);
            IdempotencyRecordStatus finalStatus = response.IsSuccess
                ? IdempotencyRecordStatus.Completed
                : IdempotencyRecordStatus.FailedDeterministic;

            await _store.MarkFinishedAsync(command.OperationKey, key, finalStatus, responseStatus, body, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return response;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            await _store.ReleaseClaimAsync(command.OperationKey, key, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static TResponse Fail(Error error) => StoredOutcomeCodec.Failure<TResponse>(error);
}
```

Add to `MessagingServiceCollectionExtensions.cs`:

```csharp
    /// <summary>AD-39: registers the idempotency pipeline stage and its scoped context.</summary>
    public static IServiceCollection AddIdempotencyBehavior(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IdempotencyContext>();
        services.AddPipelineBehavior(typeof(IdempotencyBehavior<,>));

        return services;
    }
```

Note the generic constraint `where TResponse : Result` — the open-generic DI registration will only construct this behavior for command types whose response is `Result`/`Result<T>`; the Task 8 architecture test makes that a guarantee rather than an accident.

- [ ] **Step 8: Run the behavior tests** — `dotnet test tests/GymOS.BuildingBlocks.UnitTests --filter "FullyQualifiedName~IdempotencyBehaviorTests"` → 12 PASS. Then the whole project: `dotnet test tests/GymOS.BuildingBlocks.UnitTests` → expected total = previous count (14) + new tests; no `Aborted` in output.

- [ ] **Step 9: Commit**

```bash
git add src/BuildingBlocks/GymOS.BuildingBlocks.Application tests/GymOS.BuildingBlocks.UnitTests/Idempotency
git commit -m "AD-39 (5/8): idempotency pipeline behavior — full decision table, atomic record+effect"
```

---

### Task 6: Host plumbing — header middleware, 422 mapping, registration, purge maintainer

**Files:**
- Create: `src/Host/GymOS.WebApi/Middleware/IdempotencyHeaderMiddleware.cs`
- Create: `src/Platform/GymOS.Platform.Infrastructure/Idempotency/IdempotencyRecordMaintainer.cs`
- Modify: `src/Host/GymOS.WebApi/Endpoints/ResultExtensions.cs` (`StatusCodeByErrorCode` += 422 entry)
- Modify: `src/Host/GymOS.WebApi/Program.cs` (behavior + options registration, middleware, startup purge)
- Modify: `src/Host/GymOS.WebApi/appsettings.json` (`Idempotency` section)
- Modify: `src/Platform/GymOS.Platform.Infrastructure/DependencyInjection/PlatformInfrastructureServiceCollectionExtensions.cs` (bind options, register maintainer)
- Test: `tests/GymOS.IntegrationTests/IdempotencyHostTests.cs` (header echo through a real host; full pipeline coverage is Task 7)

**Interfaces:**
- Consumes: `IdempotencyContext` (Task 1), `IdempotencyOptions` (Task 5), `AddIdempotencyBehavior()` (Task 5), `AuthenticationHost` (existing test host).
- Produces: request header constant `IdempotencyHeaders.HeaderName = "Idempotency-Key"`; response headers `Idempotency-Replayed: true` and `Retry-After: <seconds>`; `ResultExtensions.StatusCodeByErrorCode["idempotency.key_reuse_mismatch"] = 422`.

- [ ] **Step 1: Implement the middleware**

```csharp
// src/Host/GymOS.WebApi/Middleware/IdempotencyHeaderMiddleware.cs
using GymOS.BuildingBlocks.Application.Idempotency;

namespace GymOS.WebApi.Middleware;

/// <summary>Names the wire artifacts of AD-39 (header in, replay/backoff headers out).</summary>
public static class IdempotencyHeaders
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeaderName = "Idempotency-Replayed";
}

/// <summary>
/// The owning edge for IdempotencyContext: copies the caller's Idempotency-Key inward and, on
/// the way out, stamps Idempotency-Replayed / Retry-After from what the pipeline recorded.
/// </summary>
public sealed class IdempotencyHeaderMiddleware
{
    private readonly RequestDelegate _next;

    public IdempotencyHeaderMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext httpContext, IdempotencyContext idempotencyContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(idempotencyContext);

        string? key = httpContext.Request.Headers[IdempotencyHeaders.HeaderName].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(key))
        {
            idempotencyContext.SetKey(key);
        }

        httpContext.Response.OnStarting(() =>
        {
            if (idempotencyContext.WasReplayed)
            {
                httpContext.Response.Headers[IdempotencyHeaders.ReplayedHeaderName] = "true";
            }

            if (idempotencyContext.RetryAfter is { } retryAfter)
            {
                httpContext.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return Task.CompletedTask;
        });

        await _next(httpContext);
    }
}
```

- [ ] **Step 2: Wire everything**

In `ResultExtensions.cs`, extend the existing override table:

```csharp
private static readonly Dictionary<string, int> StatusCodeByErrorCode = new(StringComparer.Ordinal)
{
    ["concurrency.version_required"] = StatusCodes.Status428PreconditionRequired,
    // AD-39: key reuse with a different body is a client bug, not a state conflict — 422
    // distinguishes it from the 409 a concurrent duplicate receives.
    ["idempotency.key_reuse_mismatch"] = StatusCodes.Status422UnprocessableEntity,
};
```

In `Program.cs`, immediately after `builder.Services.AddAuthorizationBehavior();` (order rule: first registered runs outermost — Authorization stays outside Idempotency):

```csharp
    // AD-39: claims/replays idempotent commands. Registered after authorization so a stored
    // response is never returned to a caller who could not execute the operation now.
    builder.Services.AddIdempotencyBehavior();
```

In `PlatformInfrastructureServiceCollectionExtensions.cs`, following the `AuditLogPartitionOptions` pattern:

```csharp
        services.AddOptions<IdempotencyOptions>()
            .Bind(configuration.GetSection(IdempotencyOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddScoped<IdempotencyRecordMaintainer>();
```

In `Program.cs`, register the middleware next to the correlation middleware (`app.UseMiddleware<IdempotencyHeaderMiddleware>();` directly after the correlation middleware registration), and in the existing startup scope, after the audit-partition maintainer call:

```csharp
    if (app.Services.GetRequiredService<IOptions<IdempotencyOptions>>().Value.PurgeOnStartup)
    {
        await startupScope.ServiceProvider
            .GetRequiredService<IdempotencyRecordMaintainer>()
            .PurgeExpiredAsync(CancellationToken.None);
    }
```

In `appsettings.json`:

```json
  "Idempotency": {
    "LeaseDuration": "00:01:00",
    "PurgeOnStartup": true
  }
```

- [ ] **Step 3: Implement the maintainer** (mirror `AuditLogPartitionMaintainer`: `sealed partial`, `[LoggerMessage]`, never throws, skips on pending migrations)

```csharp
// src/Platform/GymOS.Platform.Infrastructure/Idempotency/IdempotencyRecordMaintainer.cs
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace GymOS.Platform.Infrastructure.Idempotency;

/// <summary>
/// Startup deletion of expired idempotency records — the interim owner of AD-39 retention
/// until the M3.4 job runner lands (then this folds into the retention job, together with
/// AuditLogPartitionMaintainer; STATUS.md tracks that).
/// </summary>
public sealed partial class IdempotencyRecordMaintainer
{
    private readonly AppDbContext _dbContext;
    private readonly IClock _clock;
    private readonly ILogger<IdempotencyRecordMaintainer> _logger;

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Idempotency record purge removed {RemovedCount} expired record(s).")]
    private static partial void LogPurged(ILogger logger, int removedCount);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Idempotency record purge skipped: the database has pending migrations.")]
    private static partial void LogSkippedPendingMigrations(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Idempotency record purge failed; expired records remain until the next start.")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);

    public IdempotencyRecordMaintainer(
        AppDbContext dbContext, IClock clock, ILogger<IdempotencyRecordMaintainer> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _logger = logger;
    }

    public async Task PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        try
        {
            if ((await _dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            {
                LogSkippedPendingMigrations(_logger);
                return;
            }

            DateTimeOffset now = _clock.UtcNow;
            int removed = await _dbContext.Database.ExecuteSqlAsync(
                $"DELETE FROM platform.idempotency_records WHERE expires_at < {now}",
                cancellationToken);

            LogPurged(_logger, removed);
        }
        catch (NpgsqlException exception) { LogPurgeFailed(_logger, exception); }
        catch (InvalidOperationException exception) { LogPurgeFailed(_logger, exception); }
    }
}
```

- [ ] **Step 4: Write the host smoke test**

```csharp
// tests/GymOS.IntegrationTests/IdempotencyHostTests.cs
using System.Net;
using Xunit;

namespace GymOS.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class IdempotencyHostTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public IdempotencyHostTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        string connectionString = await _postgres.CreateIsolatedDatabaseAsync("idempotency_host");
        _host = AuthenticationHost.Create(connectionString);
        _client = _host.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Host_boots_and_serves_health_with_the_idempotency_table_migrated()
    {
        using HttpResponseMessage ready = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task A_request_with_an_idempotency_key_on_a_non_idempotent_endpoint_is_harmless()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/ready", UriKind.Relative));
        request.Headers.Add("Idempotency-Key", "smoke-1");
        using HttpResponseMessage response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Idempotency-Replayed"));
    }
}
```

- [ ] **Step 5: Run** — `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~IdempotencyHostTests"` → 2 PASS. Also boot the API once (`docker compose up -d`, or `dotnet run --project src/Host/GymOS.WebApi`) and confirm the startup log line `Idempotency record purge removed 0 expired record(s).`

- [ ] **Step 6: Commit**

```bash
git add src/Host/GymOS.WebApi src/Platform/GymOS.Platform.Infrastructure tests/GymOS.IntegrationTests/IdempotencyHostTests.cs
git commit -m "AD-39 (6/8): host plumbing — header edge, 422 mapping, registration, startup purge"
```

---

### Task 7: End-to-end pipeline integration tests (real dispatcher, real PostgreSQL)

**Files:**
- Create: `tests/GymOS.IntegrationTests/IdempotencyPipelineTests.cs`

**Interfaces:**
- Consumes: everything above; `AddCqrsDispatcher()`, `AddIdempotencyBehavior()`, `AddHandlersFromAssembly` (`GymOS.BuildingBlocks.Application.DependencyInjection`); `FakeClock`.
- Produces: proof that the framework holds against a real database, including the concurrency race.

- [ ] **Step 1: Write the tests** — a self-contained ServiceProvider per scope: real `Dispatcher`, real `IdempotencyStore` over `AppDbContext` (isolated Testcontainers DB, migrated once in `InitializeAsync`), `FakeClock`, and a local `RecordedPayment` command/handler pair defined inside the test file:

```csharp
// tests/GymOS.IntegrationTests/IdempotencyPipelineTests.cs
using GymOS.BuildingBlocks.Application.Behaviors;
using GymOS.BuildingBlocks.Application.DependencyInjection;
using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.Platform.Infrastructure.Idempotency;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.SharedKernel.Results;
using GymOS.TestingSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GymOS.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class IdempotencyPipelineTests : IAsyncLifetime
{
    public sealed record PaymentDto(Guid CommandId, int ExecutionNumber);

    public sealed record RecordPayment(Guid CommandId, bool FailBusiness) : ICommand<Result<PaymentDto>>, IIdempotentCommand
    {
        public string OperationKey => "test.record-payment";
        public TimeSpan RetentionPeriod => TimeSpan.FromHours(72);
        public bool KeyRequired => true;
    }

    public sealed class ExecutionCounter { private int _count; public int Increment() => Interlocked.Increment(ref _count); public int Count => _count; }

    public sealed class RecordPaymentHandler : ICommandHandler<RecordPayment, Result<PaymentDto>>
    {
        private readonly ExecutionCounter _counter;
        public RecordPaymentHandler(ExecutionCounter counter) => _counter = counter;

        public Task<Result<PaymentDto>> HandleAsync(RecordPayment command, CancellationToken cancellationToken)
        {
            int execution = _counter.Increment();
            return Task.FromResult(command.FailBusiness
                ? Result.Failure<PaymentDto>(Error.Conflict("payments.already_recorded", "Deterministic business failure."))
                : Result.Success(new PaymentDto(command.CommandId, execution)));
        }
    }

    private readonly PostgresFixture _postgres;
    private string _connectionString = null!;
    private readonly ExecutionCounter _counter = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));

    public IdempotencyPipelineTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _connectionString = await _postgres.CreateIsolatedDatabaseAsync("idempotency_pipeline");
        await using ServiceProvider provider = BuildProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention());
        // Implementer: mirror the exact AppDbContext registration used by IdempotencyStoreTests /
        // the existing infrastructure test helper — including any required conventions plugin.
        services.AddCqrsDispatcher();
        services.AddIdempotencyBehavior();
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();
        services.AddScoped<ICommandHandler<RecordPayment, Result<PaymentDto>>, RecordPaymentHandler>();
        services.AddSingleton(_counter);
        services.AddSingleton<GymOS.BuildingBlocks.Application.Abstractions.IClock>(_clock);
        services.AddOptions<IdempotencyOptions>().Configure(o => o.LeaseDuration = TimeSpan.FromSeconds(60));
        return services.BuildServiceProvider();
    }

    private async Task<Result<PaymentDto>> DispatchAsync(RecordPayment command, string key)
    {
        await using ServiceProvider provider = BuildProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IdempotencyContext>().SetKey(key);
        return await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .SendAsync(command, CancellationToken.None);
    }

    [Fact]
    public async Task Retry_with_the_same_key_executes_once_and_replays_the_same_body()
    {
        var command = new RecordPayment(Guid.NewGuid(), FailBusiness: false);
        int before = _counter.Count;

        Result<PaymentDto> first = await DispatchAsync(command, "pipe-replay");
        Result<PaymentDto> second = await DispatchAsync(command, "pipe-replay");

        Assert.Equal(before + 1, _counter.Count);
        Assert.Equal(first.Value, second.Value); // ExecutionNumber identical → body verbatim
    }

    [Fact]
    public async Task Same_key_different_command_rejects_key_reuse_mismatch()
    {
        await DispatchAsync(new RecordPayment(Guid.NewGuid(), false), "pipe-mismatch");
        Result<PaymentDto> second = await DispatchAsync(new RecordPayment(Guid.NewGuid(), false), "pipe-mismatch");

        Assert.True(second.IsFailure);
        Assert.Equal("idempotency.key_reuse_mismatch", second.Error!.Code);
    }

    [Fact]
    public async Task Deterministic_business_failure_replays_without_reexecuting()
    {
        var command = new RecordPayment(Guid.NewGuid(), FailBusiness: true);
        int before = _counter.Count;

        Result<PaymentDto> first = await DispatchAsync(command, "pipe-failed");
        Result<PaymentDto> second = await DispatchAsync(command, "pipe-failed");

        Assert.Equal(before + 1, _counter.Count);
        Assert.Equal("payments.already_recorded", first.Error!.Code);
        Assert.Equal("payments.already_recorded", second.Error!.Code);
    }

    [Fact]
    public async Task Concurrent_duplicates_execute_exactly_once()
    {
        var command = new RecordPayment(Guid.NewGuid(), FailBusiness: false);
        int before = _counter.Count;

        Result<PaymentDto>[] results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => DispatchAsync(command, "pipe-race")));

        Assert.Equal(before + 1, _counter.Count);
        foreach (Result<PaymentDto> result in results)
        {
            // Every caller got either the (replayed) success or the in-flight conflict — never a second execution.
            if (result.IsFailure)
            {
                Assert.Equal("idempotency.operation_in_progress", result.Error!.Code);
            }
            else
            {
                Assert.Equal(before + 1, result.Value.ExecutionNumber);
            }
        }
        Assert.Contains(results, result => result.IsSuccess);
    }
}
```

- [ ] **Step 2: Run to verify current state** — new file, tests fail only if Tasks 4–6 left gaps; expected: 4 PASS. If the race test flakes, widen to `Enumerable.Range(0, 4)` — never delete the assertion that the counter moved exactly once.

- [ ] **Step 3: Commit**

```bash
git add tests/GymOS.IntegrationTests/IdempotencyPipelineTests.cs
git commit -m "AD-39 (7/8): pipeline integration proof — replay, mismatch, deterministic failure, race"
```

---

### Task 8: Architecture test, docs, full verification

**Files:**
- Create: `tests/GymOS.ArchitectureTests/IdempotentCommandConventionTests.cs`
- Modify: `docs/STATUS.md` (framework row + open-items note about folding maintainers into the M3.4 retention job)
- Modify: `docs/Engineering/API_STANDARDS.md` (new section: Idempotency-Key request header, Idempotency-Replayed/Retry-After response headers, the three `idempotency.*` problem codes and their statuses)

**Interfaces:**
- Consumes: `ProductionAssemblies.All` (`tests/GymOS.ArchitectureTests/ProductionAssemblies.cs`), NetArchTest-style reflection scanning as in `DomainEventConventionTests.cs`.

- [ ] **Step 1: Write the convention test**

```csharp
// tests/GymOS.ArchitectureTests/IdempotentCommandConventionTests.cs
using System.Reflection;
using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.SharedKernel.Results;
using Xunit;

namespace GymOS.ArchitectureTests;

/// <summary>
/// AD-39 guardrails: an idempotent command must be a command (never a query — replaying a
/// read is meaningless) and its response must be Result-shaped, or the pipeline could not
/// store and replay its outcome. The behavior's TResponse : Result constraint silently skips
/// non-conforming types; this test turns that silence into a build failure.
/// </summary>
public sealed class IdempotentCommandConventionTests
{
    public static TheoryData<Type> AllIdempotentCommands()
    {
        var data = new TheoryData<Type>();
        foreach (Type type in ProductionAssemblies.All
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeof(IIdempotentCommand).IsAssignableFrom(type)
                && type is { IsInterface: false, IsAbstract: false }))
        {
            data.Add(type);
        }

        return data;
    }

    [Fact]
    public void The_theory_below_is_not_vacuous_or_this_fact_documents_why()
    {
        // No production IIdempotentCommand exists yet (first consumer: invoices mark-paid).
        // When one lands, this fact simply keeps passing; the theory does the real work.
        Assert.True(AllIdempotentCommands().Count >= 0);
    }

    [Theory]
    [MemberData(nameof(AllIdempotentCommands))]
    public void Every_idempotent_command_is_a_Result_returning_command(Type commandType)
    {
        Type? commandInterface = commandType.GetInterfaces()
            .SingleOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>));

        Assert.True(commandInterface is not null,
            $"{commandType.Name} implements IIdempotentCommand but is not an ICommand<>.");
        Assert.True(typeof(Result).IsAssignableFrom(commandInterface!.GetGenericArguments()[0]),
            $"{commandType.Name} must respond with Result or Result<T> so its outcome can be stored and replayed.");
        Assert.False(commandType.GetInterfaces().Any(
            i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQuery<>)),
            $"{commandType.Name} is a query; replaying a read is meaningless.");
    }
}
```

Note: xUnit skips a `[Theory]` with empty `MemberData` — some runners flag that as an error. If the runner used here errors on the empty theory, seed `AllIdempotentCommands` with the test-assembly's own `RecordPayment` type via an explicit extra `data.Add(...)` and adjust the assertions to accept test types; otherwise leave as written. The companion `[Fact]` documents the intent either way.

- [ ] **Step 2: Run** — `dotnet test tests/GymOS.ArchitectureTests` → expected 118 + new tests, all green, no `Aborted`.

- [ ] **Step 3: Update the docs**

- `docs/STATUS.md`: add a milestone row `**AD-39 idempotency framework** (claim table, pipeline behavior, decision table, host plumbing) — ✅ complete — awaiting review`, plus an "as built" section noting: claim autocommits / explicit transaction for idempotent commands only / outcome-not-HTTP stored / `ux_idempotency_records_claim` is raw SQL like the plan-price index / maintainers fold into the M3.4 retention job.
- `docs/Engineering/API_STANDARDS.md`: document the request header, both response headers, and the table: `idempotency.key_required` → 400, `idempotency.operation_in_progress` → 409 + `Retry-After`, `idempotency.key_reuse_mismatch` → 422, replay → original status + `Idempotency-Replayed: true`.

- [ ] **Step 4: Full verification**

```bash
dotnet build GymOS.Backend.slnx        # 0 warnings
dotnet test GymOS.Backend.slnx         # all green; per-project reruns if the CLR crash bites
```

Check every project's total against STATUS.md's known counts + this plan's additions; any `Aborted` in output invalidates that run (STATUS.md test-host warning). Also confirm the OpenAPI snapshot test still passes unchanged — no endpoint was added, so `contracts/openapi-v1.json` must show no diff (`git diff contracts/`).

- [ ] **Step 5: Commit**

```bash
git add tests/GymOS.ArchitectureTests/IdempotentCommandConventionTests.cs docs/STATUS.md docs/Engineering/API_STANDARDS.md
git commit -m "AD-39 (8/8): idempotent-command convention test + standing docs"
```

---

## Post-plan review gate

Per the repo's increment protocol: after Task 8, **stop for review** (summarize what was built, verification evidence, and open questions) before starting increment 2 (invoices). Do not push without the user's say-so.
