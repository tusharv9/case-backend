namespace CaseManagement.Api.Services.EmailIntake;

using System.Text.RegularExpressions;
using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Ganss.Xss;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MimeKit;

/// <summary>A received email as the case shows it: its original HTML (signature, footer, logos) made safe, or its text.</summary>
public sealed class EmailBodyDto
{
    /// <summary>"html" or "text".</summary>
    public string Format { get; set; } = "text";
    public string? Html { get; set; }
    public string Text { get; set; } = string.Empty;
    /// <summary>The HTML loads pictures from the internet (often a logo in the signature, sometimes a tracking pixel).</summary>
    public bool HasRemoteImages { get; set; }
    /// <summary>Show those pictures without asking: the sender is verified (a registered domain or sender).</summary>
    public bool ShowRemoteImagesByDefault { get; set; }
    /// <summary>Pictures embedded in the email that could not be shown (too large, or not a picture type that is safe to show).</summary>
    public int InlineImagesOmitted { get; set; }
    /// <summary>Why only the text is shown, when it is (e.g. the original was too large to keep).</summary>
    public string? Note { get; set; }
}

public interface IEmailBodyRenderer
{
    Task<EmailBodyDto> RenderAsync(InboundEmail email, CancellationToken ct = default);
}

/// <summary>
/// Renders a received email from its stored original (.eml). Intake keeps only the text for searching and the timeline, which
/// is why signatures, footers and logos were lost; the original still holds the HTML and its embedded pictures.
/// </summary>
/// <remarks>
/// <para><b>Safe by construction.</b> The HTML goes through an allow-list sanitizer (the configuration WABA uses for email:
/// tables, inline styles of a fixed property list, links and images; no script, no event handlers, no forms, no frames, no
/// <c>javascript:</c> or <c>data:</c> links). Embedded pictures (<c>cid:</c>) are then put inline as <c>data:</c> pictures, of
/// raster types only (never SVG). The browser shows the result in a sandboxed frame with scripts off as a second wall.</para>
/// <para>Rendered answers are kept 10 minutes: an email never changes once received.</para>
/// </remarks>
public sealed partial class EmailBodyRenderer(IAttachmentStore store, IMemoryCache cache, ILogger<EmailBodyRenderer> logger) : IEmailBodyRenderer
{
    private const long MaxInlineImageBytes = 2 * 1024 * 1024;
    private const long MaxInlineTotalBytes = 8 * 1024 * 1024;
    private static readonly HashSet<string> InlineTypes = new(StringComparer.OrdinalIgnoreCase)
        { "image/png", "image/jpeg", "image/jpg", "image/gif", "image/webp", "image/bmp" };

    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    public async Task<EmailBodyDto> RenderAsync(InboundEmail email, CancellationToken ct = default)
    {
        if (cache.TryGetValue("email-body:" + email.Id, out EmailBodyDto? hit) && hit != null) return hit;

        var result = new EmailBodyDto
        {
            Text = email.BodyText,
            ShowRemoteImagesByDefault = email.Verification == SenderVerification.Verified,
        };

        if (email.RawKey == null)
        {
            result.Note = "The original of this email was not kept (it was too large), so only its text is shown.";
            return result;
        }

        MimeMessage message;
        try
        {
            await using var raw = await store.OpenReadAsync(email.RawKey, ct);
            message = await MimeMessage.LoadAsync(raw, ct);
        }
        catch (FileNotFoundException)
        {
            result.Note = "The original of this email is not stored on this server yet, so only its text is shown.";
            return result;   // not cached: it may be copied into the shared store shortly
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the original of email {Email}.", email.Id);
            result.Note = "The original of this email could not be read, so only its text is shown.";
            return result;
        }

        if (!string.IsNullOrWhiteSpace(message.TextBody)) result.Text = message.TextBody.Trim();
        if (!string.IsNullOrWhiteSpace(message.HtmlBody))
        {
            var (html, omitted) = await EmbedInlineImagesAsync(Sanitize(message.HtmlBody), message, ct);
            result.Format = "html";
            result.Html = html;
            result.InlineImagesOmitted = omitted;
            result.HasRemoteImages = RemoteImage().IsMatch(html);
        }

        cache.Set("email-body:" + email.Id, result, TimeSpan.FromMinutes(10));
        return result;
    }

    /// <summary>The allow-list cleaning; exposed for tests.</summary>
    public static string Sanitize(string html)
    {
        // Parts of a full document that are not content: kept text would otherwise show (a <title>, Outlook's <xml> blocks).
        var s = NonContent().Replace(html, "");
        s = Comments().Replace(s, "");
        s = HeadTags().Replace(s, "");   // <head>/<html>/<body> wrappers go; their <style> blocks stay
        return Sanitizer.Sanitize(s);
    }

    private static async Task<(string Html, int Omitted)> EmbedInlineImagesAsync(string html, MimeMessage message, CancellationToken ct)
    {
        var parts = message.BodyParts.OfType<MimePart>()
            .Where(p => !string.IsNullOrWhiteSpace(p.ContentId))
            .GroupBy(p => NormalizeCid(p.ContentId!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var omitted = 0;
        long total = 0;
        var uris = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in CidReference().Matches(html))
        {
            var cid = NormalizeCid(Uri.UnescapeDataString(m.Groups["cid"].Value));
            if (uris.ContainsKey(cid)) continue;
            string? uri = null;
            if (parts.TryGetValue(cid, out var part) && part.Content != null && InlineTypes.Contains(part.ContentType.MimeType))
            {
                using var buffer = new MemoryStream();
                await part.Content.DecodeToAsync(buffer, ct);
                if (buffer.Length > 0 && buffer.Length <= MaxInlineImageBytes && total + buffer.Length <= MaxInlineTotalBytes)
                {
                    total += buffer.Length;
                    uri = $"data:{part.ContentType.MimeType.ToLowerInvariant()};base64,{Convert.ToBase64String(buffer.ToArray())}";
                }
            }
            if (uri == null) omitted++;
            uris[cid] = uri;
        }

        var result = CidReference().Replace(html, m =>
        {
            var cid = NormalizeCid(Uri.UnescapeDataString(m.Groups["cid"].Value));
            return uris.TryGetValue(cid, out var uri) && uri != null ? $"{m.Groups["attr"].Value}=\"{uri}\"" : $"{m.Groups["attr"].Value}=\"\"";
        });
        return (result, omitted);
    }

    private static string NormalizeCid(string cid) => cid.Trim().Trim('<', '>');

    private static HtmlSanitizer CreateSanitizer()
    {
        var s = new HtmlSanitizer();
        s.AllowedTags.Clear();
        foreach (var tag in new[]
        {
            "p", "div", "span", "br", "hr", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "blockquote", "pre",
            "table", "thead", "tbody", "tfoot", "tr", "td", "th", "caption", "colgroup", "col",
            "strong", "b", "em", "i", "u", "s", "small", "sub", "sup", "code", "a", "img", "center", "font", "style",
        }) s.AllowedTags.Add(tag);

        s.AllowedAttributes.Clear();
        foreach (var a in new[]
        {
            "href", "src", "alt", "title", "width", "height", "align", "valign", "bgcolor", "border", "cellpadding", "cellspacing",
            "colspan", "rowspan", "color", "face", "size", "dir", "style", "class", "role",
        }) s.AllowedAttributes.Add(a);

        s.AllowedCssProperties.Clear();
        foreach (var p in new[]
        {
            "color", "background-color", "background", "font", "font-family", "font-size", "font-weight", "font-style", "text-align",
            "text-decoration", "line-height", "letter-spacing", "margin", "margin-top", "margin-bottom", "margin-left", "margin-right",
            "padding", "padding-top", "padding-bottom", "padding-left", "padding-right", "border", "border-top", "border-bottom",
            "border-left", "border-right", "border-radius", "border-collapse", "border-spacing", "border-color", "border-width",
            "border-style", "width", "max-width", "min-width", "height", "max-height", "display", "vertical-align",
            "text-transform", "white-space", "float", "clear", "table-layout",
            // The parser expands shorthands (border: 1px solid) into these, so they must be listed for the shorthand to survive.
            "border-top-width", "border-top-style", "border-top-color", "border-right-width", "border-right-style", "border-right-color",
            "border-bottom-width", "border-bottom-style", "border-bottom-color", "border-left-width", "border-left-style", "border-left-color",
            "border-top-left-radius", "border-top-right-radius", "border-bottom-left-radius", "border-bottom-right-radius",
            "background-image", "background-repeat", "background-position", "background-size",
            "font-variant", "text-indent", "list-style", "list-style-type",
        }) s.AllowedCssProperties.Add(p);

        s.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https", "mailto", "tel", "cid" }) s.AllowedSchemes.Add(scheme);
        // A signature's own <style> block: its rules (Style) and screen-size variants (Media) only — no @import, no @font-face.
        s.AllowedAtRules.Clear();
        s.AllowedAtRules.Add(AngleSharp.Css.Dom.CssRuleType.Style);
        s.AllowedAtRules.Add(AngleSharp.Css.Dom.CssRuleType.Media);
        s.KeepChildNodes = true;
        return s;
    }

    [GeneratedRegex(@"<(title|script|noscript|template|xml|object|embed|iframe|frameset)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NonContent();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"</?(html|head|body|meta|link|base)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadTags();

    [GeneratedRegex(@"(?<attr>src|background)=""cid:(?<cid>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex CidReference();

    [GeneratedRegex(@"(src|background)=""https?:|url\(\s*['""]?https?:", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteImage();
}
