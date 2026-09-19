using GymOS.Platform.Application.Settings.Abstractions;
using Microsoft.AspNetCore.DataProtection;

namespace GymOS.Platform.Infrastructure.Settings;

/// <summary>Data Protection with the key as a sub-purpose: a ciphertext cannot be moved to another key's row (M7 spec §3).</summary>
internal sealed class DataProtectionSettingProtector : ISettingProtector
{
    public const string Purpose = "GymOS.PlatformSettings.v1";

    private readonly IDataProtectionProvider _provider;

    public DataProtectionSettingProtector(IDataProtectionProvider provider) => _provider = provider;

    public string Protect(string key, string plaintext) => _provider.CreateProtector(Purpose, key).Protect(plaintext);

    public string Unprotect(string key, string ciphertext) => _provider.CreateProtector(Purpose, key).Unprotect(ciphertext);
}
