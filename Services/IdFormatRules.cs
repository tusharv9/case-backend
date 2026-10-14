namespace CaseManagement.Api.Services;

using System.Text.RegularExpressions;
using CaseManagement.Api.Data;
using CaseManagement.Api.Validators;
using Microsoft.EntityFrameworkCore;

/// <summary>What an ID value of one ID type must look like: a rule key plus an optional pattern and message.</summary>
public sealed record IdFormat(string Rule, string? Regex, string? Message);

/// <summary>
/// The format rules an administrator can attach to an ID type (Settings → the ID Type list → each option). The CHECKERS are
/// code (a Malaysian NRIC has an embedded date, which no pattern can express); WHICH checker applies to which ID type is
/// configuration, stored on the option. Today's behaviour is the default: NRIC → MY_NRIC, Passport → PASSPORT, Account → ACCOUNT_NUMBER.
/// </summary>
public static class IdFormatRules
{
    public const string MyNric = "MY_NRIC", Passport = "PASSPORT", AccountNumber = "ACCOUNT_NUMBER", Alphanumeric = "ALPHANUMERIC", Any = "ANY", Regex = "REGEX";

    public sealed record RuleInfo(string Key, string Label, string Description);

    public static readonly IReadOnlyList<RuleInfo> All = new RuleInfo[]
    {
        new(MyNric, "Malaysian NRIC (YYMMDD-PB-###G)", "Digits with hyphens, a valid birth date inside, and it must match the date of birth."),
        new(Passport, "Passport (6–12 letters/digits)", "Letters and digits only, 6 to 12 characters."),
        new(AccountNumber, "Account number (4–25 letters/digits/hyphens)", "Letters, digits and hyphens, 4 to 25 characters."),
        new(Alphanumeric, "Letters and digits", "Letters, digits and hyphens, up to 30 characters."),
        new(Regex, "Custom pattern", "Matches the regular expression you enter."),
        new(Any, "No format check", "Any value is accepted."),
    };

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>The rule an ID type had before this became configurable, derived from its name.</summary>
    public static string DefaultFor(string? idType)
    {
        var t = (idType ?? string.Empty).ToLowerInvariant();
        if (t.Contains("passport")) return Passport;
        if (t.Contains("account")) return AccountNumber;
        if (t.Contains("nric")) return MyNric;
        return Any;
    }

    public static bool IsKnown(string? key) => All.Any(r => r.Key == key);

    /// <summary>Config-time check of a rule + pattern; null when fine.</summary>
    public static string? CheckDefinition(string? rule, string? regex)
    {
        if (string.IsNullOrWhiteSpace(rule)) return null;
        if (!IsKnown(rule)) return $"'{rule}' is not a known format rule.";
        if (rule == Regex)
        {
            if (string.IsNullOrWhiteSpace(regex)) return "A custom pattern is required for the custom-pattern rule.";
            try { _ = new Regex(regex, RegexOptions.None, RegexTimeout); }
            catch (ArgumentException ex) { return $"The pattern is not a valid regular expression: {ex.Message}"; }
        }
        return null;
    }

    /// <summary>An error message, or null when <paramref name="value"/> satisfies the rule.</summary>
    public static string? Check(IdFormat format, string value)
    {
        var v = value?.Trim() ?? string.Empty;
        switch (format.Rule)
        {
            case MyNric:
                return CreateCustomerDtoValidator.BeValidNric(v) ? null : "Please enter in correct format";
            case Passport:
                return CreateCustomerDtoValidator.BeValidPassport(v) ? null : "Passport number must be 6 to 12 alphanumeric characters with no spaces or symbols (e.g. A98765432).";
            case AccountNumber:
                return CreateCustomerDtoValidator.BeValidAccountNumber(v) ? null : "Account number must be 4 to 25 alphanumeric characters (e.g. ACC-12345).";
            case Alphanumeric:
                return System.Text.RegularExpressions.Regex.IsMatch(v, @"^[A-Za-z0-9\-]{1,30}$", RegexOptions.None, RegexTimeout)
                    ? null : (string.IsNullOrWhiteSpace(format.Message) ? "Use letters, digits and hyphens only (up to 30 characters)." : format.Message);
            case Regex:
                try
                {
                    return PatternMatcher.IsMatch(v, format.Regex ?? string.Empty)
                        ? null : (string.IsNullOrWhiteSpace(format.Message) ? "The ID value format is invalid." : format.Message);
                }
                catch (ArgumentException) { return null; }            // an unusable pattern must not lock everyone out
                catch (RegexMatchTimeoutException) { return "The ID value could not be validated."; }
            default:
                return null;
        }
    }

    /// <summary>Only the Malaysian NRIC encodes a birth date, so only it is cross-checked against the date of birth.</summary>
    public static string? CheckAgainstDateOfBirth(IdFormat format, string value, DateTime? dateOfBirth)
    {
        if (format.Rule != MyNric || dateOfBirth == null || !CreateCustomerDtoValidator.BeValidNric(value.Trim())) return null;
        var date = value.Trim().Split('-')[0];
        var yy = int.Parse(date[..2]); var mm = int.Parse(date[2..4]); var dd = int.Parse(date[4..6]);
        var dob = dateOfBirth.Value;
        return dob.Year % 100 == yy && dob.Month == mm && dob.Day == dd ? null : "Date of Birth does not match the date in the NRIC number.";
    }
}

public interface IIdFormatService
{
    Task<IdFormat> GetAsync(string? idType, CancellationToken ct = default);
}

/// <summary>Looks up the format rule configured on an ID type option (cached; the cache is cleared whenever configuration changes).</summary>
public class IdFormatService : IIdFormatService
{
    private readonly AppDbContext _context;
    private readonly IConfigCache _cache;

    public IdFormatService(AppDbContext context, IConfigCache cache) { _context = context; _cache = cache; }

    public async Task<IdFormat> GetAsync(string? idType, CancellationToken ct = default)
    {
        var all = await _cache.GetOrCreateAsync("id-formats", async () =>
        {
            var rows = await _context.LookupValues.AsNoTracking()
                .Where(v => v.LookupType!.UsesFormatRules)
                .Select(v => new { v.Value, v.FormatRule, v.FormatRegex, v.FormatMessage })
                .ToListAsync(ct);
            return rows.GroupBy(r => r.Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => new IdFormat(g.First().FormatRule ?? IdFormatRules.DefaultFor(g.Key), g.First().FormatRegex, g.First().FormatMessage), StringComparer.OrdinalIgnoreCase);
        });
        return idType != null && all.TryGetValue(idType, out var f) ? f : new IdFormat(IdFormatRules.DefaultFor(idType), null, null);
    }
}
