namespace GymOS.Platform.Domain.Features;

/// <summary>Which layer decided a feature's answer — carried with every resolution.</summary>
public enum FeatureSource
{
    /// <summary>The platform-wide default: nothing more specific had an opinion.</summary>
    Default = 1,

    /// <summary>The gym's plan includes (or excludes) the feature.</summary>
    Plan = 2,

    /// <summary>A deliberate per-gym exception, with a reason and an author.</summary>
    Override = 3,
}

/// <summary>One feature's answer for one gym, and where it came from.</summary>
public sealed record ResolvedFeature(string Key, bool Enabled, FeatureSource Source);

/// <summary>
/// The three-layer resolution rule (ARCHITECTURE.md §2.4): <b>gym override → plan feature →
/// platform default</b>.
///
/// <para>A pure, total function of three nullable inputs, deliberately: the rule is the most-read
/// sentence in the module — every gym detail view, every future enforcement check — and keeping it
/// as arithmetic over values means it is exhaustively testable without a database, and cannot drift
/// between the endpoints that ask it.</para>
///
/// <para><b>Every answer carries its source</b> because "is POS on?" and "why is POS on?" are the
/// same question in a support conversation: an operator who sees only the boolean cannot tell a
/// deliberate exception from a plan they misremember.</para>
/// </summary>
public static class FeatureResolution
{
    /// <summary>
    /// Resolves one feature.
    /// </summary>
    /// <param name="overrideEnabled">
    /// The gym's override, or <see langword="null"/> when it has none. An override always wins:
    /// it is a reasoned, authored exception to what the gym's plan gives.
    /// </param>
    /// <param name="planEnabled">
    /// The plan's answer, or <see langword="null"/> when the plan says nothing about this feature —
    /// which is also the case for <b>a gym with no live subscription at all</b>. Falling through to
    /// the platform default there is deliberate: a gym is provisioned before it is sold anything
    /// (M2 supports that order), so a provisioning gym stays configurable and a cancelled gym
    /// degrades to the baseline instead of going dark.
    /// </param>
    /// <param name="defaultEnabled">The catalogue's platform-wide default — always present.</param>
    public static ResolvedFeature Resolve(
        string key,
        bool? overrideEnabled,
        bool? planEnabled,
        bool defaultEnabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (overrideEnabled is { } fromOverride)
        {
            return new ResolvedFeature(key, fromOverride, FeatureSource.Override);
        }

        return planEnabled is { } fromPlan
            ? new ResolvedFeature(key, fromPlan, FeatureSource.Plan)
            : new ResolvedFeature(key, defaultEnabled, FeatureSource.Default);
    }

    /// <summary>
    /// Whether a desired value needs an override at all, given what the layers beneath would give.
    ///
    /// <para>§2.4: "an override exists only when it *differs* from the plan". So an operator setting
    /// a feature to exactly what the gym would get anyway stores nothing, and any existing override
    /// is cleared — which keeps the invariant true by construction and, more usefully, means a gym
    /// that later changes plan follows the new plan instead of being frozen by a redundant
    /// override.</para>
    /// </summary>
    public static bool RequiresOverride(bool desiredEnabled, bool? planEnabled, bool defaultEnabled) =>
        desiredEnabled != (planEnabled ?? defaultEnabled);
}
