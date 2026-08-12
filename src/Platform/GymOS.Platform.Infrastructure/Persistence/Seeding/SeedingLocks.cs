namespace GymOS.Platform.Infrastructure.Persistence.Seeding;

/// <summary>
/// The <see cref="GymOS.BuildingBlocks.Application.Abstractions.IAdvisoryLock"/> names the
/// startup-scope seeders serialize on.
///
/// <para>One constant, not one literal string typed identically in each seeder: two copies of a
/// name that must match, with nothing enforcing the match, is a silent-failure mode — an edit to
/// one and not the other stops excluding those seeders from each other with no test failing, and
/// the boot race this lock exists to close comes back unnoticed.</para>
/// </summary>
internal static class SeedingLocks
{
    /// <summary>
    /// Shared by <see cref="FeatureCatalogSeeder"/> and <see cref="PlatformIdentitySeeder"/>:
    /// both are startup-scope syncs against the same fresh database, run back-to-back from the
    /// same <c>Program.cs</c> scope, so one lock covering the whole startup-sync window is what
    /// stops any two instances — regardless of which seeder either one happens to be running —
    /// from both reading an empty catalogue and both inserting.
    /// </summary>
    internal const string CatalogSeed = "platform.catalog-seed";
}
