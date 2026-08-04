namespace GymOS.SharedKernel.Rules;

/// <summary>
/// One named, testable business rule, evaluated inside an aggregate.
///
/// <para><b>What this is for.</b> Domain methods accumulate <c>if (…) throw</c> statements, and
/// after a few of them the rule and its reason are buried in control flow: you cannot list the
/// rules a type enforces, test one in isolation, or reuse it in the second place it applies. A rule
/// as an object has a name, a stable code, and its own tests.</para>
///
/// <para><b>What this is not for.</b> Rules that need to look at other aggregates or ask the
/// database. A rule here is a pure predicate over data the caller already holds — it takes no
/// dependencies and does no I/O, so it stays synchronous and deterministic. Cross-aggregate rules
/// ("the last SuperAdmin cannot be demoted") belong in the Application layer, where they can query
/// and return a <c>Result</c>; see <c>SuperAdminGuard</c> for the shape.</para>
///
/// <para>Implementations should be immutable records or small sealed classes, taking the values
/// they judge as constructor parameters.</para>
/// </summary>
public interface IBusinessRule
{
    /// <summary>
    /// Stable, machine-readable, dotted: <c>members.age_below_minimum</c>. Surfaced to clients and
    /// therefore API — reword <see cref="Message"/> freely, never this.
    /// </summary>
    string Code { get; }

    /// <summary>
    /// What went wrong, for a human. Should say what the rule requires rather than restating that
    /// something was invalid, so the reader knows what to change.
    /// </summary>
    string Message { get; }

    /// <summary>
    /// <see langword="true"/> when the rule is violated.
    ///
    /// Phrased as "is broken" rather than "is satisfied" deliberately: the interesting case is the
    /// failure, and <c>if (rule.IsBroken())</c> reads the way the check is meant.
    /// </summary>
    bool IsBroken();
}
