namespace CaseManagement.Api.Services;

using System.Reflection;
using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Server-side masking of customer data, driven by field configuration (MaskingRule,
/// VisibleChars). This is the ONLY place masking happens: the API never sends a sensitive value the
/// caller is not entitled to see, so there is nothing for a browser to "mask" afterwards.
///
/// Deny-by-default: a caller sees raw values only if they hold the explicit <c>pii.unmask</c> permission
/// (not implied by the "*" wildcard). Background work with no caller therefore gets masked data too.
/// </summary>
public interface IPiiMaskingService
{
    /// <summary>Masks a single value according to the configured rule.</summary>
    string Mask(string? rawValue, string maskingRule, int visibleChars = 4);

    /// <summary>Masks every configured sensitive property of a customer-shaped DTO, in place.</summary>
    Task MaskAsync(object? dto, CancellationToken ct = default);

    /// <summary>Applies masking in-place on a CustomerSummaryDto.</summary>
    Task MaskCustomerSummaryAsync(CustomerSummaryDto dto, CancellationToken ct = default);

    /// <summary>Applies masking in-place on a CustomerDetailDto.</summary>
    Task MaskCustomerDetailAsync(CustomerDetailDto dto, CancellationToken ct = default);

    /// <summary>
    /// Masks a case-shaped DTO in place: its custom attributes by the Case Management (Create Case) rules, the customer's name it
    /// carries and its nested customer by the Customer 360 rules, and any sub-cases it lists.
    /// </summary>
    Task MaskCaseAsync(object? dto, CancellationToken ct = default);

    /// <summary>Masks every case-shaped DTO of a list (rules are read once).</summary>
    Task MaskCasesAsync(IEnumerable<object>? dtos, CancellationToken ct = default);
}

public class PiiMaskingService : IPiiMaskingService
{
    private readonly AppDbContext _context;
    private readonly IConfigCache _cache;
    private readonly ICurrentUserAccessor? _currentUser;

    public PiiMaskingService(AppDbContext context, IConfigCache cache, ICurrentUserAccessor? currentUser = null)
    {
        _context = context;
        _cache = cache;
        _currentUser = currentUser;
    }

    private sealed record Rule(string MaskingRule, int VisibleChars);

    /// <summary>
    /// Which DTO properties a configured field governs. A general field (idValue) covers every
    /// identifier property; a specific one (nric, passport, accountNumber) overrides it for that property.
    /// Matching is case-insensitive on both sides.
    /// </summary>
    private static readonly (string ApiField, string[] Properties)[] FieldBindings =
    {
        ("idValue",           new[] { "IdValue", "NRIC", "Passport", "AccountNumber" }),   // general first…
        ("fullName",          new[] { "FullName" }),
        ("phoneNumber",       new[] { "PhoneNumber" }),
        ("email",             new[] { "Email" }),
        ("dateOfBirth",       new[] { "DateOfBirth" }),
        ("branch",            new[] { "Branch" }),
        ("preferredLanguage", new[] { "PreferredLanguage" }),
        ("customerSegment",   new[] { "CustomerSegment" }),
        ("nric",              new[] { "NRIC", "IdValue" }),                                // …specific after, so they override
        ("passport",          new[] { "Passport" }),
        ("accountNumber",     new[] { "AccountNumber" }),
    };

    public string Mask(string? rawValue, string maskingRule, int visibleChars = 4)
    {
        if (string.IsNullOrEmpty(rawValue)) return rawValue ?? string.Empty;
        if (string.IsNullOrEmpty(maskingRule) || maskingRule == "None") return rawValue;
        if (visibleChars < 0) visibleChars = 0;

        switch (maskingRule)
        {
            case "FullMask":
                return new string('*', rawValue.Length);

            case "HideMiddle":
                if (rawValue.Length <= visibleChars * 2) return new string('*', rawValue.Length);
                var prefix = rawValue[..visibleChars];
                var suffix = visibleChars == 0 ? string.Empty : rawValue[^visibleChars..];
                return prefix + new string('*', rawValue.Length - visibleChars * 2) + suffix;

            case "HideFirstShowLast":
                if (rawValue.Length <= visibleChars) return new string('*', rawValue.Length);
                return new string('*', rawValue.Length - visibleChars) + (visibleChars == 0 ? string.Empty : rawValue[^visibleChars..]);

            default:
                // Unknown rule: mask everything for safety
                return new string('*', rawValue.Length);
        }
    }

    public Task MaskCustomerSummaryAsync(CustomerSummaryDto dto, CancellationToken ct = default) => MaskAsync(dto, ct);

    public Task MaskCustomerDetailAsync(CustomerDetailDto dto, CancellationToken ct = default) => MaskAsync(dto, ct);

    /// <summary>The module whose field settings govern customer data, and the one that governs the data of a case.</summary>
    public const string CustomerModule = "Customer360";
    public const string CaseModule = "CaseManagement";

    private sealed record RuleSet(Dictionary<string, Rule> Customer, Dictionary<string, Rule> Case)
    {
        public bool IsEmpty => Customer.Count == 0 && Case.Count == 0;
    }

    /// <summary>The rules that apply to the caller, or null when the caller may see raw values (or nothing is masked).</summary>
    private async Task<RuleSet?> RulesForCallerAsync()
    {
        if (_currentUser != null && _currentUser.Permissions.Contains(Permissions.PiiUnmask)) return null;
        var rules = new RuleSet(await GetRulesAsync(CustomerModule), await GetRulesAsync(CaseModule));
        return rules.IsEmpty ? null : rules;
    }

    public async Task MaskAsync(object? dto, CancellationToken ct = default)
    {
        if (dto == null) return;
        var rules = await RulesForCallerAsync();
        if (rules != null) MaskCustomerObject(dto, rules, depth: 0);
    }

    public async Task MaskCaseAsync(object? dto, CancellationToken ct = default)
    {
        if (dto == null) return;
        var rules = await RulesForCallerAsync();
        if (rules != null) MaskCaseObject(dto, rules, depth: 0);
    }

    public async Task MaskCasesAsync(IEnumerable<object>? dtos, CancellationToken ct = default)
    {
        if (dtos == null) return;
        var rules = await RulesForCallerAsync();
        if (rules == null) return;
        foreach (var dto in dtos) if (dto != null) MaskCaseObject(dto, rules, depth: 0);
    }

    private const int MaxDepth = 3;   // case → customer → that customer's cases is as deep as any response goes

    /// <summary>A customer-shaped object: its bound properties and custom attributes by the Customer 360 rules.</summary>
    private void MaskCustomerObject(object dto, RuleSet rules, int depth)
    {
        if (depth > MaxDepth) return;

        // property rules (by DTO property name) from the field bindings
        var byProperty = new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase);
        foreach (var (apiField, properties) in FieldBindings)
            if (rules.Customer.TryGetValue(apiField, out var rule))
                foreach (var property in properties) byProperty[property] = rule;

        foreach (var property in dto.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;

            if (property.CanWrite && byProperty.TryGetValue(property.Name, out var rule))
            {
                ApplyToProperty(dto, property, rule);
            }
            else if (property.GetValue(dto) is List<CustomerCustomAttributeDto> custom)
            {
                // Custom fields are masked by their own key.
                MaskAttributes(custom, rules.Customer);
            }
            else if (CaseList(property, dto) is { } cases)
            {
                foreach (var c in cases) MaskCaseObject(c, rules, depth + 1);   // the customer's cases repeat their name
            }
        }
    }

    /// <summary>
    /// A case-shaped object: custom attributes by the Case Management rules (they are case data), the customer's name by the
    /// Customer 360 "fullName" rule, the nested customer as a customer, and listed sub-cases as cases.
    /// </summary>
    private void MaskCaseObject(object dto, RuleSet rules, int depth)
    {
        if (depth > MaxDepth) return;

        foreach (var property in dto.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;

            if (property.Name == "CustomerName" && property.PropertyType == typeof(string) && property.CanWrite)
            {
                if (rules.Customer.TryGetValue("fullName", out var nameRule)) ApplyToProperty(dto, property, nameRule);
            }
            else if (property.GetValue(dto) is List<CustomerCustomAttributeDto> custom)
            {
                MaskAttributes(custom, rules.Case);
            }
            else if (property.GetValue(dto) is CustomerSummaryDto customer)
            {
                MaskCustomerObject(customer, rules, depth + 1);
            }
            else if (CaseList(property, dto) is { } cases)
            {
                foreach (var c in cases) MaskCaseObject(c, rules, depth + 1);
            }
        }
    }

    private void MaskAttributes(List<CustomerCustomAttributeDto> attributes, Dictionary<string, Rule> rules)
    {
        foreach (var attribute in attributes)
            if (rules.TryGetValue(attribute.FieldKey, out var rule))
                attribute.FieldValue = Mask(attribute.FieldValue, rule.MaskingRule, rule.VisibleChars);
    }

    /// <summary>A list property whose items carry a customer's name (case summaries): those items are masked as cases.</summary>
    private static IEnumerable<object>? CaseList(PropertyInfo property, object dto)
    {
        if (property.PropertyType == typeof(string) || !typeof(System.Collections.IEnumerable).IsAssignableFrom(property.PropertyType)) return null;
        var itemType = property.PropertyType.IsGenericType ? property.PropertyType.GetGenericArguments().FirstOrDefault() : null;
        if (itemType == null || itemType.GetProperty("CustomerName") == null) return null;
        return (property.GetValue(dto) as System.Collections.IEnumerable)?.Cast<object>().Where(o => o != null).ToList();
    }

    private void ApplyToProperty(object dto, PropertyInfo property, Rule rule)
    {
        if (property.PropertyType == typeof(string))
        {
            property.SetValue(dto, Mask((string?)property.GetValue(dto), rule.MaskingRule, rule.VisibleChars));
        }
        else if (property.PropertyType == typeof(DateTime?))
        {
            // A date cannot be partially masked, so a sensitive one is withheld.
            property.SetValue(dto, null);
        }
    }

    /// <summary>
    /// Effective masking rule per configured field of ONE module (Customer 360 governs customer data, Case Management governs case
    /// data), across the module's form sections. Filter sections are search inputs, so masking never comes from them. When several
    /// sections disagree the MOST RESTRICTIVE wins (full mask, otherwise the fewest visible characters), so a lenient duplicate row
    /// can never expose what another row hides.
    /// </summary>
    private Task<Dictionary<string, Rule>> GetRulesAsync(string module) =>
        _cache.GetOrCreateAsync($"pii-rules:{module}", async () =>
        {
            var masked = await _context.FieldConfigurations.AsNoTracking()
                .Where(f => f.ModuleKey == module && f.SectionKey != "Filters" && f.MaskingRule != "None" && f.MaskingRule != "")
                .ToListAsync();

            var result = new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in masked)
            {
                var candidate = new Rule(field.MaskingRule, Math.Max(0, field.VisibleChars));
                if (!result.TryGetValue(field.ApiField, out var existing) || MoreRestrictive(candidate, existing))
                    result[field.ApiField] = candidate;
            }
            return result;
        });

    private static bool MoreRestrictive(Rule candidate, Rule existing)
    {
        if (existing.MaskingRule == "FullMask") return false;
        if (candidate.MaskingRule == "FullMask") return true;
        return candidate.VisibleChars < existing.VisibleChars;
    }
}
