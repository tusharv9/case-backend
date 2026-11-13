namespace CaseManagement.Api.Services.EmailIntake;

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CaseManagement.Api.Models;
using DnsClient;
using MimeKit;
using MimeKit.Cryptography;

/// <summary>What an email proves about its sender's domain (<see cref="EmailAuthenticity"/>) and what was found, for the Inbox.</summary>
public sealed record EmailAuthenticationResult(string Result, string Detail);

/// <summary>
/// Decides whether an email really comes from the domain in its From address. A From line proves nothing — anyone can write any
/// address — so the email has to carry the domain's own DKIM signature (checked against the public key the domain publishes in
/// DNS), aligned with the From domain as DMARC defines it.
/// </summary>
public interface IEmailAuthenticator
{
    Task<EmailAuthenticationResult> CheckAsync(MimeMessage message, string fromDomain, CancellationToken ct);
}

/// <summary>A DNS TXT lookup. <see cref="DnsTxtAnswer.Ok"/> is false when DNS could not answer (not when the name has no records).</summary>
public interface IDnsTxtResolver
{
    Task<DnsTxtAnswer> TxtAsync(string name, CancellationToken ct);
}

public sealed record DnsTxtAnswer(bool Ok, IReadOnlyList<string> Records)
{
    public static readonly DnsTxtAnswer Failed = new(false, Array.Empty<string>());
}

/// <summary>
/// Checked here, by Case Management itself, because a receiving mail server may not record SPF/DKIM/DMARC results at all (the
/// company's cPanel/Exim server does not), and a result header the sender wrote is worthless. Only "fail" is ever taken from an
/// existing Authentication-Results header: a forger gains nothing by claiming his own email failed.
/// </summary>
/// <remarks>
/// <para><b>Pass</b>: a valid DKIM signature by the From domain (or its parent/sub-domain — relaxed alignment).</para>
/// <para><b>Fail</b>: a signature by that domain that does not verify (the email was altered or forged), or no valid signature
/// while the domain's DMARC policy is quarantine/reject — the domain itself asks for such mail to be refused.</para>
/// <para><b>None</b>: no valid signature, and the domain does not ask for refusal. <b>Unknown</b>: DNS could not be reached.</para>
/// <para>SPF is deliberately not checked: it needs the address of the server that handed the email over and breaks on forwarding.</para>
/// </remarks>
public sealed partial class EmailAuthenticator(IDnsTxtResolver dns) : IEmailAuthenticator
{
    public async Task<EmailAuthenticationResult> CheckAsync(MimeMessage message, string fromDomain, CancellationToken ct)
    {
        fromDomain = fromDomain.Trim().TrimEnd('.').ToLowerInvariant();
        var details = new List<string>();
        bool alignedPass = false, alignedBroken = false, dnsTrouble = false;

        var verifier = new DkimVerifier(new MimeKitDns(dns));
        foreach (var signature in message.Headers.Where(h => h.Id == HeaderId.DkimSignature).Take(5))
        {
            var result = await verifier.VerifyAsync(message, signature, ct);
            var signer = (result.Domain ?? SignerDomain(signature.Value))?.Trim().TrimEnd('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(signer)) { details.Add("dkim=permerror (unreadable signature)"); continue; }
            var aligned = Aligned(signer, fromDomain);
            switch (result.Status)
            {
                case DkimSignatureStatus.Pass:
                    details.Add($"dkim=pass d={signer}");
                    alignedPass |= aligned;
                    break;
                case DkimSignatureStatus.Fail:
                    // The signature does not match the email: altered on the way, or forged.
                    details.Add($"dkim=fail d={signer}");
                    alignedBroken |= aligned;
                    break;
                case DkimSignatureStatus.TempError:
                    details.Add($"dkim=temperror d={signer}");
                    dnsTrouble = true;
                    break;
                default:
                    // PermError/Policy: malformed, no key published, or a weak algorithm — proves nothing either way.
                    details.Add($"dkim=permerror d={signer}");
                    break;
            }
        }
        if (details.Count == 0) details.Add("dkim=none");

        if (alignedPass) return new(EmailAuthenticity.Pass, Join(details, "dmarc=pass"));

        var policy = await DmarcPolicyAsync(fromDomain, ct);
        if (policy == null)
        {
            // DNS could not be reached: a broken signature is still a broken signature; otherwise we cannot say.
            return alignedBroken
                ? new(EmailAuthenticity.Fail, Join(details, "dmarc=temperror"))
                : new(EmailAuthenticity.Unknown, Join(details, "dmarc=temperror"));
        }

        var dmarc = policy.Length == 0 ? "dmarc=none (no policy published)" : $"dmarc=fail p={policy}";
        if (alignedBroken) return new(EmailAuthenticity.Fail, Join(details, dmarc));
        if (EmailParsing.FailedDmarc(message)) return new(EmailAuthenticity.Fail, Join(details, "dmarc=fail (receiving server)"));
        if (policy is "reject" or "quarantine") return new(EmailAuthenticity.Fail, Join(details, dmarc));
        if (dnsTrouble) return new(EmailAuthenticity.Unknown, Join(details, dmarc));
        return new(EmailAuthenticity.None, Join(details, dmarc));
    }

    /// <summary>
    /// The DMARC policy (p=, or sp= when inherited from a parent domain) for the domain: "none", "quarantine", "reject", ""
    /// when no policy is published, or null when DNS could not answer.
    /// </summary>
    private async Task<string?> DmarcPolicyAsync(string domain, CancellationToken ct)
    {
        var name = domain;
        var inherited = false;
        while (true)
        {
            var answer = await dns.TxtAsync("_dmarc." + name, ct);
            if (!answer.Ok) return null;
            var record = answer.Records.FirstOrDefault(r => r.TrimStart().StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase));
            if (record != null)
            {
                var tags = Tags(record);
                var p = inherited && tags.TryGetValue("sp", out var sp) ? sp : tags.GetValueOrDefault("p", "none");
                return p.ToLowerInvariant();
            }
            // Fall back to the parent domain (an approximation of the organisational domain), never above two labels.
            var dot = name.IndexOf('.');
            if (dot < 0 || name[(dot + 1)..].Count(c => c == '.') < 1) return "";
            name = name[(dot + 1)..];
            inherited = true;
        }
    }

    /// <summary>Relaxed alignment: the signing domain and the From domain are the same, or one is a sub-domain of the other.</summary>
    public static bool Aligned(string signer, string fromDomain) =>
        signer == fromDomain || fromDomain.EndsWith("." + signer, StringComparison.Ordinal) || signer.EndsWith("." + fromDomain, StringComparison.Ordinal);

    private static string? SignerDomain(string headerValue)
    {
        var m = SignerTag().Match(headerValue);
        return m.Success ? m.Groups[1].Value.Trim().TrimEnd('.').ToLowerInvariant() : null;
    }

    private static Dictionary<string, string> Tags(string record) => record.Split(';')
        .Select(t => t.Split('=', 2))
        .Where(kv => kv.Length == 2)
        .GroupBy(kv => kv[0].Trim().ToLowerInvariant())
        .ToDictionary(g => g.Key, g => g.First()[1].Trim());

    private static string Join(List<string> details, string dmarc) => EmailParsing.Truncate(string.Join("; ", details.Append(dmarc)), 500);

    [GeneratedRegex(@"(?:^|;)\s*d\s*=\s*([^;\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SignerTag();
}

/// <summary>MimeKit's DNS interface (used to fetch DKIM keys) over <see cref="IDnsTxtResolver"/>, so both checks share one cache.</summary>
internal sealed class MimeKitDns(IDnsTxtResolver dns) : IDnsResolver
{
    public DnsTxtResponse QueryTxt(string domain, CancellationToken cancellationToken = default) =>
        QueryTxtAsync(domain, cancellationToken).GetAwaiter().GetResult();

    public async Task<DnsTxtResponse> QueryTxtAsync(string domain, CancellationToken cancellationToken = default)
    {
        var answer = await dns.TxtAsync(domain, cancellationToken);
        return answer.Ok ? new DnsTxtResponse(answer.Records) : new DnsTxtResponse(DnsQueryStatus.TemporaryFailure);
    }
}

/// <summary>TXT lookups through the system's DNS servers, with answers cached for ten minutes (failures are not cached).</summary>
public sealed class DnsClientTxtResolver : IDnsTxtResolver
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private readonly LookupClient _client = new(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(5), Retries = 1, UseCache = false });
    private readonly ConcurrentDictionary<string, (DateTime Until, DnsTxtAnswer Answer)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<DnsClientTxtResolver> _logger;

    public DnsClientTxtResolver(ILogger<DnsClientTxtResolver> logger) => _logger = logger;

    public async Task<DnsTxtAnswer> TxtAsync(string name, CancellationToken ct)
    {
        if (_cache.TryGetValue(name, out var hit) && hit.Until > DateTime.UtcNow) return hit.Answer;
        try
        {
            var result = await _client.QueryAsync(name, QueryType.TXT, QueryClass.IN, ct);
            if (result.HasError && result.Header.ResponseCode != DnsHeaderResponseCode.NotExistentDomain)
            {
                _logger.LogWarning("DNS TXT lookup for {Name} failed: {Error}", name, result.ErrorMessage);
                return DnsTxtAnswer.Failed;
            }
            var records = result.Answers.TxtRecords().Select(r => string.Concat(r.Text)).ToList();
            var answer = new DnsTxtAnswer(true, records);
            if (_cache.Count > 5000) _cache.Clear();
            _cache[name] = (DateTime.UtcNow.Add(CacheFor), answer);
            return answer;
        }
        catch (Exception ex) when (ex is DnsResponseException or TimeoutException or System.Net.Sockets.SocketException)
        {
            _logger.LogWarning("DNS TXT lookup for {Name} failed: {Error}", name, ex.Message);
            return DnsTxtAnswer.Failed;
        }
    }
}
