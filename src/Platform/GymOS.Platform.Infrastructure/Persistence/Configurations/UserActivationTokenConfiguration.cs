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

        // No xmin. A token is written once and consumed once, and the consume is guarded by the
        // aggregate's own `IsUsable` check inside the same transaction — there is no field a second
        // writer could overwrite unnoticed. `RefreshToken` is exempt for the same reason.
    }
}
