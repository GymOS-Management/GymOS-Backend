using GymOS.BuildingBlocks.Infrastructure.Auditing;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Invoices;
using GymOS.Platform.Domain.Subscriptions;

namespace GymOS.Platform.Infrastructure.Persistence.Auditing;

/// <summary>
/// What the platform module audits (AD-7). Declared, not inferred — adding a table to the
/// trail is a decision, and this list is the one place to review what the trail covers.
/// (Moves onto the <c>IModule</c> manifest when the module loader lands, like permissions.)
/// </summary>
public static class PlatformAuditedEntities
{
    public static IReadOnlyList<AuditedEntity> All =>
    [
        // password_hash and security_stamp are masked by the name heuristic; listing them
        // anyway makes the intent explicit at the point someone reviews this file.
        AuditedEntity.Of<User>("user", alwaysRedacted: [nameof(User.PasswordHash), nameof(User.SecurityStamp)]),

        AuditedEntity.Of<Role>("role"),
        AuditedEntity.Of<UserRole>("user_role"),
        AuditedEntity.Of<RolePermission>("role_permission"),

        // A gym is a tenant: who provisioned it, who suspended it and why, and who deleted it are the
        // questions a support conversation or a billing dispute turns on.
        AuditedEntity.Of<Gym>("gym"),
        AuditedEntity.Of<GymBranding>("gym_branding"),

        // What the platform charges, and when it changed. A price edit is the entry a billing dispute
        // turns on, and "who withdrew this plan" is the other half of that conversation.
        AuditedEntity.Of<SubscriptionPlan>("subscription_plan"),
        AuditedEntity.Of<PlanPrice>("plan_price"),

        // What each gym agreed to pay, and every change to it. This is the row a billing dispute is
        // argued from, so who assigned, repriced, or cancelled it has to be recoverable.
        AuditedEntity.Of<GymSubscription>("gym_subscription"),

        // What was actually charged, who marked it paid, and who voided it. payment_reference is
        // deliberately NOT redacted: it is a bank/processor transaction identifier an operator typed,
        // not a credential — and "which payment was this recorded against" is the audit question.
        AuditedEntity.Of<Invoice>("invoice"),
        AuditedEntity.Of<InvoiceLine>("invoice_line"),

        // Deliberately absent:
        //   RefreshToken — every sign-in and rotation writes one, so auditing rows here
        //     would bury the administrative actions the log exists for. Sessions are
        //     recorded as explicit `auth.*` entries instead.
        //   Permission — the catalog is code-canonical and reconciled at startup; a diff
        //     against the deployed code, not an operator's action.
        //   AuditLog — auditing the audit log is a recursion with no reader.
    ];
}
