using GymOS.Platform.Domain.Features.Events;
using GymOS.Platform.Domain.Features.Rules;
using GymOS.SharedKernel.Entities;
using GymOS.SharedKernel.Rules;

namespace GymOS.Platform.Domain.Features;

/// <summary>
/// A deliberate exception to what one gym's plan gives (ARCHITECTURE.md §2.4) — the first and
/// strongest resolution layer.
///
/// <para><b>A row exists only when it differs from the plan.</b> An operator setting a feature to
/// exactly what the gym would get anyway clears the exception instead of storing it, so a gym that
/// later changes plan follows the new plan rather than staying frozen by a redundant row. The
/// handler owns that decision (<see cref="FeatureResolution.RequiresOverride"/>); this aggregate
/// owns what a stored exception must contain.</para>
///
/// <para>Audited, and unique on (gym, feature): "who gave this gym a paid feature for free, when,
/// and why" is one row, not a history to reconstruct — and the audit trail keeps the history.</para>
/// </summary>
public sealed class GymFeatureOverride : AggregateRoot, IAuditable
{
    private GymFeatureOverride(Guid id, Guid gymId, Guid featureId)
        : base(id)
    {
        GymId = gymId;
        FeatureId = featureId;
    }

    private GymFeatureOverride()
    {
    }

    public Guid GymId { get; private set; }

    public Guid FeatureId { get; private set; }

    public bool IsEnabled { get; private set; }

    /// <summary>
    /// Why the exception exists. <b>Required</b> — see <see cref="OverrideReasonIsRequired"/>.
    /// </summary>
    public string Reason { get; private set; } = string.Empty;

    /// <summary>
    /// Who set it, when known. Nullable and cleared if that account is ever removed: provenance,
    /// like a subscription's <c>PlanPriceId</c>, so losing the author never loses the override.
    /// </summary>
    public Guid? SetByUserId { get; private set; }

    public DateTimeOffset SetAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public static GymFeatureOverride Grant(
        Guid gymId,
        Guid featureId,
        string featureKey,
        bool isEnabled,
        string reason,
        Guid? setByUserId,
        DateTimeOffset now)
    {
        BusinessRules.Check(new OverrideReasonIsRequired(reason));

        var over = new GymFeatureOverride(Guid.NewGuid(), gymId, featureId)
        {
            IsEnabled = isEnabled,
            Reason = reason.Trim(),
            SetByUserId = setByUserId,
            SetAtUtc = now,
        };

        over.Raise(new GymFeatureOverridden(
            gymId, featureId, featureKey, isEnabled, over.Reason, setByUserId));

        return over;
    }

    /// <summary>
    /// Restates an existing exception — a flipped value, a better reason, or a new author taking
    /// responsibility for it. The author and timestamp move too: the current row answers "who
    /// decided this", and the audit trail keeps who decided it before.
    /// </summary>
    public void Restate(
        string featureKey,
        bool isEnabled,
        string reason,
        Guid? setByUserId,
        DateTimeOffset now)
    {
        BusinessRules.Check(new OverrideReasonIsRequired(reason));

        IsEnabled = isEnabled;
        Reason = reason.Trim();
        SetByUserId = setByUserId;
        SetAtUtc = now;

        Raise(new GymFeatureOverridden(GymId, FeatureId, featureKey, isEnabled, Reason, setByUserId));
    }

    /// <summary>
    /// Announces that this exception is being withdrawn. The row is deleted by the repository —
    /// an override's absence *is* "follows the plan", so there is no revoked state to keep.
    /// </summary>
    public void MarkCleared(string featureKey) =>
        Raise(new GymFeatureOverrideCleared(GymId, FeatureId, featureKey));
}
