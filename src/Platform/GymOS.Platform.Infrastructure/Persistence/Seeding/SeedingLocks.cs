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
/// <para><b>What the lock guarantees, precisely.</b> <c>SqlServerAdvisoryLock</c> takes
/// <c>sp_getapplock</c> with <c>@LockOwner = 'Transaction'</c>: the lock is held by the transaction
/// that acquired it and released when that transaction commits or rolls back. Each seeder opens its
/// own transaction and acquires inside it, so the guarantee is <em>per seeder</em>: one seeder's
/// check-then-insert in one replica is atomic against the same seeder in another replica. It does
/// not span the seeders and it does not cover the whole start-up window — nor does it need to: each
/// seeder is idempotent on its own, and each one's race is only with itself. (An earlier version of
/// this comment claimed a whole-window guarantee; the M4.1 close-out review corrected it.)</para>
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
