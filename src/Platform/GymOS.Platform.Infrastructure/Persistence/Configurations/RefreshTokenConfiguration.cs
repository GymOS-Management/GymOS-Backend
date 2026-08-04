using GymOS.Platform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.Platform.Infrastructure.Persistence.Configurations;

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("refresh_tokens", "platform");
        builder.HasKey(token => token.Id);

        builder.Property(token => token.TokenHash).HasMaxLength(128);
        builder.Property(token => token.CreatedByIp).HasMaxLength(45); // IPv6 max
        builder.Property(token => token.RevokedByIp).HasMaxLength(45);
        builder.Property(token => token.RevocationReason).HasMaxLength(200);
        builder.Property(token => token.ReplacedByTokenHash).HasMaxLength(128);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(token => token.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(token => token.TokenHash).IsUnique();
        builder.HasIndex(token => new { token.UserId, token.ExpiresAtUtc });
    }
}
