using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.BuildingBlocks.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GymOS.BuildingBlocks.UnitTests.Jobs;

/// <summary>
/// <see cref="JobsServiceCollectionExtensions.AddJobHost"/>'s own registrations, separate from
/// <see cref="JobHost"/>'s runtime behavior (<see cref="JobHostTests"/>).
/// </summary>
public sealed class JobsServiceCollectionExtensionsTests
{
    [Fact]
    public void AddJobHostAlone_RegistersANullJobRunRecorder()
    {
        // A host that composes the job runner without AddPlatformMonitoring (BuildingBlocks
        // never depends on Platform) must still be able to resolve IJobRunRecorder — monitoring
        // must never be why a job fails to run.
        var services = new ServiceCollection();
        services.AddJobHost();

        using ServiceProvider provider = services.BuildServiceProvider();

        var recorder = provider.GetRequiredService<IJobRunRecorder>();

        Assert.IsType<NullJobRunRecorder>(recorder);
    }
}
