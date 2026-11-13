namespace CaseManagement.Api.Services.EmailIntake;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CaseManagement.Api.Models;
using MimeKit;

/// <summary>Reading what an email says about itself: its identity, kind, sender, text and thread. Pure functions, unit-tested.</summary>
public static partial class EmailParsing
{
    /// <summary>A Message-ID without angle brackets, lower-case; null when there is none.</summary>
    public static string? NormalizeMessageId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var v = id.Trim().Trim('<', '>').Trim().ToLowerInvariant();
        return v.Length == 0 ? null : v;
    }

    /// <summary>
    /// The key that makes an email unique: its Message-ID, or — when it has none — a hash of who sent it, when, to whom and about
    /// what, so the same email delivered twice is still recognised.
    /// </summary>
    public static string MessageKey(MimeMessage m, string mailboxAddress)
    {
        if (NormalizeMessageId(m.MessageId) is { } id) return id.Length > 900 ? "sha256:" + Hash(id) : id;
        var basis = string.Join("|", m.From.Mailboxes.FirstOrDefault()?.Address?.ToLowerInvariant(), m.Date.ToUniversalTime().ToString("O"),
            m.Subject, string.Join(",", m.To.Mailboxes.Select(a => a.Address?.ToLowerInvariant())), mailboxAddress.ToLowerInvariant(),
            Hash(m.TextBody ?? m.HtmlBody ?? ""));
        return "sha256:" + Hash(basis);
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>
    /// Auto-replies (out of office…), bounces and mail from our own mailboxes never become cases and are never answered — answering
    /// them is how two automatic systems end up mailing each other forever.
    /// </summary>
    public static string Kind(MimeMessage m, IReadOnlyCollection<string> ownAddresses)
    {
        var from = m.From.Mailboxes.FirstOrDefault()?.Address?.ToLowerInvariant() ?? "";
        if (ownAddresses.Contains(from)) return EmailKinds.OwnMailbox;

        var local = from.Split('@')[0];
        if (local is "mailer-daemon" or "postmaster" || m.Body is MultipartReport) return EmailKinds.Bounce;
        if (m.Body is Multipart mp && mp.ContentType.IsMimeType("multipart", "report")) return EmailKinds.Bounce;

        var auto = m.Headers[HeaderId.AutoSubmitted];
        if (!string.IsNullOrWhiteSpace(auto) && !auto.Trim().Equals("no", StringComparison.OrdinalIgnoreCase)) return EmailKinds.AutoReply;
        if (m.Headers.Contains("X-Autoreply") || m.Headers.Contains("X-Autorespond") || m.Headers.Contains("X-Auto-Reply")) return EmailKinds.AutoReply;
        var precedence = m.Headers["Precedence"]?.Trim().ToLowerInvariant();
        if (precedence is "auto_reply" or "bulk" or "junk" or "list") return EmailKinds.AutoReply;
        if (local is "noreply" or "no-reply" or "donotreply" or "do-not-reply") return EmailKinds.AutoReply;
        return EmailKinds.Normal;
    }

    /// <summary>The domain of an address, lower-case; empty when it has none.</summary>
    public static string DomainOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at < 0 ? "" : address[(at + 1)..].Trim().TrimEnd('.').ToLowerInvariant();
    }

    /// <summary>A domain as stored: lower-case, no leading "@" or "www.", no trailing dot. Empty when it is not a plausible domain.</summary>
    public static string NormalizeDomain(string? domain)
    {
        var d = (domain ?? "").Trim().TrimStart('@').TrimEnd('.').ToLowerInvariant();
        if (d.StartsWith("www.")) d = d[4..];
        return DomainPattern().IsMatch(d) ? d : "";
    }

    [GeneratedRegex(@"^(?=.{3,253}$)([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex DomainPattern();

    /// <summary>
    /// The verified domain the sender belongs to: an exact match first, then the closest parent domain that accepts sub-domains.
    /// </summary>
    public static T? MatchDomain<T>(string senderDomain, IEnumerable<T> domains, Func<T, string> domainOf, Func<T, bool> includesSubdomains) where T : class
    {
        if (senderDomain.Length == 0) return null;
        var list = domains.ToList();
        var exact = list.FirstOrDefault(d => domainOf(d) == senderDomain);
        if (exact != null) return exact;
        return list.Where(d => includesSubdomains(d) && senderDomain.EndsWith("." + domainOf(d), StringComparison.Ordinal))
            .OrderByDescending(d => domainOf(d).Length)
            .FirstOrDefault();
    }

    /// <summary>
    /// True when the receiving mail server reported that the sender failed DMARC — the domain's own rule for who may send as it. Only
    /// the top-most Authentication-Results header counts: it is the one our own provider added; anything below came with the email.
    /// </summary>
    public static bool FailedDmarc(MimeMessage m)
    {
        var top = m.Headers.FirstOrDefault(h => h.Id == HeaderId.AuthenticationResults)?.Value;
        return top != null && DmarcFail().IsMatch(top);
    }

    [GeneratedRegex(@"\bdmarc\s*=\s*fail\b", RegexOptions.IgnoreCase)]
    private static partial Regex DmarcFail();

    /// <summary>The readable text of the email: the plain-text part, or the HTML part turned into text. Never HTML.</summary>
    public static string BodyText(MimeMessage m)
    {
        var text = m.TextBody;
        if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(m.HtmlBody)) text = HtmlToText(m.HtmlBody);
        return NormalizeWhitespace(text ?? "");
    }

    public static string HtmlToText(string html)
    {
        var s = Regex.Replace(html, @"<(script|style|head)[^>]*>.*?</\1>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"<br\s*/?>|</p>|</div>|</tr>|</li>|</h[1-6]>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<li[^>]*>", "• ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", "");
        return WebUtility.HtmlDecode(s);
    }

    private static string NormalizeWhitespace(string s)
    {
        s = s.Replace("\r\n", "\n").Replace('\r', '\n').Replace(' ', ' ');
        s = Regex.Replace(s, @"[ \t]+\n", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    public static string Snippet(string body, int length = 180)
    {
        var flat = Regex.Replace(body, @"\s+", " ").Trim();
        return flat.Length <= length ? flat : flat[..length].TrimEnd() + "…";
    }

    /// <summary>The message ids this email answers (In-Reply-To and References), normalised.</summary>
    public static IReadOnlyList<string> ThreadIds(string? inReplyTo, string? references) =>
        new[] { inReplyTo }.Concat((references ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(NormalizeMessageId).OfType<string>().Distinct().ToList();

    /// <summary>A case number quoted in a subject the way our acknowledgement writes it: "[Ticket C-01248]" or "[C-01248]".</summary>
    public static string? CaseNumberInSubject(string? subject)
    {
        var m = SubjectCaseNumber().Match(subject ?? "");
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    [GeneratedRegex(@"\[(?:Ticket\s*)?([A-Za-z]{1,6}-\d{4,}(?:-[A-Za-z]\d{2})?)\]", RegexOptions.IgnoreCase)]
    private static partial Regex SubjectCaseNumber();

    /// <summary>"Re: " + the subject, unless it already starts with a reply prefix.</summary>
    public static string ReplySubject(string subject)
    {
        var s = string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject.Trim();
        return Regex.IsMatch(s, @"^(re|aw|sv)\s*:", RegexOptions.IgnoreCase) ? s : "Re: " + s;
    }

    public static string Truncate(string value, int max) => value.Length <= max ? value : value[..Math.Max(0, max - 1)].TrimEnd() + "…";

    /// <summary>The files attached to an email — not the pictures embedded in its body (signatures, logos).</summary>
    public static IEnumerable<MimeEntity> RealAttachments(MimeMessage m) =>
        m.Attachments.Where(a => a is MessagePart || a is MimePart { IsAttachment: true } || a is MimePart p && string.IsNullOrEmpty(p.ContentId));

    public static string AttachmentName(MimeEntity entity, int index) => entity switch
    {
        MimePart p when !string.IsNullOrWhiteSpace(p.FileName) => p.FileName!,
        MessagePart mp => (mp.ContentDisposition?.FileName ?? mp.Message?.Subject ?? $"attached-message-{index}") is var n && n.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) ? n : n + ".eml",
        _ => $"attachment-{index}",
    };
}
