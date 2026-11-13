namespace CaseManagement.Api.Services.EmailIntake;

using System.Security.Cryptography;
using System.Text;
using CaseManagement.Api.Services.Customer360;
using Microsoft.Extensions.Options;

/// <summary>Encrypts mailbox passwords for storage (AES-256-GCM, random nonce, <c>v1:</c> format) and decrypts them to sign in.</summary>
/// <remarks>
/// The key is <c>EmailIntake:SecretKey</c>, or — when that is not set — one derived (HKDF) from <c>CustomerIdentifiers:EncryptionKey</c>
/// with a label of its own, so it is never the same key that protects customer identifiers. Without either, saving a password is
/// refused: nothing is ever stored in the clear. Losing the key means re-entering each mailbox's password, nothing worse.
/// </remarks>
public interface IMailboxSecretProtector
{
    bool IsConfigured { get; }
    string Protect(string secret);
    string Unprotect(string protectedValue);
}

public sealed class MailboxSecretProtector : IMailboxSecretProtector
{
    private const string Version = "v1";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] DerivationLabel = Encoding.UTF8.GetBytes("CaseManagement.EmailIntake.MailboxSecret.v1");
    private readonly byte[]? _key;

    public MailboxSecretProtector(IOptions<EmailIntakeOptions> options, IOptions<CustomerIdentifierOptions> identifiers)
    {
        _key = Decode(options.Value.SecretKey)
            ?? (Decode(identifiers.Value.EncryptionKey) is { } root ? HKDF.DeriveKey(HashAlgorithmName.SHA256, root, 32, info: DerivationLabel) : null);
    }

    public bool IsConfigured => _key != null;

    public string Protect(string secret)
    {
        var key = _key ?? throw new InvalidOperationException(NotConfiguredMessage);
        var plain = Encoding.UTF8.GetBytes(secret);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize)) aes.Encrypt(nonce, plain, cipher, tag);
        return $"{Version}:{Convert.ToBase64String([.. nonce, .. cipher, .. tag])}";
    }

    public string Unprotect(string protectedValue)
    {
        var key = _key ?? throw new InvalidOperationException(NotConfiguredMessage);
        if (!protectedValue.StartsWith(Version + ":", StringComparison.Ordinal)) throw new CryptographicException("Unknown secret format.");
        var all = Convert.FromBase64String(protectedValue[(Version.Length + 1)..]);
        if (all.Length < NonceSize + TagSize) throw new CryptographicException("The stored secret is damaged.");
        var nonce = all.AsSpan(0, NonceSize);
        var cipher = all.AsSpan(NonceSize, all.Length - NonceSize - TagSize);
        var tag = all.AsSpan(all.Length - TagSize);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(key, TagSize)) aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    public const string NotConfiguredMessage =
        "Mailbox passwords cannot be stored yet: set EmailIntake__SecretKey (32 random bytes, base64) or CustomerIdentifiers__EncryptionKey in the Case Management .env.";

    private static byte[]? Decode(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64.Trim());
            return bytes.Length == 32 ? bytes : null;
        }
        catch (FormatException) { return null; }
    }
}
