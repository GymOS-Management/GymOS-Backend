using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Infrastructure.Auditing;
using GymOS.BuildingBlocks.Infrastructure.Idempotency;
using GymOS.BuildingBlocks.Infrastructure.Outbox;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Invoices;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace GymOS.Platform.Infrastructure.UnitTests;

/// <summary>
/// The concurrency model, asserted against the EF model itself rather than against behavior
/// (ARCHITECTURE.md §3: optimistic concurrency, no last-write-wins).
///
/// <para>Behavioral tests prove that concurrency works on the endpoints that have it. These prove
/// something a behavioral test cannot: that no mutable aggregate was added *without* it. The
/// failure mode being guarded against is silent — an aggregate with no concurrency token accepts
/// conflicting writes and simply loses one, with no error anywhere.</para>
///
/// <para>The model is built without a database: EF resolves it from configuration alone, so this
/// runs in milliseconds and gates every build rather than only the ones with Docker.</para>
/// </summary>
public sealed class ConcurrencyModelTests
{
    /// <summary>
    /// Types that legitimately carry no concurrency token, each with its reason. Anything not
    /// listed must have one — a new aggregate fails this test rather than shipping unprotected.
    /// </summary>
    private static readonly Dictionary<Type, string> ExemptFromConcurrency = new()
    {
        // Append-only: nothing ever updates a row, so there is no write to lose.
        [typeof(AuditLog)] = "append-only audit record",
        [typeof(OutboxMessage)] = "append-only event capture",

        // Delivery state is claimed with SKIP LOCKED by exactly one worker (AD-13), which is
        // pessimistic by construction — an optimistic token would add nothing.
        [typeof(OutboxDelivery)] = "claimed with SKIP LOCKED, not optimistically",

        // Aggregate internals, never updated in place: role and user assignments are inserted and
        // deleted, and both are guarded by the parent aggregate's own token.
        [typeof(UserRole)] = "insert/delete only, guarded by the User aggregate",
        [typeof(RolePermission)] = "insert/delete only, guarded by the Role aggregate",

        // Prices *are* updated in place, unlike the assignment rows above — but only ever through
        // SubscriptionPlan, whose handler calls MarkAggregateChanged so the plan's own token moves on
        // every price write. A second token here would be a version a caller never sees and could not
        // send back, since the plan is the resource the API exposes and the ETag belongs to it.
        [typeof(PlanPrice)] = "updated only through the SubscriptionPlan aggregate, whose token guards it",

        // Owned by GymSubscription and stored in its row, so the subscription's own xmin already
        // moves when a scheduled change is written, replaced, or cleared. A second token here would
        // describe the same row twice.
        [typeof(PendingPlanChange)] = "owned by GymSubscription, stored in its row and guarded by its xmin",

        // Written once when the invoice is issued and never afterwards — an issued invoice is a
        // statement of what was charged, and the aggregate exposes no way to edit a line. The two
        // writes that do exist (mark-paid, void) touch the invoice row, whose xmin guards them.
        [typeof(InvoiceLine)] = "write-once at issue; the Invoice aggregate's xmin guards its only mutations",

        // Reconciled from code at startup; not editable through any API.
        [typeof(Permission)] = "code-canonical catalog, no API writes",

        // Rotation replaces rather than edits, and reuse detection already refuses a token
        // presented twice — which is a stronger guarantee than a version check.
        [typeof(RefreshToken)] = "rotated by replacement; reuse detection covers double-use",

        // Every write is a raw SQL statement (IdempotencyStore) gated by its own WHERE clause —
        // INSERT ... ON CONFLICT DO NOTHING for the claim, status/lease_expires_at conditions for
        // takeover, finish, and release (AD-39). That is a condition-based CAS, not EF-tracked
        // optimistic concurrency, so an xmin token would sit unread: nothing ever loads this
        // entity into a change tracker to compare it against.
        [typeof(IdempotencyRecord)] = "mutated by condition-gated raw SQL, never through SaveChanges",
    };

    [Fact]
    public void EveryMutableEntityHasAConcurrencyToken()
    {
        var unprotected = new List<string>();

        foreach (IEntityType entityType in Model().GetEntityTypes())
        {
            Type clrType = entityType.ClrType;

            if (ExemptFromConcurrency.ContainsKey(clrType))
            {
                continue;
            }

            bool hasToken = entityType.GetProperties().Any(property => property.IsConcurrencyToken);

            if (!hasToken)
            {
                unprotected.Add(clrType.Name);
            }
        }

        Assert.True(
            unprotected.Count == 0,
            "Entities with no concurrency token (add one, or record why it is unnecessary in " +
            $"ExemptFromConcurrency): {string.Join(", ", unprotected)}");
    }

    [Fact]
    public void TheConcurrencyTokenIsPostgresXminRatherThanAMaintainedColumn()
    {
        IEntityType user = Model().FindEntityType(typeof(User))!;

        IProperty token = Assert.Single(user.GetProperties(), property => property.IsConcurrencyToken);

        // xmin is maintained by PostgreSQL on every update. A hand-incremented column would have to
        // be bumped by every write path, and the one that forgot would silently stop protecting.
        Assert.Equal("xmin", token.GetColumnName());
        Assert.Equal(ValueGenerated.OnAddOrUpdate, token.ValueGenerated);
    }

    [Fact]
    public void EveryExemptionNamesATypeStillInTheModel()
    {
        // A stale exemption would silently excuse whatever entity is added under that name later.
        var mapped = Model().GetEntityTypes().Select(entityType => entityType.ClrType).ToHashSet();

        var stale = ExemptFromConcurrency.Keys
            .Where(type => !mapped.Contains(type))
            .Select(type => type.Name)
            .ToList();

        Assert.True(stale.Count == 0, "Exempted types no longer in the model: " + string.Join(", ", stale));
    }

    [Fact]
    public void EveryExemptionCarriesAReason()
    {
        Assert.All(ExemptFromConcurrency, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value)));
    }

    [Fact]
    public void RowVersionTokensRoundTrip()
    {
        string token = RowVersion.ToToken(4294967295);

        Assert.True(RowVersion.TryParse(token, out uint parsed));
        Assert.Equal(4294967295u, parsed);
    }

    [Theory]
    [InlineData("\"42\"", 42u)]      // the quoted form a client echoes from an ETag
    [InlineData("42", 42u)]          // and the bare value
    public void AnEtagIsAcceptedQuotedOrBare(string header, uint expected)
    {
        Assert.True(RowVersion.TryParse(header, out uint parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("-1")]
    [InlineData("4294967296")]        // one past uint.MaxValue
    [InlineData("W/\"42\"")]          // weak validators cannot be used for If-Match (RFC 9110)
    public void AMalformedPreconditionIsRejectedRatherThanIgnored(string? header)
    {
        // Rejected, not treated as "no precondition given": silently ignoring a malformed
        // If-Match would turn a client bug into a last-write-wins update.
        Assert.False(RowVersion.TryParse(header, out _));
    }

    private static IModel Model()
    {
        // No connection is opened: EF builds the model from configuration, and the connection
        // string only has to be syntactically valid.
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=model-only;Database=model-only;Username=none;Password=none")
            .UseSnakeCaseNamingConvention()
            .Options;

        using var context = new AppDbContext(options);
        return context.Model;
    }
}
