namespace GymOS.Platform.Infrastructure.Persistence.Seeding;

/// <summary>
/// The initial SuperAdmin account, from configuration only (section <c>Seed:SuperAdmin</c>).
///
/// There is deliberately no default here and no fallback anywhere: a hardcoded initial
/// credential is the single most reliably exploited defect in self-hosted software,
/// because it ships identically to every installation and outlives every intention to
/// change it. Unset means the seeder does nothing and says so.
///
/// Supply it out of band — <c>dotnet user-secrets</c> locally, environment or Key Vault
/// elsewhere:
/// <code>
/// dotnet user-secrets set "Seed:SuperAdmin:Email" "you@example.com"
/// dotnet user-secrets set "Seed:SuperAdmin:Password" "&lt;a long unique password&gt;"
/// </code>
/// </summary>
public sealed class SuperAdminSeedOptions
{
    public const string SectionName = "Seed:SuperAdmin";

    public string? Email { get; set; }

    public string? Password { get; set; }

    public string FirstName { get; set; } = "Platform";

    public string LastName { get; set; } = "SuperAdmin";

    /// <summary>Both halves of the credential present — anything less is not a configuration.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Password);
}
