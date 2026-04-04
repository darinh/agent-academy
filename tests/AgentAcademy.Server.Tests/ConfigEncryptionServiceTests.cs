using AgentAcademy.Server.Notifications;
using Microsoft.AspNetCore.DataProtection;

namespace AgentAcademy.Server.Tests;

public class ConfigEncryptionServiceTests
{
    private readonly ConfigEncryptionService _service;

    public ConfigEncryptionServiceTests()
    {
        var provider = DataProtectionProvider.Create("AgentAcademy.Tests");
        _service = new ConfigEncryptionService(provider);
    }

    [Fact]
    public void Encrypt_ReturnsEncryptedValueWithPrefix()
    {
        var encrypted = _service.Encrypt("my-secret-token");

        Assert.StartsWith(ConfigEncryptionService.EncryptedPrefix, encrypted);
        Assert.NotEqual("my-secret-token", encrypted);
    }

    [Fact]
    public void TryDecrypt_EncryptedValue_ReturnsOriginal()
    {
        var original = "my-secret-bot-token-12345";
        var encrypted = _service.Encrypt(original);

        Assert.True(_service.TryDecrypt(encrypted, out var decrypted));
        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void TryDecrypt_PlaintextValue_ReturnsAsIs()
    {
        var plaintext = "not-encrypted-value";

        Assert.True(_service.TryDecrypt(plaintext, out var result));
        Assert.Equal(plaintext, result);
    }

    [Fact]
    public void Encrypt_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", _service.Encrypt(""));
    }

    [Fact]
    public void Encrypt_Null_ReturnsNull()
    {
        Assert.Null(_service.Encrypt(null!));
    }

    [Fact]
    public void TryDecrypt_EmptyString_ReturnsSuccess()
    {
        Assert.True(_service.TryDecrypt("", out var result));
        Assert.Equal("", result);
    }

    [Fact]
    public void TryDecrypt_Null_ReturnsSuccess()
    {
        Assert.True(_service.TryDecrypt(null!, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void TryDecrypt_CorruptedCiphertext_ReturnsFalse()
    {
        var corrupted = $"{ConfigEncryptionService.EncryptedPrefix}totally-not-valid-ciphertext";

        Assert.False(_service.TryDecrypt(corrupted, out var result));
        Assert.Equal("", result);
    }

    [Fact]
    public void TryDecrypt_DifferentKeyInstance_ReturnsFalse()
    {
        var provider1 = DataProtectionProvider.Create("Instance-A");
        var service1 = new ConfigEncryptionService(provider1);
        var encrypted = service1.Encrypt("secret");

        var provider2 = DataProtectionProvider.Create("Instance-B");
        var service2 = new ConfigEncryptionService(provider2);

        Assert.False(service2.TryDecrypt(encrypted, out _));
    }

    [Fact]
    public void TryDecrypt_LegacyPlaintextStartingWithOldPrefix_TreatedAsPlaintext()
    {
        // A plaintext value like "ENC:something" (without the ".v1:" version)
        // should be treated as plaintext since the prefix doesn't match "ENC.v1:"
        var legacyLike = "ENC:some-old-value";

        Assert.True(_service.TryDecrypt(legacyLike, out var result));
        Assert.Equal(legacyLike, result);
    }

    [Fact]
    public void RoundTrip_SpecialCharacters_PreservesValue()
    {
        var special = "p@$$w0rd!#%&*()_+-=[]{}|;':\",./<>?";
        var encrypted = _service.Encrypt(special);

        Assert.True(_service.TryDecrypt(encrypted, out var decrypted));
        Assert.Equal(special, decrypted);
    }

    [Fact]
    public void RoundTrip_LongValue_PreservesValue()
    {
        var longValue = new string('A', 10_000);
        var encrypted = _service.Encrypt(longValue);

        Assert.True(_service.TryDecrypt(encrypted, out var decrypted));
        Assert.Equal(longValue, decrypted);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("plain-text", false)]
    [InlineData("ENC.v1:some-cipher", true)]
    [InlineData("ENC:old-format", false)] // old format is not recognized
    public void IsEncrypted_ReturnsCorrectResult(string? value, bool expected)
    {
        Assert.Equal(expected, ConfigEncryptionService.IsEncrypted(value));
    }

    [Fact]
    public void Encrypt_SameValue_ProducesDifferentCiphertexts()
    {
        var a = _service.Encrypt("same-value");
        var b = _service.Encrypt("same-value");

        Assert.True(_service.TryDecrypt(a, out var da));
        Assert.True(_service.TryDecrypt(b, out var db));
        Assert.Equal("same-value", da);
        Assert.Equal("same-value", db);

        // Ciphertexts differ (non-deterministic encryption)
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TryDecrypt_EmptySecretValue_IsDistinguishableFromFailure()
    {
        // An explicitly empty value encrypts to empty (passthrough)
        var encrypted = _service.Encrypt("");
        Assert.True(_service.TryDecrypt(encrypted, out var result));
        Assert.Equal("", result);

        // A corrupted value returns false
        var corrupted = $"{ConfigEncryptionService.EncryptedPrefix}garbage";
        Assert.False(_service.TryDecrypt(corrupted, out _));
    }
}
