namespace CaseManagement.Api.Services.Customer360;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

/// <summary>Keys for protecting Customer 360 identifiers (NRIC, BRN, passport…) stored on cases.</summary>
/// <remarks>
/// Bound from <c>CustomerIdentifiers</c> (<c>CustomerIdentifiers__EncryptionKey</c> / <c>__HashKey</c> in .env).
/// Each is 32 random bytes, base64. Losing the encryption key makes stored identifiers unreadable — the cases
/// can then no longer show their customer — so it belongs in the deployment's secret store, backed up.
/// </remarks>
public class CustomerIdentifierOptions
{
    public const string SectionName = "CustomerIdentifiers";

    public string? EncryptionKey { get; set; }
    public string? HashKey { get; set; }
}

/// <summary>
/// The only place a Customer 360 identifier is turned into what a case stores, and back.
/// </summary>
/// <remarks>
/// <para><b>Why the identifier is stored at all.</b> Customer 360 can find a customer only by a search
/// (NRIC, BRN, passport…), not by its own CRM key, so a case must keep the search value to show its customer
/// later. It keeps nothing else about them.</para>
///
/// <para><b>Encrypted</b> (AES-256-GCM, random nonce): readable only by this service, with its key. A database
/// copy, a report or a support query sees ciphertext. <b>Hashed</b> (HMAC-SHA256 with a separate key) for
/// lookups: "all cases for this NRIC" is an index seek on the hash, never a decrypt-everything scan, and the
/// hash cannot be reversed or recomputed without the key.</para>
///
/// <para>Format <c>v1:&lt;base64(nonce|ciphertext|tag)&gt;</c>; the version leaves room for key rotation.
/// Without configured keys every operation refuses — nothing is ever stored in the clear by default.</para>
/// </remarks>
public interface ICustomerIdentifierProtector
{
    bool IsConfigured { get; }
    string Protect(string lookupType, string identifier);
    string Unprotect(string protectedValue);
    string Hash(string lookupType, string identifier);
}

public sealed class CustomerIdentifierProtector : ICustomerIdentifierProtector
{
    private const string Version = "v1";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[]? _encryptionKey;
    private readonly byte[]? _hashKey;

    public CustomerIdentifierProtector(IOptions<CustomerIdentifierOptions> options)
    {
        _encryptionKey = ReadKey(options.Value.EncryptionKey, "EncryptionKey");
        _hashKey = ReadKey(options.Value.HashKey, "HashKey");
    }

    public bool IsConfigured => _encryptionKey != null && _hashKey != null;

    public string Protect(string lookupType, string identifier)
    {
        var key = _encryptionKey ?? throw NotConfigured();
        var plain = Encoding.UTF8.GetBytes(Canonical(lookupType, identifier));
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize)) aes.Encrypt(nonce, plain, cipher, tag);

        var packed = new byte[NonceSize + cipher.Length + TagSize];
        nonce.CopyTo(packed, 0);
        cipher.CopyTo(packed, NonceSize);
        tag.CopyTo(packed, NonceSize + cipher.Length);
        return $"{Version}:{Convert.ToBase64String(packed)}";
    }

    /// <summary>The identifier (without its type) a <see cref="Protect"/> call stored.</summary>
    public string Unprotect(string protectedValue)
    {
        var key = _encryptionKey ?? throw NotConfigured();
        if (!protectedValue.StartsWith(Version + ":", StringComparison.Ordinal))
            throw new CryptographicException("Unknown customer identifier format.");
        var packed = Convert.FromBase64String(protectedValue[(Version.Length + 1)..]);
        if (packed.Length < NonceSize + TagSize) throw new CryptographicException("Customer identifier is truncated.");

        var nonce = packed.AsSpan(0, NonceSize);
        var cipher = packed.AsSpan(NonceSize, packed.Length - NonceSize - TagSize);
        var tag = packed.AsSpan(packed.Length - TagSize);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(key, TagSize)) aes.Decrypt(nonce, cipher, tag, plain);

        var canonical = Encoding.UTF8.GetString(plain);
        var separator = canonical.IndexOf('|');
        return separator < 0 ? canonical : canonical[(separator + 1)..];
    }

    public string Hash(string lookupType, string identifier)
    {
        var key = _hashKey ?? throw NotConfigured();
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Canonical(lookupType, identifier))));
    }

    /// <summary>Type and value together, so the same digits under two identifier types never collide.</summary>
    private static string Canonical(string lookupType, string identifier) =>
        $"{lookupType.Trim().ToUpperInvariant()}|{identifier.Trim()}";

    private static byte[]? ReadKey(string? configured, string name)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        byte[] key;
        try { key = Convert.FromBase64String(configured.Trim()); }
        catch (FormatException) { throw new InvalidOperationException($"CustomerIdentifiers:{name} must be base64 (32 random bytes)."); }
        if (key.Length != 32) throw new InvalidOperationException($"CustomerIdentifiers:{name} must be exactly 32 bytes, base64-encoded.");
        return key;
    }

    private static InvalidOperationException NotConfigured() =>
        new("Customer identifier protection is not configured (CustomerIdentifiers:EncryptionKey and CustomerIdentifiers:HashKey).");
}
