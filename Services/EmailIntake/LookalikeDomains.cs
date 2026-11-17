namespace CaseManagement.Api.Services.EmailIntake;

using System.Globalization;
using System.Text;

/// <summary>
/// Spots a sender domain that RESEMBLES a registered organisation's domain without being it: look-alike characters (a Cyrillic "а"
/// for a Latin "a"), a typo ("bsm.example"), another ending ("bsn.example.co"), an unaccepted sub-domain, or the organisation's name
/// in the display name of a stranger. Deterministic and explainable: every hit carries the reason shown to the reviewer.
/// </summary>
/// <remarks>
/// This is a review hint, never verification: a resemblance only decides which queue an email waits in. Verification is exact
/// (a registered sender's address), and nothing here can make an email verified.
/// </remarks>
public static class LookalikeDomains
{
    /// <summary>A registered domain to compare against, with the organisation it belongs to.</summary>
    public sealed record Registered(string Domain, string Organisation, bool IncludeSubdomains);

    /// <summary>What resembles what, and why.</summary>
    public sealed record Resemblance(string Domain, string Organisation, string Reason);

    // Endings that sit under a country code (co.in, com.my): the organisation's own name is the label before them.
    private static readonly HashSet<string> SecondLevel = new(StringComparer.Ordinal) { "co", "com", "net", "org", "gov", "edu", "ac", "gob", "or", "ne", "go" };

    // Characters people (and attackers) confuse with Latin letters and digits. Kept small and explicit, so every substitution
    // named in a reason is one a reviewer can see.
    private static readonly Dictionary<char, char> Confusables = new()
    {
        // Cyrillic
        ['а'] = 'a', ['в'] = 'b', ['е'] = 'e', ['к'] = 'k', ['м'] = 'm', ['н'] = 'h', ['о'] = 'o', ['р'] = 'p', ['с'] = 'c', ['т'] = 't',
        ['у'] = 'y', ['х'] = 'x', ['і'] = 'i', ['ј'] = 'j', ['ѕ'] = 's', ['ԁ'] = 'd', ['ԛ'] = 'q', ['ԝ'] = 'w', ['ӏ'] = 'l', ['һ'] = 'h',
        // Greek
        ['α'] = 'a', ['β'] = 'b', ['ε'] = 'e', ['ι'] = 'i', ['κ'] = 'k', ['ν'] = 'v', ['ο'] = 'o', ['ρ'] = 'p', ['τ'] = 't', ['υ'] = 'u', ['χ'] = 'x',
        // Armenian
        ['ո'] = 'n', ['ս'] = 'u', ['օ'] = 'o', ['հ'] = 'h', ['ց'] = 'g',
        // Latin look-alikes and digits
        ['ı'] = 'i', ['ɡ'] = 'g', ['0'] = 'o', ['1'] = 'l', ['3'] = 'e', ['5'] = 's',
    };

    /// <summary>
    /// The first resemblance between <paramref name="senderDomain"/> and a registered domain, or one between the display name and an
    /// organisation's name; null when nothing resembles. An exact match is not a resemblance (the caller handles it).
    /// </summary>
    public static Resemblance? Find(string senderDomain, string? displayName, IReadOnlyCollection<Registered> registered)
    {
        if (string.IsNullOrWhiteSpace(senderDomain) || registered.Count == 0) return null;
        var unicode = ToUnicode(senderDomain);
        var sender = Split(unicode);

        foreach (var r in registered)
        {
            var domain = r.Domain;
            if (unicode == domain) continue;

            // it.bsn.example where bsn.example does not accept sub-domains.
            if (unicode.EndsWith("." + domain, StringComparison.Ordinal))
                return new(domain, r.Organisation, $"{senderDomain} is a sub-domain of {domain} ({r.Organisation}), which is registered without its sub-domains.");

            var reg = Split(domain);
            if (sender.Core.Length == 0 || reg.Core.Length == 0) continue;

            // Look-alike characters: different text, same shape.
            var swapped = Substitutions(sender.Core);
            if (swapped.Count > 0 && Skeleton(sender.Core) == Skeleton(reg.Core))
                return new(domain, r.Organisation, $"{senderDomain} looks like {domain} ({r.Organisation}) but uses look-alike characters: {string.Join(", ", swapped)}.");
            if (sender.Core != reg.Core && Skeleton(sender.Core) == Skeleton(reg.Core))
                return new(domain, r.Organisation, $"{senderDomain} reads like {domain} ({r.Organisation}) once look-alike characters are read as letters (e.g. 0 for o, rn for m).");

            // The organisation's name under another ending: bsn.example vs bsn.example.co / bsn.co.
            if (sender.Core == reg.Core && sender.Suffix != reg.Suffix)
                return new(domain, r.Organisation, $"{senderDomain} has the same name as {domain} ({r.Organisation}) under a different ending.");

            // A typo of the name: one character away for short names, two for long ones.
            var allowed = reg.Core.Length <= 5 ? 1 : 2;
            if (reg.Core.Length >= 3 && Math.Abs(sender.Core.Length - reg.Core.Length) <= allowed && Distance(sender.Core, reg.Core) is var d && d > 0 && d <= allowed)
                return new(domain, r.Organisation, $"{senderDomain} is {d} {(d == 1 ? "character" : "characters")} away from {domain} ({r.Organisation}).");

            // The name with words around it: bsn-example-support.com, bsnbank.com. Short names are too common to say anything.
            if (reg.Core.Length >= 4 && sender.Core != reg.Core && sender.Core.Replace("-", "").Contains(reg.Core.Replace("-", ""), StringComparison.Ordinal))
                return new(domain, r.Organisation, $"{senderDomain} contains the name of {domain} ({r.Organisation}) with other words around it.");
        }

        // A stranger using an organisation's name: "BSN Organization <someone@elsewhere.com>".
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            var name = Normalise(displayName);
            foreach (var org in registered.GroupBy(r => r.Organisation))
            {
                var orgName = Normalise(org.Key);
                if (orgName.Length >= 3 && name.Contains(orgName, StringComparison.Ordinal))
                    return new(org.First().Domain, org.Key,
                        $"The sender's name \"{displayName.Trim()}\" mentions {org.Key}, but {senderDomain} is not one of its domains ({string.Join(", ", org.Select(x => x.Domain))}).");
            }
        }
        return null;
    }

    /// <summary>The domain in Unicode (xn-- labels decoded), lower-case; the input unchanged if it cannot be decoded.</summary>
    public static string ToUnicode(string domain)
    {
        var d = domain.Trim().TrimEnd('.').ToLowerInvariant();
        try { return new IdnMapping().GetUnicode(d).ToLowerInvariant(); }
        catch (ArgumentException) { return d; }
    }

    /// <summary>The look-alike characters in a name, described for a person: "'а' (Cyrillic) for 'a'".</summary>
    private static List<string> Substitutions(string text) => text
        .Where(c => Confusables.ContainsKey(c) && !char.IsAsciiDigit(c) && c > 127)
        .Distinct()
        .Select(c => $"'{c}' ({ScriptOf(c)}) for '{Confusables[c]}'")
        .ToList();

    private static string ScriptOf(char c) => c switch
    {
        >= 'Ѐ' and <= 'ԯ' => "Cyrillic",
        >= 'Ͱ' and <= 'Ͽ' => "Greek",
        >= '԰' and <= '֏' => "Armenian",
        _ => "non-Latin",
    };

    /// <summary>The name with every look-alike replaced by the letter it imitates, and rn→m, vv→w, which read the same.</summary>
    public static string Skeleton(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKC)) sb.Append(Confusables.TryGetValue(c, out var latin) ? latin : c);
        return sb.ToString().Replace("rn", "m").Replace("vv", "w");
    }

    /// <summary>The domain's organisation name (the label before the ending) and its ending: bsn.com.my → ("bsn", "com.my").</summary>
    private static (string Core, string Suffix) Split(string domain)
    {
        var labels = domain.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2) return (domain, "");
        var suffixLabels = labels.Length >= 3 && SecondLevel.Contains(labels[^2]) && labels[^1].Length == 2 ? 2 : 1;
        var coreIndex = labels.Length - suffixLabels - 1;
        return (labels[coreIndex], string.Join('.', labels[^suffixLabels..]));
    }

    private static string Normalise(string text) =>
        System.Text.RegularExpressions.Regex.Replace(Skeleton(text.ToLowerInvariant()), @"[^\p{L}\p{N}]+", " ").Trim();

    /// <summary>Levenshtein distance (insertions, deletions and substitutions).</summary>
    private static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[b.Length];
    }
}
