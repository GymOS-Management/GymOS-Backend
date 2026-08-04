using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Infrastructure.Context;
using GymOS.BuildingBlocks.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GymOS.BuildingBlocks.UnitTests;

public sealed class CorrelationContextTests
{
    [Fact]
    public void Set_ExposesTheCorrelationId()
    {
        var context = new CorrelationContext();
        var id = Guid.NewGuid();

        context.Set(id);

        Assert.Equal(id, context.CorrelationId);
    }

    [Fact]
    public void Set_Twice_Throws_ExactlyOneEdgeOwnsTheScope()
    {
        var context = new CorrelationContext();
        context.Set(Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() => context.Set(Guid.NewGuid()));
    }

    [Fact]
    public void Unset_ReadsAsNull()
    {
        var context = new CorrelationContext();

        Assert.Null(context.CorrelationId);
    }

    [Fact]
    public void WriterAndReader_ResolveToTheSameScopedInstance()
    {
        var services = new ServiceCollection();
        services.AddBuildingBlocksInfrastructure();
        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        var writer = scope.ServiceProvider.GetRequiredService<CorrelationContext>();
        var reader = scope.ServiceProvider.GetRequiredService<ICorrelationContext>();
        var id = Guid.NewGuid();

        writer.Set(id);

        Assert.Equal(id, reader.CorrelationId);
    }

    [Fact]
    public void DifferentScopes_HaveIndependentContexts()
    {
        var services = new ServiceCollection();
        services.AddBuildingBlocksInfrastructure();
        using ServiceProvider provider = services.BuildServiceProvider();

        using IServiceScope first = provider.CreateScope();
        using IServiceScope second = provider.CreateScope();

        first.ServiceProvider.GetRequiredService<CorrelationContext>().Set(Guid.NewGuid());

        Assert.Null(second.ServiceProvider.GetRequiredService<ICorrelationContext>().CorrelationId);
    }
}
