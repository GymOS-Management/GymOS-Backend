using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace GymOS.ArchitectureTests;

/// <summary>
/// Type-level Clean Architecture rules (R6/R7, ARCHITECTURE-ADDENDUM.md §8).
/// These run against every type in every production assembly on every build —
/// a violating `using` fails CI, not code review.
/// </summary>
public sealed class LayerDependencyTests
{
    private static readonly Assembly SharedKernel = typeof(GymOS.SharedKernel.AssemblyMarker).Assembly;
    private static readonly Assembly BuildingBlocksApplication = typeof(GymOS.BuildingBlocks.Application.AssemblyMarker).Assembly;
    private static readonly Assembly BuildingBlocksInfrastructure = typeof(GymOS.BuildingBlocks.Infrastructure.AssemblyMarker).Assembly;
    private static readonly Assembly PlatformDomain = typeof(GymOS.Platform.Domain.AssemblyMarker).Assembly;
    private static readonly Assembly PlatformApplication = typeof(GymOS.Platform.Application.AssemblyMarker).Assembly;
    private static readonly Assembly PlatformInfrastructure = typeof(GymOS.Platform.Infrastructure.AssemblyMarker).Assembly;

    private static readonly Assembly[] AllProductionAssemblies =
    [
        SharedKernel,
        BuildingBlocksApplication,
        BuildingBlocksInfrastructure,
        PlatformDomain,
        PlatformApplication,
        PlatformInfrastructure,
    ];

    [Fact]
    public void SharedKernel_DependsOnNoOtherGymOsAssembly()
    {
        TestResult result = Types.InAssembly(SharedKernel)
            .Should()
            .NotHaveDependencyOnAny(
                "GymOS.BuildingBlocks",
                "GymOS.Platform",
                "GymOS.Modules",
                "GymOS.WebApi")
            .GetResult();

        Assert.True(result.IsSuccessful, Violations(result));
    }

    [Fact]
    public void DomainLayers_DoNotDependOnApplicationInfrastructureOrEfCore()
    {
        TestResult result = Types.InAssembly(PlatformDomain)
            .Should()
            .NotHaveDependencyOnAny(
                "GymOS.BuildingBlocks.Application",
                "GymOS.BuildingBlocks.Infrastructure",
                "GymOS.Platform.Application",
                "GymOS.Platform.Infrastructure",
                "GymOS.WebApi",
                "Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful, Violations(result));
    }

    [Fact]
    public void ApplicationLayers_DoNotDependOnAnyInfrastructure()
    {
        foreach (Assembly assembly in new[] { BuildingBlocksApplication, PlatformApplication })
        {
            TestResult result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOnAny(
                    "GymOS.BuildingBlocks.Infrastructure",
                    "GymOS.Platform.Infrastructure",
                    "GymOS.WebApi",
                    "Microsoft.EntityFrameworkCore")
                .GetResult();

            Assert.True(result.IsSuccessful, $"{assembly.GetName().Name}: {Violations(result)}");
        }
    }

    [Fact]
    public void NoProductionAssembly_DependsOnTheHost()
    {
        foreach (Assembly assembly in AllProductionAssemblies)
        {
            TestResult result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOnAny("GymOS.WebApi")
                .GetResult();

            Assert.True(result.IsSuccessful, $"{assembly.GetName().Name}: {Violations(result)}");
        }
    }

    private static string Violations(TestResult result) =>
        result.IsSuccessful
            ? string.Empty
            : "Violating types: " + string.Join(", ", result.FailingTypeNames ?? Enumerable.Empty<string>());
}
