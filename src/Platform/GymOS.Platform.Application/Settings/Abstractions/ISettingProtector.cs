namespace GymOS.Platform.Application.Settings.Abstractions;

/// <summary>Encrypts and decrypts a sensitive setting's value with purpose
/// <c>GymOS.PlatformSettings.v1</c> and the key itself as a sub-purpose (M7 spec §3), so a ciphertext
/// cannot be moved to another key's row and decrypted there.</summary>
public interface ISettingProtector
{
    string Protect(string key, string plaintext);

    string Unprotect(string key, string ciphertext);
}
