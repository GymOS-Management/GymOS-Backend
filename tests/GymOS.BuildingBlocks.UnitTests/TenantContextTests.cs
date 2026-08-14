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
