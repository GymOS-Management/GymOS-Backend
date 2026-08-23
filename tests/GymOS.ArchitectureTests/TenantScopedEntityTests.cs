using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace GymOS.ArchitectureTests;

/// <summary>
/// Rule R14 (tenant isolation), the architecture half of it: every type marked
/// <see cref="ITenantScoped"/> must be something <c>TenantIsolationValidator</c> can actually go and
/// check at startup — a mapped EF entity with a table name and an explicit schema.
///
/// <para>This is the test D7 promised. The marker was chosen over an EF annotation precisely so a test
/// could see it, and the R14 increment's definition of done listed "an architecture test asserting
/// every <c>ITenantScoped</c> entity's table is validated" — which was not built at the time and is
/// built here.</para>
///
/// <para><b>Honest about its own coverage: nothing implements <see cref="ITenantScoped"/> yet.</b> No
/// production table is tenant-scoped as of the R14 increment; <c>members</c> is the first, in the
/// increment after it. So every assertion below currently iterates an empty set and passes having
/// inspected nothing. This is a <b>tripwire armed for the Members increment</b>, not a statement that
/// anything is covered today — do not read a green run here as evidence that tenant isolation is
/// enforced over any table.</para>
///
/// <para><b>Why the tripwire is worth arming early.</b> <c>TenantIsolationValidator</c> returns early
/// when no entity implements the marker, and that early return switches off the per-table checks
/// <em>and</em> the connected-role check together. A <c>Member</c> that forgets <c>: ITenantScoped</c>
/// therefore boots green with no isolation at all. This test cannot catch that omission — nothing can
/// see a marker that was never written — but it does catch the near miss it will be confused with: a
/// marker applied to a class EF never maps, or mapped without an explicit schema, where the validator
/// would go looking for <c>public.&lt;table&gt;</c>, find nothing, and the marker would do nothing while
/// looking like it did something.</para>
/// </summary>
public sealed class TenantScopedEntityTests
{
    /// <summary>
    /// The production model, built without touching a database. <c>UseSqlServer</c> with a connection
    /// string nothing dials is enough: EF needs a relational provider to resolve table names and
    /// schemas, and building the model opens no connection. The naming convention is applied exactly as
    /// <c>Program.cs</c> applies it, so column names here are the ones the validator will query for.
    /// </summary>
    private static IModel ProductionModel()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=architecture-tests-never-connect;Database=none")
            .UseSnakeCaseNamingConvention()
            .Options;

        using var context = new AppDbContext(options);
        return context.Model;
    }

    private static List<Type> TenantScopedTypes() =>
        ProductionAssemblies.All
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeof(ITenantScoped).IsAssignableFrom(type)
                && type is { IsInterface: false, IsAbstract: false })
            .ToList();

    [Fact]
    public void EveryTenantScopedTypeIsAMappedEntityWithATableAndAnExplicitSchema()
    {
        IModel model = ProductionModel();
        var violations = new List<string>();

        foreach (Type type in TenantScopedTypes())
        {
            IEntityType? entity = model.FindEntityType(type);

            if (entity is null)
            {
                // The failure this exists for: a marker on a class EF never maps. The validator walks
                // the EF model, so such a type is invisible to it — the marker reads as protection in
                // review and buys nothing at runtime.
                violations.Add(
                    $"{type.FullName} implements ITenantScoped but is not a mapped entity type, so " +
                    "TenantIsolationValidator will never see it.");
                continue;
            }

            if (entity.GetTableName() is null)
            {
                violations.Add(
                    $"{type.FullName} is mapped but resolves to no table (owned without a table of its " +
                    "own, or a TPH/TPC leaf), so there is no table for the validator to check.");
                continue;
            }

            if (entity.GetSchema() is null)
            {
                // AppDbContext declares no default schema, so an unstated schema sends the validator
                // looking for `public.<table>` — which does not exist, and it would report "the table
                // does not exist" for a table that is right there under another schema.
                violations.Add(
                    $"{type.FullName} maps to \"{entity.GetTableName()}\" with no explicit schema. " +
                    "TenantIsolationValidator reads GetSchema() ?? \"public\", so it would probe the " +
                    "wrong table. State the schema in ToTable.");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Types marked ITenantScoped that TenantIsolationValidator cannot validate (rule R14):" +
            Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", violations));
    }

    [Fact]
    public void EveryTenantScopedTypeMapsItsTenantIdToATenantIdColumn()
    {
        // The validator asserts a `tenant_id` column exists on the table, and the RLS policy compares
        // that column. An entity that maps TenantId elsewhere — a HasColumnName someone added for
        // consistency with a legacy name — would fail the boot with a message about a missing column,
        // which is the safe direction but a confusing place to start debugging. Catch it at build time.
        IModel model = ProductionModel();
        var violations = new List<string>();

        foreach (Type type in TenantScopedTypes())
        {
            if (model.FindEntityType(type) is not { } entity)
            {
                continue; // Reported by the test above; one finding per defect.
            }

            IProperty? tenantId = entity.FindProperty(nameof(ITenantScoped.TenantId));

            if (tenantId is null)
            {
                violations.Add($"{type.FullName} does not map TenantId as a property.");
                continue;
            }

            string? columnName = tenantId.GetColumnName(
                StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema()));

            if (!string.Equals(columnName, "tenant_id", StringComparison.Ordinal))
            {
                violations.Add(
                    $"{type.FullName}.TenantId maps to column \"{columnName}\", not \"tenant_id\".");
            }
        }

        Assert.True(
            violations.Count == 0,
            "ITenantScoped entities whose TenantId is not the tenant_id column the RLS policy " +
            "compares (rule R14):" + Environment.NewLine + "  " +
            string.Join(Environment.NewLine + "  ", violations));
    }
}
