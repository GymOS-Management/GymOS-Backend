using GymOS.Platform.Domain.Features.Events;
using GymOS.Platform.Domain.Features.Rules;
using GymOS.SharedKernel.Entities;
using GymOS.SharedKernel.Rules;

namespace GymOS.Platform.Domain.Features;

/// <summary>
/// One capability the platform can switch on for a gym (ARCHITECTURE.md §2.4).
///
/// <para>Platform-scope and audited: who changed what the platform ships enabled, and who marked a
/// capability as paid, are decisions a later commercial conversation turns on.</para>
///
/// <para><b>Rows are seeded, never created or deleted through the API</b> (§3.5). The key is a
/// constant that code branches on, so a runtime-invented feature would be a flag nothing reads, and
/// a deleted one would strand the code that reads it. <see cref="PlatformFeatures"/> is the
/// declaration; this aggregate owns what an operator may then change about it.</para>
/// </summary>
public sealed class Feature : AggregateRoot, IAuditable
{
    private Feature(Guid id, string key)
        : base(id) => Key = key;

    private Feature()
    {
    }

    /// <summary>
    /// The stable handle, unique and <b>immutable</b> — code branches on it, and the API returns it.
    /// <see cref="Name"/> is the human-facing label and is free to change.
    /// </summary>
    public string Key { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    /// <summary>A display grouping for the catalogue list — not behavior.</summary>
    public string Category { get; private set; } = string.Empty;

    /// <summary>
    /// The answer when neither an override nor a plan says anything (the third resolution layer).
    ///
    /// <para>Editable at runtime on purpose (§3.5): what the platform ships enabled is a commercial
    /// decision, and requiring a deployment to change it would make the flag less useful than the
    /// hard-coded boolean it replaced. The seeder therefore inserts this once and never rewrites it.
    /// </para>
    /// </summary>
    public bool DefaultEnabled { get; private set; }

    /// <summary>
    /// Whether the feature is sold rather than included.
    ///
    /// <para><b>Informational in M4</b>: what a gym actually gets is decided by its plan and its
    /// overrides. Gating overrides on this was considered and rejected — it would remove the
    /// operator's ability to grant a trial or a goodwill exception, which is the main reason
    /// overrides exist.</para>
    /// </summary>
    public bool IsPremium { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>Creates a catalogue row. Called by the seeder only — see the type's remarks.</summary>
    public static Feature FromDefinition(FeatureDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        BusinessRules.CheckAll(
            new FeatureKeyMustBeAnIdentifier(definition.Key),
            new FeatureNameIsRequired(definition.Name));

        return new Feature(Guid.NewGuid(), definition.Key.Trim().ToLowerInvariant())
        {
            Name = definition.Name.Trim(),
            Description = (definition.Description ?? string.Empty).Trim(),
            Category = (definition.Category ?? string.Empty).Trim(),
            DefaultEnabled = definition.DefaultEnabled,
            IsPremium = definition.IsPremium,
        };
    }

    /// <summary>
    /// Edits the catalogue copy and the commercial flags an operator owns (§3.5). The key is not a
    /// parameter — see <see cref="Key"/>.
    /// </summary>
    public void Describe(string name, string? description, string? category)
    {
        BusinessRules.Check(new FeatureNameIsRequired(name));

        Name = name.Trim();
        Description = (description ?? string.Empty).Trim();
        Category = (category ?? string.Empty).Trim();
    }

    /// <summary>
    /// Changes what the platform gives a gym that has no override and no plan opinion.
    ///
    /// <para>Raises an event because the blast radius is every such gym at once: flipping a default
    /// silently re-answers the question for everyone who never made a decision about it, which is
    /// exactly the change an audit trail should be able to explain.</para>
    /// </summary>
    public void SetDefault(bool enabled)
    {
        if (DefaultEnabled == enabled)
        {
            return;
        }

        DefaultEnabled = enabled;
        Raise(new FeatureDefaultChanged(Id, Key, enabled));
    }

    public void SetPremium(bool isPremium) => IsPremium = isPremium;
}
