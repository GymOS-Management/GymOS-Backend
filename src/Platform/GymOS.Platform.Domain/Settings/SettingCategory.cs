namespace GymOS.Platform.Domain.Settings;

/// <summary>The category a setting belongs to, for console grouping and the surface contract.</summary>
public enum SettingCategory
{
    /// <summary>Platform-wide settings.</summary>
    General = 1,
    /// <summary>Branding and appearance settings (deferred).</summary>
    Branding = 2,
    /// <summary>Email delivery configuration.</summary>
    Email = 3,
    /// <summary>Storage and file handling settings (deferred).</summary>
    Storage = 4,
    /// <summary>AI and machine learning settings (deferred).</summary>
    AI = 5,
    /// <summary>Payment and billing settings (deferred).</summary>
    Payment = 6,
    /// <summary>Security and authentication settings.</summary>
    Security = 7,
    /// <summary>Maintenance mode and platform state.</summary>
    Maintenance = 8,
    /// <summary>Additive to ARCHITECTURE.md §2.7's list; see the M7 spec §1.</summary>
    Retention = 9
}
