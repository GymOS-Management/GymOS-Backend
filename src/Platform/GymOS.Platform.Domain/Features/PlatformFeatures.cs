namespace GymOS.Platform.Domain.Features;

/// <summary>One catalogue entry a module declares — the feature analogue of a permission (AD-22).</summary>
/// <param name="Key">
/// The compile-time constant code branches on. Stable API, never renamed.
/// </param>
/// <param name="DefaultEnabled">
/// The platform-wide answer when no plan and no override say otherwise. A <b>seed</b> value, not a
/// setting: once the row exists an operator owns it (§3.5 allows editing the default), so the seeder
/// never writes this again — see <c>FeatureCatalogSeeder</c>.
/// </param>
public sealed record FeatureDefinition(
    string Key,
    string Name,
    string Description,
    string Category,
    bool DefaultEnabled,
    bool IsPremium);

/// <summary>
/// The platform's feature catalogue (ARCHITECTURE.md §2.4) — the single source the startup seeder
/// inserts from.
///
/// <para><b>Keys are code-canonical and not runtime-creatable</b> (§3.5). A feature key is a
/// constant that code branches on, so inventing one through an API would create a flag nothing
/// reads; and deleting one would strand the code that reads it. The catalogue therefore grows here,
/// in a reviewed commit, exactly like <c>PlatformPermissions</c>.</para>
///
/// <para><b>What the platform ships enabled</b> is a commercial decision, and the values below are
/// the opening position rather than a law: the twelve keys are §2.4's, the defaults say "the
/// operational core is on for everyone; anything a customer would pay extra for, or that needs
/// hardware or an AI budget, is off until it is sold". Every one of them is editable at runtime, and
/// the seeder will not argue.</para>
/// </summary>
public static class PlatformFeatures
{
    // Categories are display groupings for the console's catalogue list, not behavior.
    private const string Operations = "Operations";
    private const string Members = "Members";
    private const string Training = "Training";
    private const string Growth = "Growth";
    private const string Integrations = "Integrations";
    private const string Insights = "Insights";

    public const string Pos = "pos";
    public const string Inventory = "inventory";
    public const string Crm = "crm";
    public const string Nutrition = "nutrition";
    public const string WorkoutBuilder = "workout_builder";
    public const string MemberApp = "member_app";
    public const string AiAssistant = "ai_assistant";
    public const string Gateway = "gateway";
    public const string AccessControl = "access_control";
    public const string Marketing = "marketing";
    public const string Analytics = "analytics";
    public const string Reports = "reports";

    public static readonly IReadOnlyList<FeatureDefinition> All =
    [
        // The operational core: a gym that cannot take a payment or see its own numbers is not
        // running, so these are on by default and free.
        new(Pos, "Point of sale", "Sell products and memberships at the desk.", Operations,
            DefaultEnabled: true, IsPremium: false),
        new(Inventory, "Inventory", "Track stock levels, suppliers, and reorder points.", Operations,
            DefaultEnabled: true, IsPremium: false),
        new(Reports, "Reports", "Operational reports and exports.", Insights,
            DefaultEnabled: true, IsPremium: false),

        // Sold capabilities: real value, real cost to build, off until a plan includes them.
        new(Crm, "CRM", "Leads, follow-ups, and the member pipeline.", Members,
            DefaultEnabled: false, IsPremium: true),
        new(Nutrition, "Nutrition", "Meal plans and nutrition tracking for members.", Training,
            DefaultEnabled: false, IsPremium: true),
        new(WorkoutBuilder, "Workout builder", "Build and assign training programmes.", Training,
            DefaultEnabled: false, IsPremium: true),
        new(MemberApp, "Member app", "The branded member application.", Members,
            DefaultEnabled: false, IsPremium: true),
        new(Marketing, "Marketing", "Campaigns, automations, and member messaging.", Growth,
            DefaultEnabled: false, IsPremium: true),
        new(Analytics, "Analytics", "Dashboards and trend analysis beyond standard reports.", Insights,
            DefaultEnabled: false, IsPremium: true),

        // Off by default because they cost per use or need hardware on site — enabling one for a
        // gym that has neither would promise something the platform cannot deliver.
        new(AiAssistant, "AI assistant", "AI assistance for staff and programme design.", Growth,
            DefaultEnabled: false, IsPremium: true),
        new(Gateway, "Gateway", "On-site gateway for local devices.", Integrations,
            DefaultEnabled: false, IsPremium: false),
        new(AccessControl, "Access control", "Doors, turnstiles, and entry permissions.", Integrations,
            DefaultEnabled: false, IsPremium: false),
    ];
}
