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

        // SetNull: provenance only. Losing the administrator who sent an invitation must not
        // invalidate the invitation — the PlanPriceId precedent.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(token => token.CreatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // The lookup, and the guarantee that two tokens cannot collide.
        builder.HasIndex(token => token.TokenHash).IsUnique();

        // Backs "invalidate everything outstanding for this user" before issuing a new one.
        builder.HasIndex(token => new { token.UserId, token.ConsumedAtUtc });

        // Backs the retention purge.
        builder.HasIndex(token => token.ExpiresAtUtc);

        // Optimistic concurrency, like every other aggregate here (spec §2/§3).
        //
        // This carried no xmin, on the reasoning that a token is written once and consumed once so
        // "there is no field a second writer could overwrite unnoticed". That frames the risk as a
        // lost update, and the risk is not a lost update: it is two readers both passing `IsUsable`
        // before either commits. Nothing then stopped both consuming, because the UPDATE carried no
        // version predicate.
        //
        // The window is narrow but it is exactly the guarantee this table exists to provide. It also
        // is not closed by the User aggregate's own xmin: SetPasswordCommandHandler's
        // policy-rejection path commits the consumption ALONE, touching no user row, so on that path
        // there was nothing to arbitrate.
        //
        // xmin is PostgreSQL's system column, so this costs no schema change — only the model
        // snapshot moves.
        builder.Property<uint>("xmin").IsRowVersion();
    }
}
