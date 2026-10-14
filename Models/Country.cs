namespace CaseManagement.Api.Models;

/// <summary>
/// Country metadata for phone numbers: dial code plus the rules a national number must satisfy. Phone validation is
/// driven by these rows (not by code), so adding or correcting a country is a data change.
/// </summary>
public class Country : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Iso2 { get; set; } = string.Empty;
    public string Iso3 { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Digits only, without "+", e.g. "60".</summary>
    public string DialCode { get; set; } = string.Empty;

    /// <summary>Digits of the national (significant) number, excluding the dial code and any trunk "0".</summary>
    public int MinNationalDigits { get; set; } = 6;
    public int MaxNationalDigits { get; set; } = 12;

    /// <summary>Optional pattern the national digits must match, in addition to the length limits.</summary>
    public string? NationalPattern { get; set; }

    public bool IsActive { get; set; } = true;
}
