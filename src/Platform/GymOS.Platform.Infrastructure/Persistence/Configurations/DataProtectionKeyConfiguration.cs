using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.Platform.Infrastructure.Persistence.Configurations;

/// <summary>
/// The Data Protection key ring (M7 spec §3). In the database rather than on disk because UAT scales
/// to zero and a replica that comes back on a fresh file system must still decrypt yesterday's row.
/// Until Key Vault protects it (AZURE.md §7) the ring sits beside the ciphertext — stated there.
/// </summary>
public sealed class DataProtectionKeyConfiguration : IEntityTypeConfiguration<DataProtectionKey>
{
    public void Configure(EntityTypeBuilder<DataProtectionKey> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("data_protection_keys", "platform");
        builder.HasKey(key => key.Id);
        builder.Property(key => key.FriendlyName).HasMaxLength(200);
    }
}
