namespace CaseManagement.Api.Models;

public class FieldConfiguration : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public string ModuleKey { get; set; } = "Customer360"; // e.g., Customer360, CaseManagement
    public string SectionKey { get; set; } = "AddNewCustomer"; // e.g., AddNewCustomer, ExistingCustomer, Filters
    
    public string ApiField { get; set; } = string.Empty; // e.g. customerName, birthDate, preferredLanguage
    public string DisplayLabel { get; set; } = string.Empty;
    
    public bool IsVisible { get; set; } = true;
    public bool IsRequired { get; set; } = false;

    /// <summary>
    /// True for the few fields the application cannot function without (e.g. the customer of a case, a
    /// customer's name and ID). Their "Required" setting is always on and cannot be switched off — every other
    /// field's Required flag is honoured exactly as configured, by the backend as well as the UI.
    /// </summary>
    public bool IsSystemRequired { get; set; } = false;
    
    public string MaskingRule { get; set; } = "None"; // None, FullMask, HideMiddle, HideFirstShowLast
    public int VisibleChars { get; set; } = 4;
    public int DisplayOrder { get; set; } = 0;
    
    public string FieldType { get; set; } = "Text"; // Text, Number, Date, Email, Phone, Dropdown, Checkbox
    public string? ValidationRegex { get; set; }

    /// <summary>Shown to the user when <see cref="ValidationRegex"/> fails (instead of a generic "format is invalid").</summary>
    public string? ValidationMessage { get; set; }
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }

    /// <summary>Lower bound for Number (a decimal) and Date (yyyy-MM-dd) fields. Interpreted by <see cref="FieldType"/>.</summary>
    public string? MinValue { get; set; }

    /// <summary>Upper bound for Number and Date fields.</summary>
    public string? MaxValue { get; set; }
    
    public string? LookupTypeCode { get; set; } // Reference code for LookupType options if FieldType is Dropdown
    public bool IsCustomField { get; set; } = false; // True if created via "+ ADD NEW FIELD"
}
