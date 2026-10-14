namespace CaseManagement.Api.Services;

using System.Globalization;
using System.Text.RegularExpressions;
using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

public sealed record FieldError(string Field, string Message);

/// <summary>
/// One or more fields failed validation. Derives from <see cref="ArgumentException"/> so existing handling
/// (HTTP 400) applies, and carries every error so the UI can mark each field instead of just the first.
/// </summary>
public sealed class FieldValidationException : ArgumentException
{
    public IReadOnlyList<FieldError> Errors { get; }

    public FieldValidationException(IEnumerable<FieldError> errors)
        : this(errors.ToList()) { }

    private FieldValidationException(List<FieldError> errors)
        : base(errors.Count > 0 ? errors[0].Message : "Validation failed.")
    {
        Errors = errors;
    }
}

public sealed class FieldValidationResult
{
    public IReadOnlyList<FieldError> Errors { get; init; } = Array.Empty<FieldError>();

    /// <summary>The submitted values, trimmed, with dropdown values replaced by their configured spelling.</summary>
    public IReadOnlyDictionary<string, string> Normalized { get; init; } = new Dictionary<string, string>();

    public bool IsValid => Errors.Count == 0;

    public void ThrowIfInvalid()
    {
        if (!IsValid) throw new FieldValidationException(Errors);
    }
}

public interface IFieldValidationEngine
{
    /// <summary>
    /// Validates <paramref name="values"/> against the field configuration of a form section.
    /// <paramref name="customFieldKeys"/> are the keys of the submitted custom fields, which must all be configured.
    /// <paramref name="requirednessHandledElsewhere"/> lists fields whose "required" check some other rule owns.
    /// </summary>
    Task<FieldValidationResult> ValidateAsync(
        string moduleKey,
        string sectionKey,
        IReadOnlyDictionary<string, string?> values,
        IEnumerable<string>? customFieldKeys = null,
        IReadOnlySet<string>? requirednessHandledElsewhere = null,
        CancellationToken ct = default);

    /// <summary>
    /// Checks ONE stored value against a field definition (type, length, range, pattern, list membership). Null = acceptable.
    /// Used to prove existing data still fits before a field's type is changed.
    /// </summary>
    Task<string?> CheckValueAsync(FieldConfiguration config, string value, bool includeInactiveOptions = false);

    /// <summary>The configuration of one field of a form section (null when it is not configured).</summary>
    Task<FieldConfiguration?> GetConfigAsync(string moduleKey, string sectionKey, string apiField);
}

/// <summary>
/// Generic, metadata-driven validation: "Field definition + validation metadata → engine", never "new field → new
/// code". It evaluates exactly what an administrator configured — Required, min/max length, regex (with the admin's
/// message), the field type (email, phone, date, number, checkbox), and dropdown membership against the configured
/// lookup — and is the only place those rules are applied, so the backend never relies on the browser having checked.
///
/// Hidden fields are skipped (the user cannot fill what they cannot see) except system-required ones, which are
/// always enforced. A field's Required flag is honoured as configured; only IsSystemRequired fields are fixed.
/// </summary>
public sealed class FieldValidationEngine : IFieldValidationEngine
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex CustomKeyPattern = new(@"^[A-Za-z][A-Za-z0-9_]{0,49}$", RegexOptions.Compiled, RegexTimeout);

    private readonly AppDbContext _context;
    private readonly IConfigCache _cache;
    private readonly ILogger<FieldValidationEngine> _logger;

    public FieldValidationEngine(AppDbContext context, IConfigCache cache, ILogger<FieldValidationEngine> logger)
    {
        _context = context;
        _cache = cache;
        _logger = logger;
    }

    public async Task<FieldValidationResult> ValidateAsync(
        string moduleKey,
        string sectionKey,
        IReadOnlyDictionary<string, string?> values,
        IEnumerable<string>? customFieldKeys = null,
        IReadOnlySet<string>? requirednessHandledElsewhere = null,
        CancellationToken ct = default)
    {
        var configs = await GetConfigsAsync(moduleKey, sectionKey);
        var supplied = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        var errors = new List<FieldError>();
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var config in configs)
        {
            if (!config.IsVisible && !config.IsSystemRequired) continue;

            var label = string.IsNullOrWhiteSpace(config.DisplayLabel) ? config.ApiField : config.DisplayLabel;
            supplied.TryGetValue(config.ApiField, out var raw);
            var value = raw?.Trim() ?? string.Empty;

            var required = config.IsRequired || config.IsSystemRequired;
            if (required && value.Length == 0 && requirednessHandledElsewhere?.Contains(config.ApiField) != true)
            {
                errors.Add(new FieldError(config.ApiField, $"{label} is required."));
                continue;
            }
            if (value.Length == 0) continue;

            var before = errors.Count;
            await ValidateValueAsync(config, label, value, errors);
            normalized[config.ApiField] = errors.Count == before ? await CanonicalAsync(config, value) : value;
        }

        // Custom fields that were submitted but are not configured are rejected rather than stored blindly.
        if (customFieldKeys != null)
        {
            var configured = configs.Where(c => c.IsCustomField && c.IsVisible).Select(c => c.ApiField).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in customFieldKeys.Where(k => !string.IsNullOrWhiteSpace(k)))
            {
                if (!CustomKeyPattern.IsMatch(key) || !configured.Contains(key))
                    errors.Add(new FieldError(key, $"'{key}' is not a configured field."));
            }
        }

        return new FieldValidationResult { Errors = errors, Normalized = normalized };
    }

    public async Task<FieldConfiguration?> GetConfigAsync(string moduleKey, string sectionKey, string apiField) =>
        (await GetConfigsAsync(moduleKey, sectionKey)).FirstOrDefault(c => c.ApiField.Equals(apiField, StringComparison.OrdinalIgnoreCase));

    public async Task<string?> CheckValueAsync(FieldConfiguration config, string value, bool includeInactiveOptions = false)
    {
        var label = string.IsNullOrWhiteSpace(config.DisplayLabel) ? config.ApiField : config.DisplayLabel;
        var errors = new List<FieldError>();
        _includeInactive.Value = includeInactiveOptions;
        try { await ValidateValueAsync(config, label, value, errors); }
        finally { _includeInactive.Value = false; }
        return errors.Count == 0 ? null : errors[0].Message;
    }

    private readonly AsyncLocal<bool> _includeInactive = new();

    private async Task ValidateValueAsync(FieldConfiguration config, string label, string value, List<FieldError> errors)
    {
        if (config.MinLength.HasValue && value.Length < config.MinLength.Value)
        {
            errors.Add(new FieldError(config.ApiField, $"{label} must be at least {config.MinLength.Value} characters."));
            return;
        }
        if (config.MaxLength.HasValue && value.Length > config.MaxLength.Value)
        {
            errors.Add(new FieldError(config.ApiField, $"{label} cannot exceed {config.MaxLength.Value} characters."));
            return;
        }

        switch ((config.FieldType ?? "Text").ToLowerInvariant())
        {
            case "email":
                if (value.Length > 254 || !SafeMatch(EmailPattern, value))
                {
                    errors.Add(new FieldError(config.ApiField, $"{label} must be a valid email address."));
                    return;
                }
                break;

            case "phone":
                if (value.Count(char.IsDigit) < 7 || value.Any(ch => !(char.IsDigit(ch) || " +-()".Contains(ch))))
                {
                    errors.Add(new FieldError(config.ApiField, $"{label} must be a valid phone number."));
                    return;
                }
                break;

            case "date":
                if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
                {
                    errors.Add(new FieldError(config.ApiField, $"{label} must be a valid date."));
                    return;
                }
                if (!CheckRange(config, label, date.Date.Ticks, errors, d => new DateTime((long)d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))) return;
                break;

            case "number":
                if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                {
                    errors.Add(new FieldError(config.ApiField, $"{label} must be a number."));
                    return;
                }
                if (!CheckRange(config, label, number, errors, d => d.ToString(CultureInfo.InvariantCulture))) return;
                break;

            case "checkbox":
                if (!value.Equals("true", StringComparison.OrdinalIgnoreCase) && !value.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new FieldError(config.ApiField, $"{label} must be true or false."));
                    return;
                }
                break;

            case "dropdown":
                if (!string.IsNullOrWhiteSpace(config.LookupTypeCode))
                {
                    var options = _includeInactive.Value ? await GetAllLookupAsync(config.LookupTypeCode) : await GetLookupAsync(config.LookupTypeCode);
                    if (!options.ContainsKey(value))
                    {
                        errors.Add(new FieldError(config.ApiField, $"'{value}' is not a valid option for {label}."));
                        return;
                    }
                }
                break;
        }

        if (!string.IsNullOrWhiteSpace(config.ValidationRegex))
        {
            try
            {
                if (!PatternMatcher.IsMatch(value, config.ValidationRegex))
                    errors.Add(new FieldError(config.ApiField, string.IsNullOrWhiteSpace(config.ValidationMessage) ? $"{label} format is invalid." : config.ValidationMessage));
            }
            catch (ArgumentException ex)   // RegexParseException: an unusable pattern must not lock every user out
            {
                _logger.LogWarning(ex, "Ignoring invalid validation pattern for field {Field}.", config.ApiField);
            }
            catch (RegexMatchTimeoutException)
            {
                errors.Add(new FieldError(config.ApiField, $"{label} could not be validated."));
            }
        }
    }

    /// <summary>Min/max value (Number) or earliest/latest date (Date) from the field configuration. False = an error was added.</summary>
    private static bool CheckRange(FieldConfiguration config, string label, decimal value, List<FieldError> errors, Func<decimal, string> show)
    {
        var type = config.FieldType ?? "Text";
        var min = FieldDefinitionRules.TryParseBound(type, config.MinValue, out var minOk);
        var max = FieldDefinitionRules.TryParseBound(type, config.MaxValue, out var maxOk);
        if (minOk && min.HasValue && value < min.Value)
        {
            errors.Add(new FieldError(config.ApiField, type.Equals("Date", StringComparison.OrdinalIgnoreCase)
                ? $"{label} cannot be before {show(min.Value)}." : $"{label} must be at least {show(min.Value)}."));
            return false;
        }
        if (maxOk && max.HasValue && value > max.Value)
        {
            errors.Add(new FieldError(config.ApiField, type.Equals("Date", StringComparison.OrdinalIgnoreCase)
                ? $"{label} cannot be after {show(max.Value)}." : $"{label} cannot exceed {show(max.Value)}."));
            return false;
        }
        return true;
    }

    private static bool SafeMatch(Regex regex, string value)
    {
        try { return regex.IsMatch(value); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private async Task<string> CanonicalAsync(FieldConfiguration config, string value)
    {
        if (!string.Equals(config.FieldType, "Dropdown", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(config.LookupTypeCode))
            return value;
        var options = await GetLookupAsync(config.LookupTypeCode);
        return options.TryGetValue(value, out var canonical) ? canonical : value;
    }

    private Task<IReadOnlyList<FieldConfiguration>> GetConfigsAsync(string moduleKey, string sectionKey) =>
        _cache.GetOrCreateAsync<IReadOnlyList<FieldConfiguration>>($"fields:{moduleKey}:{sectionKey}", async () =>
            await _context.FieldConfigurations.AsNoTracking()
                .Where(f => f.ModuleKey == moduleKey && f.SectionKey == sectionKey)
                .OrderBy(f => f.DisplayOrder)
                .ToListAsync());

    /// <summary>Every value of a lookup, active or not (not cached: used only when a field's type is being changed).</summary>
    private async Task<Dictionary<string, string>> GetAllLookupAsync(string typeCode)
    {
        var values = await _context.LookupValues.AsNoTracking().Where(v => v.TypeCode == typeCode).Select(v => v.Value).ToListAsync();
        return values.GroupBy(v => v, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Active values of a lookup: any casing in, configured spelling out.</summary>
    private Task<Dictionary<string, string>> GetLookupAsync(string typeCode) =>
        _cache.GetOrCreateAsync($"lookup-active:{typeCode}", async () =>
        {
            var values = await _context.LookupValues.AsNoTracking()
                .Where(v => v.TypeCode == typeCode && v.IsActive)
                .Select(v => v.Value)
                .ToListAsync();
            return values.GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
                         .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        });
}
