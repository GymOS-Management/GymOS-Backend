using System.Reflection;

namespace GymOS.ArchitectureTests;

/// <summary>
/// Every assembly the architecture rules apply to, in one place.
///
/// <para>Listed explicitly rather than discovered by scanning the output directory: a rule that silently
/// stops covering an assembly is a rule that has quietly been switched off, and a missing entry here is
/// visible in review in a way a failed glob is not. A new production project is added by hand — which is
/// the point.</para>
/// </summary>
internal static class ProductionAssemblies
{
    public static Assembly SharedKernel => typeof(GymOS.SharedKernel.AssemblyMarker).Assembly;

    public static Assembly BuildingBlocksApplication => typeof(GymOS.BuildingBlocks.Application.AssemblyMarker).Assembly;

    public static Assembly BuildingBlocksInfrastructure => typeof(GymOS.BuildingBlocks.Infrastructure.AssemblyMarker).Assembly;

    public static Assembly PlatformDomain => typeof(GymOS.Platform.Domain.AssemblyMarker).Assembly;

    public static Assembly PlatformApplication => typeof(GymOS.Platform.Application.AssemblyMarker).Assembly;

    public static Assembly PlatformInfrastructure => typeof(GymOS.Platform.Infrastructure.AssemblyMarker).Assembly;

    public static Assembly[] All =>
    [
        SharedKernel,
        BuildingBlocksApplication,
        BuildingBlocksInfrastructure,
        PlatformDomain,
        PlatformApplication,
        PlatformInfrastructure,
    ];
}
