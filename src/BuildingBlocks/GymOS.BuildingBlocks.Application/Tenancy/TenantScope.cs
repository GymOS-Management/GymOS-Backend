using GymOS.BuildingBlocks.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace GymOS.BuildingBlocks.Application.Tenancy;

/// <summary>
/// One gym's turn, for a job looping gyms: a dependency-injection scope whose
/// <see cref="ITenantContext"/> acts for that gym and ceases to exist when the scope is disposed.
/// </summary>
/// <remarks>
/// <para><b>It owns a scope rather than clearing a context, and that is a correction to R14's
/// design.</b> §4 of <c>2026-08-14-r14-tenant-isolation-design.md</c> describes this as a helper that
/// sets the tenant for one gym and <i>clears it on dispose</i>. That cannot be built against the
/// context the same design specifies: <c>TenantContext.SetTenant</c> refuses to be re-pointed and has
/// no clear, deliberately — a second set would mean any query that already ran did so under the
/// previous value, "a difference the caller cannot see and the database will not report". A clear
/// would have to reopen exactly that hole. So the second half of the pair is delivered by the scope
/// holding the tenant ceasing to exist, which is the same guarantee with no reset path to misuse.</para>
/// <para><b>The mistake it exists to prevent</b> is a job that reuses one scope across every gym.
/// That fails loudly here — the second iteration's <c>SetTenant</c> throws rather than silently
/// carrying gym A's tenant into gym B's work — but longhand it is easy to write, which is why
/// <c>TenantIsolation.md</c> names it among the paths the fail-closed policy has to catch.</para>
/// <para><b>Services must be resolved from <see cref="ServiceProvider"/>, not from the job's own
/// provider.</b> Nothing here can enforce that: a job holding an injected <c>DbContext</c> from its
/// outer scope keeps the outer scope's tenant, and this type never sees it. That is the residual risk,
/// and it is the reason <see cref="ServiceProvider"/> is the only way to reach anything.</para>
/// <para><b>No production caller yet.</b> Built ahead of Members, which brings the first
/// <c>ITenantScoped</c> entity and therefore the first job with tenant data to loop over.
/// <c>FeatureOverrideSweepJob</c> has the scope-per-gym shape for unrelated reasons but sets no
/// tenant — its tables are platform-scope — so routing it through this type would be wrong. The
/// end-to-end proof that RLS filters through a <see cref="TenantScope"/> needs a tenant-scoped table
/// and belongs to that increment; what is proved here is the scoping contract itself.</para>
/// </remarks>
public sealed class TenantScope : IAsyncDisposable, IDisposable
{
    private readonly AsyncServiceScope _scope;

    private TenantScope(AsyncServiceScope scope, Guid tenantId)
    {
        _scope = scope;
        TenantId = tenantId;
    }

    /// <summary>The gym this scope acts for.</summary>
    public Guid TenantId { get; }

    /// <summary>
    /// Resolve the iteration's services from here, not from the job's own provider — a service taken
    /// from the outer scope carries the outer scope's tenant, which is the mistake this type exists
    /// to make hard.
    /// </summary>
    public IServiceProvider ServiceProvider => _scope.ServiceProvider;

    /// <summary>Opens a scope acting for one gym.</summary>
    /// <param name="tenantId">The gym. <see cref="Guid.Empty"/> is refused.</param>
    /// <param name="scopeFactory">The job's scope factory.</param>
    public static TenantScope For(Guid tenantId, IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "A tenant scope must name a gym. Guid.Empty is not platform scope — platform scope " +
                "is the absence of a tenant, which is what a scope with no TenantScope around it " +
                "already is.",
                nameof(tenantId));
        }

        AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        try
        {
            scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().SetTenant(tenantId);
        }
        catch
        {
            // The caller never receives the scope, so nobody else can dispose it. Synchronously,
            // because this method cannot await — and there is nothing in a scope that failed before
            // it did any work whose disposal needs the async path.
            ((IServiceScope)scope).Dispose();
            throw;
        }

        return new TenantScope(scope, tenantId);
    }

    public void Dispose() => ((IServiceScope)_scope).Dispose();

    public ValueTask DisposeAsync() => _scope.DisposeAsync();
}
