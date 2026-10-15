namespace CaseManagement.Api.HostIntegration;

using System.Security.Cryptography;

/// <summary>
/// Reads the OmniConnect platform's RS256 public key from configuration, in the forms the platform's
/// <c>.env</c> files carry it: a PEM block, a PEM with literal <c>\n</c> escapes (one-line env var), or the
/// bare base64 body. Same tolerance as the other services' key loaders, so one value works everywhere.
/// </summary>
public static class RsaPublicKey
{
    public static RSA Load(string pem)
    {
        var rsa = RSA.Create();
        var normalized = pem.Trim().Trim('"').Trim('\'').Replace("\\n", "\n").Replace("\r", "").Trim();
        try
        {
            rsa.ImportFromPem(normalized);
        }
        catch (ArgumentException)
        {
            var base64 = normalized
                .Replace("-----BEGIN PUBLIC KEY-----", "")
                .Replace("-----END PUBLIC KEY-----", "")
                .Replace("-----BEGIN RSA PUBLIC KEY-----", "")
                .Replace("-----END RSA PUBLIC KEY-----", "")
                .Replace("\n", "")
                .Replace(" ", "")
                .Trim();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(base64), out _);
        }
        return rsa;
    }
}
