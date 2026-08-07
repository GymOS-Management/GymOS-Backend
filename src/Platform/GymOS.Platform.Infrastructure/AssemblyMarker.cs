using System.Runtime.CompilerServices;

// The identity adapters are internal because nothing outside this assembly should
// construct them — callers depend on the Application-layer seams. Their behavior still
// needs direct testing (PHC round-trips, claim contents), which this grant allows
// without widening the production surface.
[assembly: InternalsVisibleTo("GymOS.Platform.Infrastructure.UnitTests")]

// IdempotencyStore is internal for the same reason (AD-39 callers depend on
// IIdempotencyStore, not the EF-backed adapter), but its race/rollback/lease behavior
// needs a real PostgreSQL to prove, which only the integration suite provides — hence
// this grant rather than widening the production surface.
[assembly: InternalsVisibleTo("GymOS.IntegrationTests")]

namespace GymOS.Platform.Infrastructure;

/// <summary>
/// Identifies this assembly for assembly scanning (module loading, handler
/// registration) and for architecture tests. Intentionally contains no behavior.
/// </summary>
public static class AssemblyMarker
{
}
