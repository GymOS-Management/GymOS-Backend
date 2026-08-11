using GymOS.Platform.Domain.Subscriptions.Events;
using GymOS.Platform.Domain.Subscriptions.Rules;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Currencies;
using GymOS.SharedKernel.Entities;
using GymOS.SharedKernel.Rules;

namespace GymOS.Platform.Domain.Subscriptions;

/// <summary>
/// A commercial plan gyms subscribe to (ARCHITECTURE.md §2.3): its limits, its trial, and its prices.
///
/// <para>Platform-scope, like <c>Gym</c> — a plan belongs to the platform, not to any tenant.</para>
///
/// <para><b>Plans are never deleted.</b> §3.4 says "delete = deactivate only, never destroys history",
/// and the reason is that subscriptions and invoices point here: destroying a plan would orphan the
/// record of what a gym actually agreed to pay. <see cref="Deactivate"/> stops new assignments and
/// leaves existing ones running.</para>
/// </summary>
public sealed class SubscriptionPlan : AggregateRoot, IAuditable
{
    private readonly List<PlanPrice> _prices = [];
    private readonly List<PlanFeature> _features = [];

    private SubscriptionPlan(Guid id, string code, string name, string description)
        : base(id)
    {
        Code = code;
        Name = name;
        Description = description;
        IsActive = true;
        IsPublic = true;
    }

    private SubscriptionPlan()
    {
    }

    /// <summary>
    /// Stable upper-case handle, unique across plans and **immutable after creation**.
    ///
    /// Immutable for the same reason a gym's slug is: it leaves the system. It appears in exports, in
    /// accounting integrations, and in the sentence a support agent says out loud. Renaming it would
    /// silently break whatever downstream mapping keyed on it, and the human-readable
    /// <see cref="Name"/> is already free to change.
    /// </summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    /// <summary>Days of trial a new subscription gets. Zero means no trial.</summary>
    public int TrialDays { get; private set; }

    /// <summary>
    /// Ceiling on members, or <see langword="null"/> for unlimited.
    ///
    /// Null rather than a sentinel: zero would mean "no members allowed", and <c>int.MaxValue</c> makes
    /// every limit check read as arithmetic instead of as a question about whether a limit exists.
    /// </summary>
    public int? MaxMembers { get; private set; }

    public int? MaxTrainers { get; private set; }

    public int? MaxStaff { get; private set; }

    public long? StorageLimitMb { get; private set; }

    /// <summary>
    /// Whether the plan can be newly assigned. Inactive plans stay attached to the gyms already on them
    /// (§2.3) — deactivating is a decision about the sales catalogue, not about existing customers.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// Whether the plan appears in a public price list. Distinct from <see cref="IsActive"/> on purpose:
    /// a negotiated plan is assignable but unlisted, which is a different state from withdrawn.
    /// </summary>
    public bool IsPublic { get; private set; }

    /// <summary>Display order in the catalogue; ties broken by name at the read side.</summary>
    public int SortOrder { get; private set; }

    public IReadOnlyCollection<PlanPrice> Prices => _prices.AsReadOnly();

    /// <summary>What the plan includes (§2.4). Absent features are silence, not exclusion.</summary>
    public IReadOnlyCollection<PlanFeature> Features => _features.AsReadOnly();

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public static SubscriptionPlan Create(
        string code,
        string name,
        string description,
        int trialDays,
        PlanLimits limits,
        int sortOrder = 0)
    {
        ArgumentNullException.ThrowIfNull(limits);

        string normalizedCode = (code ?? string.Empty).Trim().ToUpperInvariant();

        BusinessRules.CheckAll(
            new PlanCodeMustBeAnIdentifier(normalizedCode),
            new PlanNameIsRequired(name),
            new TrialDaysMustBeSensible(trialDays),
            new PlanLimitMustBePositiveWhenSet("Member limit", limits.MaxMembers),
            new PlanLimitMustBePositiveWhenSet("Trainer limit", limits.MaxTrainers),
            new PlanLimitMustBePositiveWhenSet("Staff limit", limits.MaxStaff),
            new PlanLimitMustBePositiveWhenSet("Storage limit", limits.StorageLimitMb));

        var plan = new SubscriptionPlan(
            Guid.NewGuid(),
            normalizedCode,
            name.Trim(),
            (description ?? string.Empty).Trim())
        {
            TrialDays = trialDays,
            MaxMembers = limits.MaxMembers,
            MaxTrainers = limits.MaxTrainers,
            MaxStaff = limits.MaxStaff,
            StorageLimitMb = limits.StorageLimitMb,
            SortOrder = sortOrder,
        };

        plan.Raise(new PlanCreated(plan.Id, plan.Code, plan.Name));

        return plan;
    }

    /// <summary>
    /// Changes the plan's details. <see cref="Code"/> is absent by design — see its documentation.
    /// </summary>
    public void UpdateDetails(
        string name,
        string description,
        int trialDays,
        PlanLimits limits,
        int sortOrder,
        bool isPublic)
    {
        ArgumentNullException.ThrowIfNull(limits);

        BusinessRules.CheckAll(
            new PlanNameIsRequired(name),
            new TrialDaysMustBeSensible(trialDays),
            new PlanLimitMustBePositiveWhenSet("Member limit", limits.MaxMembers),
            new PlanLimitMustBePositiveWhenSet("Trainer limit", limits.MaxTrainers),
            new PlanLimitMustBePositiveWhenSet("Staff limit", limits.MaxStaff),
            new PlanLimitMustBePositiveWhenSet("Storage limit", limits.StorageLimitMb));

        Name = name.Trim();
        Description = (description ?? string.Empty).Trim();
        TrialDays = trialDays;
        MaxMembers = limits.MaxMembers;
        MaxTrainers = limits.MaxTrainers;
        MaxStaff = limits.MaxStaff;
        StorageLimitMb = limits.StorageLimitMb;
        SortOrder = sortOrder;
        IsPublic = isPublic;

        Raise(new PlanUpdated(Id));
    }

    /// <summary>
    /// Sets the price for one cycle and currency, adding it or changing the existing one.
    ///
    /// <para>Upsert rather than separate add/change operations: "the annual euro price is €480" is one
    /// intent, and making the caller discover whether a row already exists invites the race where two
    /// operators both add one. The (cycle, currency) uniqueness is enforced here and by a unique
    /// index.</para>
    ///
    /// <para>Changing a price never reprices existing subscriptions — those hold a price snapshot
    /// (§2.3), so this affects new assignments and renewals only.</para>
    /// </summary>
    public PlanPrice SetPrice(BillingCycle billingCycle, Money price)
    {
        BusinessRules.Check(new PlanPriceMustBePositive(price));

        PlanPrice? existing = _prices.SingleOrDefault(row => row.Matches(billingCycle, price.Currency));

        if (existing is null)
        {
            existing = new PlanPrice(Guid.NewGuid(), Id, billingCycle, price);
            _prices.Add(existing);
        }
        else
        {
            existing.ChangeTo(price);
        }

        Raise(new PlanPriceSet(
            Id,
            existing.Id,
            billingCycle.ToString(),
            price.Currency.Code,
            price.Amount));

        return existing;
    }

    /// <summary>
    /// Removes a price. Safe because subscriptions snapshot the price they were assigned — removing the
    /// row withdraws an offer, it does not rewrite a contract.
    /// </summary>
    public void RemovePrice(Guid planPriceId)
    {
        PlanPrice? price = _prices.SingleOrDefault(row => row.Id == planPriceId);

        if (price is null)
        {
            throw new DomainException("That price does not belong to this plan.");
        }

        _prices.Remove(price);
        Raise(new PlanPriceRemoved(Id, planPriceId));
    }

    /// <summary>
    /// States what this plan says about a feature — the second resolution layer (§2.4).
    ///
    /// <para>An upsert, like <see cref="SetPrice"/>: "the PRO plan includes CRM" is one intent, and
    /// making the caller check first invites the race where two operators both add a row.</para>
    /// </summary>
    public PlanFeature SetFeature(Guid featureId, bool isEnabled)
    {
        PlanFeature? existing = _features.SingleOrDefault(row => row.FeatureId == featureId);

        if (existing is null)
        {
            existing = new PlanFeature(Guid.NewGuid(), Id, featureId, isEnabled);
            _features.Add(existing);
        }
        else
        {
            existing.ChangeTo(isEnabled);
        }

        Raise(new PlanFeatureSet(Id, featureId, isEnabled));

        return existing;
    }

    /// <summary>
    /// Removes the plan's opinion about a feature, so gyms on it fall through to the platform
    /// default — which is <b>not</b> the same as including it as disabled. See
    /// <see cref="PlanFeature"/> on why silence and "no" are different answers.
    /// </summary>
    public void ClearFeature(Guid featureId)
    {
        PlanFeature? feature = _features.SingleOrDefault(row => row.FeatureId == featureId);

        if (feature is null)
        {
            return; // Already silent: the caller's intent is satisfied.
        }

        _features.Remove(feature);
        Raise(new PlanFeatureCleared(Id, featureId));
    }

    /// <summary>Withdraws the plan from the catalogue. Existing subscriptions continue.</summary>
    public void Deactivate()
    {
        if (!IsActive)
        {
            throw new DomainException("The plan is already inactive.");
        }

        IsActive = false;
        Raise(new PlanDeactivated(Id));
    }

    public void Activate()
    {
        if (IsActive)
        {
            throw new DomainException("The plan is already active.");
        }

        IsActive = true;
        Raise(new PlanActivated(Id));
    }
}

/// <summary>
/// A plan's capacity ceilings, grouped so four nullable numbers cannot be passed in the wrong order —
/// the same reasoning as <c>GymLocale</c>. <see langword="null"/> means unlimited throughout.
/// </summary>
public sealed record PlanLimits(int? MaxMembers, int? MaxTrainers, int? MaxStaff, long? StorageLimitMb)
{
    /// <summary>No ceilings at all — the shape an enterprise plan usually takes.</summary>
    public static PlanLimits Unlimited => new(null, null, null, null);
}
