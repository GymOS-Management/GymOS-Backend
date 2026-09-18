using System.Runtime.CompilerServices;

// NullJobRunRecorder is internal — nothing outside this assembly should construct it, callers
// depend on IJobRunRecorder — but AddJobHost's registration of it needs a direct assertion.
[assembly: InternalsVisibleTo("GymOS.BuildingBlocks.UnitTests")]

namespace GymOS.BuildingBlocks.Infrastructure;

/// <summary>
/// Identifies this assembly for assembly scanning (module loading, handler
/// registration) and for architecture tests. Intentionally contains no behavior.
/// </summary>
public static class AssemblyMarker
{
}
