namespace CaseManagement.Api.Services;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>
/// What an administrator may configure for each field type, and the checks on a field DEFINITION (as opposed to the
/// checks on a value, which <see cref="FieldValidationEngine"/> does). One table drives both the server's save-time
/// validation and — through the field-types endpoint's capability flags — the Add/Edit drawer, so the two cannot disagree
/// about which settings apply to which type.
/// </summary>
public static class FieldDefinitionRules
{
    public sealed record Capabilities(bool Length, bool Pattern, bool Range, bool Lookup, bool Masking);

    public static readonly IReadOnlyDictionary<string, Capabilities> ByType =
        new Dictionary<string, Capabilities>(StringComparer.OrdinalIgnoreCase)
        {
            ["Text"]     = new(Length: true,  Pattern: true,  Range: false, Lookup: false, Masking: true),
            ["Email"]    = new(Length: true,  Pattern: true,  Range: false, Lookup: false, Masking: true),
            ["Phone"]    = new(Length: false, Pattern: false, Range: false, Lookup: false, Masking: true),   // country rules apply
            ["Number"]   = new(Length: false, Pattern: false, Range: true,  Lookup: false, Masking: true),
            ["Date"]     = new(Length: false, Pattern: false, Range: true,  Lookup: false, Masking: true),
            ["Dropdown"] = new(Length: false, Pattern: false, Range: false, Lookup: true,  Masking: true),
            ["Checkbox"] = new(Length: false, Pattern: false, Range: false, Lookup: false, Masking: false),
        };

    public static readonly string[] MaskingRules = { "None", "FullMask", "HideMiddle", "HideFirstShowLast" };

    public const string DisplayOrderMessage = "Display order {0} is already assigned to another field. Please choose a different display order.";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>The field type spelled as configured, or null when it is not a supported type.</summary>
    public static string? CanonicalType(string? type) =>
        ByType.Keys.FirstOrDefault(k => string.Equals(k, type, StringComparison.OrdinalIgnoreCase));

    /// <summary>The settings of a field definition that this check looks at (the shape shared by every request DTO).</summary>
    public sealed record Definition(
        string Label, string FieldType, string MaskingRule, int VisibleChars, int DisplayOrder,
        string? ValidationRegex, int? MinLength, int? MaxLength, string? MinValue, string? MaxValue, string? LookupTypeCode);

    /// <summary>
    /// Synchronous checks; returns one message per problem, keyed by the request property the UI shows it under.
    /// </summary>
    public static List<FieldError> Check(Definition d)
    {
        var errors = new List<FieldError>();
        var type = CanonicalType(d.FieldType);
        if (type == null)
        {
            errors.Add(new FieldError("fieldType", $"'{d.FieldType}' is not a valid field type. Valid types: {string.Join(", ", ByType.Keys)}."));
            return errors;
        }
        var caps = ByType[type];

        if (d.DisplayOrder < 0) errors.Add(new FieldError("displayOrder", "Display order must be 0 or greater."));
        if (!MaskingRules.Contains(d.MaskingRule ?? "None", StringComparer.Ordinal))
            errors.Add(new FieldError("maskingRule", $"'{d.MaskingRule}' is not a valid masking rule."));
        if (d.VisibleChars < 0) errors.Add(new FieldError("visibleChars", "Visible characters must be 0 or greater."));

        if (caps.Length)
        {
            if (d.MinLength is < 0) errors.Add(new FieldError("minLength", "Minimum length must be 0 or greater."));
            if (d.MaxLength is < 1) errors.Add(new FieldError("maxLength", "Maximum length must be 1 or greater."));
            if (d.MinLength.HasValue && d.MaxLength.HasValue && d.MinLength > d.MaxLength)
                errors.Add(new FieldError("maxLength", "Maximum length cannot be below the minimum."));
        }

        if (caps.Pattern && !string.IsNullOrWhiteSpace(d.ValidationRegex))
        {
            try { _ = new Regex(d.ValidationRegex, RegexOptions.None, RegexTimeout); }
            catch (ArgumentException ex) { errors.Add(new FieldError("validationRegex", $"This is not a valid regular expression: {ex.Message}")); }
        }

        if (caps.Range)
        {
            var min = TryParseBound(type, d.MinValue, out var minOk);
            var max = TryParseBound(type, d.MaxValue, out var maxOk);
            var what = type == "Date" ? "date" : "number";
            if (!minOk) errors.Add(new FieldError("minValue", $"Minimum must be a valid {what}."));
            if (!maxOk) errors.Add(new FieldError("maxValue", $"Maximum must be a valid {what}."));
            if (minOk && maxOk && min.HasValue && max.HasValue && min > max)
                errors.Add(new FieldError("maxValue", "Maximum cannot be below the minimum."));
        }

        return errors;
    }

    /// <summary>Parses a Number/Date bound into a comparable decimal (dates as ticks). Empty = no bound (valid).</summary>
    public static decimal? TryParseBound(string type, string? raw, out bool ok)
    {
        ok = true;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (string.Equals(type, "Date", StringComparison.OrdinalIgnoreCase))
        {
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
                return d.Date.Ticks;
            ok = false; return null;
        }
        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return n;
        ok = false; return null;
    }

    /// <summary>
    /// Clears the settings that do not apply to the type (so switching Text to Number cannot leave a stale regex that
    /// silently keeps being enforced) and tidies blanks to null.
    /// </summary>
    public static (string? Regex, string? Message, int? Min, int? Max, string? MinValue, string? MaxValue, string? Lookup, string MaskingRule, int VisibleChars)
        Normalize(Definition d, string? validationMessage)
    {
        var caps = ByType[CanonicalType(d.FieldType)!];
        string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var regex = caps.Pattern ? Blank(d.ValidationRegex) : null;
        return (
            regex,
            regex == null ? null : Blank(validationMessage),
            caps.Length ? d.MinLength : null,
            caps.Length ? d.MaxLength : null,
            caps.Range ? Blank(d.MinValue) : null,
            caps.Range ? Blank(d.MaxValue) : null,
            caps.Lookup ? Blank(d.LookupTypeCode) : null,
            caps.Masking ? (string.IsNullOrWhiteSpace(d.MaskingRule) ? "None" : d.MaskingRule) : "None",
            Math.Max(0, d.VisibleChars));
    }
}
