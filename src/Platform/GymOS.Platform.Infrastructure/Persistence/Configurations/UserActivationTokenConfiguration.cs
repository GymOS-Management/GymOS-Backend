using GymOS.BuildingBlocks.Infrastructure.Persistence;
using GymOS.Platform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.Platform.Infrastructure.Persistence.Configurations;

/// <summary>
/// Activation tokens (ADR-53), shaped like <c>RefreshTokenConfiguration</c> — the same kind of thing,
/// stored the same way.
/// </summary>
public sealed class UserActivationTokenConfiguration : IEntityTypeConfiguration<UserActivationToken>
{
    public void Configure(EntityTypeBuilder<UserActivationToken> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("user_activation_tokens", "platform");
        builder.HasKey(token => token.Id);

        builder.Property(token => token.TokenHash).HasMaxLength(128);

        // Cascade: a token is meaningless without the account it opens, and unlike an invoice it is
        // not a record of anything anyone reads later.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(token => token.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Provenance only. Losing the administrator who sent an invitation must not invalidate the
        // invitation — the PlanPriceId precedent.
        //
        // ClientSetNull rather than SetNull, and the difference is SQL Server's, not a change of
        // intent. This table has two foreign keys to `users`, and SQL Server refuses more than one
        // path between a pair of tables that carries a referential action: "Introducing FOREIGN KEY
        // constraint 'fk_user_activation_tokens_user_user_id' on table 'user_activation_tokens' may
        // cause cycles or multiple cascade paths" (error 1785). PostgreSQL has no such rule and
        // enforced both actions itself. One of the two has to become NO ACTION in the database, and
        // it is this one: the cascade above is the load-bearing half — a token is meaningless without
        // the account it opens — while this is a nullable provenance column.
        //
        // ClientSetNull emits NO ACTION in the schema and keeps EF's own set-to-null for dependents
        // it has loaded, so the intent still holds wherever a delete goes through the change tracker.
        //
        // Nothing exercises either path today: users are suspended, never deleted, and the codebase
        // has no user-deletion command at all. If one is ever added it must null this column for the
        // departing administrator's tokens before deleting the row, or the database will refuse the
        // delete instead of nulling it. That is the one observable difference, and it fails loudly
        // rather than silently.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(token => token.CreatedByUserId)
            .OnDelete(DeleteBehavior.ClientSetNull);

        // The lookup, and the guarantee that two tokens cannot collide.
        builder.HasIndex(token => token.TokenHash).IsUnique();

        // Backs "invalidate everything outstanding for this user" before issuing a new one.
        builder.HasIndex(token => new { token.UserId, token.ConsumedAtUtc });

        // Backs the retention purge.
        builder.HasIndex(token => token.ExpiresAtUtc);

        // Optimistic concurrency, like every other aggregate here (spec §2/§3).
        //
        // This carried no row version, on the reasoning that a token is written once and consumed once so
        // "there is no field a second writer could overwrite unnoticed". That frames the risk as a
        // lost update, and the risk is not a lost update: it is two readers both passing `IsUsable`
        // before either commits. Nothing then stopped both consuming, because the UPDATE carried no
        // version predicate.
        //
        // The window is narrow but it is exactly the guarantee this table exists to provide. It also
        // is not closed by the User aggregate's own row version: SetPasswordCommandHandler's
        // policy-rejection path commits the consumption ALONE, touching no user row, so on that path
        // there was nothing to arbitrate.
        //
        // The row version is a real column here, unlike PostgreSQL's xmin, which every table carried
        // for free — so adding it to this table is a schema change as well as a model change.
        builder.HasGymOSRowVersion();
    }
}
