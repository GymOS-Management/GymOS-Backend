using GymOS.BuildingBlocks.Application.Abstractions;

namespace GymOS.Platform.Infrastructure.Persistence.Seeding;

/// <summary>
/// The <see cref="IAdvisoryLock"/> names the startup-scope seeders serialize on.
///
/// <para>One constant, not one literal string typed identically in each seeder: two copies of a
/// name that must match, with nothing enforcing the match, is a silent-failure mode — an edit to
/// one and not the other stops excluding those seeders from each other with no test failing, and
/// the boot race this lock exists to close comes back unnoticed.</para>
///
/// <para><b>What the lock guarantees, precisely.</b> The name is shared by all three seeders, so
/// <c>sp_getapplock</c> makes every seeder in every replica mutually exclusive with every other one —
/// mutual exclusion spans all three seeders and all replicas because the name is shared. What is
/// narrower is the scope: <c>@LockOwner = 'Transaction'</c> means the lock is held for exactly one
/// seeder's transaction and released when that transaction commits or rolls back, so each seeder's own
/// check-then-insert is the atomic unit — which is the only guarantee any seeder needs. It does not
/// cover the whole start-up window: a replica that finishes its three seeders releases the lock three
/// times, and another replica may interleave between them, seeing committed rows each time it
/// acquires.</para>
/// </summary>
internal static class SeedingLocks
{
    /// <summary>
    /// Shared by <see cref="PlatformIdentitySeeder"/>, <see cref="FeatureCatalogSeeder"/> and
    /// <see cref="SuperAdminSeeder"/>. One name rather than three: the seeders run back to back in one
    /// start-up scope, so a second replica booting at the same moment queues behind whichever seeder
    /// the first replica is in, and each seeder sees the previous one's rows committed — which matters
    /// for <see cref="SuperAdminSeeder"/>, whose insert needs the role the identity seeder wrote.
    /// </summary>
    internal const string CatalogSeed = "platform.catalog-seed";
}
