using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Tenancy;
using GymOS.BuildingBlocks.Infrastructure.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GymOS.BuildingBlocks.UnitTests;

public sealed class TenantScopeTests
{
    private static readonly Guid Gym = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>
    /// Mirrors the production registration (PlatformInfrastructureServiceCollectionExtensions): one
    /// TenantContext per scope, exposed as both halves of the seam. A test that registered the two
    /// interfaces as separate instances would pass while proving nothing.
    /// </summary>
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(p => p.GetRequiredService<TenantContext>());
        services.AddScoped<ITenantContextSetter>(p => p.GetRequiredService<TenantContext>());
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task TheScopeActsForTheGymItNames()
    {
        await using ServiceProvider provider = BuildProvider();

        await using TenantScope scope = TenantScope.For(
            Gym, provider.GetRequiredService<IServiceScopeFactory>());

        var context = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        Assert.Equal(Gym, context.TenantId);
        Assert.False(context.IsPlatformScope);
    }

    [Fact]
    public async Task DisposingTheScopeDisposesTheServicesTakenFromIt()
    {
        // "Clears on dispose" is delivered by the scope ceasing to exist, so the disposal has to be
        // real: a job looping thousands of gyms that leaked a DbContext per iteration would exhaust
        // the connection pool long before the sweep finished.
        var services = new ServiceCollection();
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContextSetter>(p => p.GetRequiredService<TenantContext>());
        services.AddScoped<DisposalProbe>();
        await using ServiceProvider provider = services.BuildServiceProvider();

        DisposalProbe probe;
        await using (TenantScope scope = TenantScope.For(
            Gym, provider.GetRequiredService<IServiceScopeFactory>()))
        {
            probe = scope.ServiceProvider.GetRequiredService<DisposalProbe>();
            Assert.False(probe.Disposed);
        }

        Assert.True(probe.Disposed);
    }

    [Fact]
    public async Task TheEmptyGuidIsRefused()
    {
        // Not because it collides with platform scope — the interceptor writes an empty *string* for
        // that, so Guid.Empty would reach the database as a well-formed uniqueidentifier matching no gym and the
        // job would quietly process nothing. A caller passing default(Guid) has a bug, and this is
        // the last place able to say so.
        await using ServiceProvider provider = BuildProvider();
        var factory = provider.GetRequiredService<IServiceScopeFactory>();

        Assert.Throws<ArgumentException>(() => TenantScope.For(Guid.Empty, factory));
    }

    [Fact]
    public async Task AFailureToNameTheTenantDoesNotLeakTheScope()
    {
        // SetTenant is not a formality that cannot fail — TenantContext throws on a re-point. A job
        // looping thousands of gyms that leaked a scope on each failure would take the process down
        // by a route pointing nowhere near the gym that failed.
        var services = new ServiceCollection();
        services.AddSingleton<DisposalRecorder>();
        services.AddScoped<RecordingProbe>();
        services.AddScoped<ITenantContextSetter, ThrowingSetter>();
        await using ServiceProvider provider = services.BuildServiceProvider();

        var recorder = provider.GetRequiredService<DisposalRecorder>();
        var factory = provider.GetRequiredService<IServiceScopeFactory>();

        Assert.Throws<InvalidOperationException>(() => TenantScope.For(Gym, factory));

        Assert.Equal(1, recorder.Disposals);
    }

    [Fact]
    public async Task SuccessiveGymsEachGetTheirOwnContext()
    {
        // The whole reason this type owns a DI scope rather than mutating an ambient context:
        // TenantContext refuses to be re-pointed, so a loop that reused one scope would throw on its
        // second gym. Fails if TenantScope stops creating a scope per call.
        await using ServiceProvider provider = BuildProvider();
        var factory = provider.GetRequiredService<IServiceScopeFactory>();
        Guid otherGym = Guid.Parse("22222222-2222-2222-2222-222222222222");

        await using (TenantScope first = TenantScope.For(Gym, factory))
        {
            Assert.Equal(Gym, first.ServiceProvider.GetRequiredService<ITenantContext>().TenantId);
        }

        await using (TenantScope second = TenantScope.For(otherGym, factory))
        {
            Assert.Equal(
                otherGym, second.ServiceProvider.GetRequiredService<ITenantContext>().TenantId);
        }
    }

    [Fact]
    public async Task TheSurroundingScopeKeepsItsOwnTenant()
    {
        // A job's own scope stays platform scope while an iteration acts for one gym. Fails if
        // anyone reintroduces the set-and-clear-an-ambient-context shape, where the inner scope's
        // tenant would be visible to — and left behind on — the outer one.
        await using ServiceProvider provider = BuildProvider();
        var outer = provider.GetRequiredService<ITenantContext>();

        await using (TenantScope scope = TenantScope.For(
            Gym, provider.GetRequiredService<IServiceScopeFactory>()))
        {
            Assert.NotSame(outer, scope.ServiceProvider.GetRequiredService<ITenantContext>());
            Assert.True(outer.IsPlatformScope);
        }

        Assert.True(outer.IsPlatformScope);
        Assert.Null(outer.TenantId);
    }

    private sealed class DisposalProbe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class DisposalRecorder
    {
        public int Disposals { get; set; }
    }

    private sealed class RecordingProbe : IDisposable
    {
        private readonly DisposalRecorder _recorder;

        public RecordingProbe(DisposalRecorder recorder) => _recorder = recorder;

        public void Dispose() => _recorder.Disposals++;
    }

    /// <summary>Takes the probe so that resolving the setter puts a disposable into the scope.</summary>
    private sealed class ThrowingSetter : ITenantContextSetter
    {
        public ThrowingSetter(RecordingProbe probe) => _ = probe;

        public void SetTenant(Guid tenantId) =>
            throw new InvalidOperationException("This scope already acts for another tenant.");
    }
}
