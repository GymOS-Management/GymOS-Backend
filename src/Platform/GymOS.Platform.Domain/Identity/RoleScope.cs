namespace GymOS.Platform.Domain.Identity;

/// <summary>Two scopes, one mechanism (AD-22).</summary>
public enum RoleScope
{
    /// <summary>Platform administrators (SuperAdmin, BillingAdmin, …); tenant_id is null.</summary>
    Platform = 1,

    /// <summary>Gym-side roles (GymAdmin, Reception, Trainer, future custom roles).</summary>
    Tenant = 2,
}
