using Microsoft.AspNetCore.DataProtection;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Encrypts and decrypts notification provider config values using ASP.NET Core Data Protection.
/// Values are prefixed with <see cref="EncryptedPrefix"/> to distinguish encrypted from plaintext.
/// This enables transparent migration: existing plaintext values are used as-is on read,
/// then encrypted when next saved.
/// </summary>
public sealed class ConfigEncryptionService
{
    internal const string EncryptedPrefix = "ENC.v1:";
    private const string Purpose = "AgentAcademy.NotificationConfig";

    private readonly IDataProtector _protector;

    public ConfigEncryptionService(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    /// <summary>
    /// Encrypts a value and prepends the <see cref="EncryptedPrefix"/> marker.
    /// Returns the original value if it is null or empty.
    /// </summary>
    public string Encrypt(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var encrypted = _protector.Protect(value);
        return $"{EncryptedPrefix}{encrypted}";
    }

    /// <summary>
    /// Attempts to decrypt a value. If the value has the <see cref="EncryptedPrefix"/> marker,
    /// decryption is attempted. If decryption fails (e.g., key rotation), returns false.
    /// Plaintext values (no prefix) are returned as-is with success = true.
    /// </summary>
    public bool TryDecrypt(string value, out string result)
    {
        if (string.IsNullOrEmpty(value))
        {
            result = value;
            return true;
        }

        if (!value.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            result = value; // plaintext — will be encrypted on next save
            return true;
        }

        var ciphertext = value[EncryptedPrefix.Length..];
        try
        {
            result = _protector.Unprotect(ciphertext);
            return true;
        }
        catch (Exception)
        {
            result = "";
            return false;
        }
    }

    /// <summary>
    /// Returns true if the value is already encrypted (has the prefix marker).
    /// </summary>
    public static bool IsEncrypted(string? value)
        => value?.StartsWith(EncryptedPrefix, StringComparison.Ordinal) == true;
}
