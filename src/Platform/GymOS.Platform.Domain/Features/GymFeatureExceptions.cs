namespace GymOS.Platform.Domain.Features;

/// <summary>
/// Which of a gym's stored exceptions no longer differ from what it would get anyway.
///
/// <para>M4 enforced "an override exists only while it differs" at the instant of a write, and
/// nothing maintained it afterwards — so a gym upgraded onto a plan that grants what it had been
/// granted by exception kept a redundant row, reported <c>source: Override</c> for a capability it
/// now pays for, and on a later downgrade kept a capability it no longer buys. This is the function
/// that lets the invariant be maintained rather than merely asserted.</para>
///
/// <para><b>It does not define redundancy.</b> That is
/// <see cref="FeatureResolution.RequiresOverride"/> — the same function the write path calls. A
/// second definition would be exactly the drift this module already learned about the hard way.</para>
/// </summary>
public static class GymFeatureExceptions
{
    /// <summary>One override as stored: which feature, and what it forces.</summary>
    public sealed record StoredException(Guid FeatureId, bool IsEnabled);

    /// <param name="planOpinions">
    /// What the gym's live plan says, by feature. A feature absent from this map is one the plan is
    /// silent about — which is also every feature when the gym has no live subscription.
    /// </param>
    /// <param name="platformDefaults">
    /// The catalogue's defaults. A feature absent here has no computable baseline (an orphaned key),
    /// and its exception is left alone rather than withdrawn on a guess.
    /// </param>
    public static IReadOnlyList<Guid> Redundant(
        IEnumerable<StoredException> stored,
        IReadOnlyDictionary<Guid, bool> planOpinions,
        IReadOnlyDictionary<Guid, bool> platformDefaults)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(planOpinions);
        ArgumentNullException.ThrowIfNull(platformDefaults);

        var redundant = new List<Guid>();

        foreach (StoredException exception in stored)
        {
            if (!platformDefaults.TryGetValue(exception.FeatureId, out bool defaultEnabled))
            {
                continue;
            }

            bool? planEnabled = planOpinions.TryGetValue(exception.FeatureId, out bool opinion)
                ? opinion
                : null;

            if (!FeatureResolution.RequiresOverride(exception.IsEnabled, planEnabled, defaultEnabled))
            {
                redundant.Add(exception.FeatureId);
            }
        }

        return redundant;
    }
}
