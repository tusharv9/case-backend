namespace CaseManagement.Api.DTOs;

public class FieldConfigurationDto
{
    public Guid Id { get; set; }
    public string ModuleKey { get; set; } = "Customer360";
    public string SectionKey { get; set; } = "AddNewCustomer";
    public string ApiField { get; set; } = string.Empty;
    public string DisplayLabel { get; set; } = string.Empty;
    public bool IsVisible { get; set; } = true;
    public bool IsRequired { get; set; } = false;

    /// <summary>Read-only: the field is mandatory for the system to work, so "Required" cannot be turned off.</summary>
    public bool IsSystemRequired { get; set; } = false;
    public string MaskingRule { get; set; } = "None";
    public int VisibleChars { get; set; } = 4;
    public int DisplayOrder { get; set; } = 0;
    public string FieldType { get; set; } = "Text";
    public string? ValidationRegex { get; set; }
    public string? ValidationMessage { get; set; }
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public string? MinValue { get; set; }
    public string? MaxValue { get; set; }
    public string? LookupTypeCode { get; set; }
    public bool IsCustomField { get; set; } = false;

    /// <summary>
    /// The field types this field may be switched to (read-only; null = any). A built-in field is stored in a fixed
    /// column of the customer record, which limits what its type can become.
    /// </summary>
    public string[]? AllowedFieldTypes { get; set; }

    public static FieldConfigurationDto From(CaseManagement.Api.Models.FieldConfiguration entity) => new()
    {
        Id = entity.Id,
        ModuleKey = entity.ModuleKey,
        SectionKey = entity.SectionKey,
        ApiField = entity.ApiField,
        DisplayLabel = entity.DisplayLabel,
        IsVisible = entity.IsVisible || entity.IsSystemRequired,
        IsRequired = entity.IsRequired || entity.IsSystemRequired,
        IsSystemRequired = entity.IsSystemRequired,
        MaskingRule = entity.MaskingRule,
        VisibleChars = entity.VisibleChars,
        DisplayOrder = entity.DisplayOrder,
        FieldType = entity.FieldType,
        ValidationRegex = entity.ValidationRegex,
        ValidationMessage = entity.ValidationMessage,
        MinLength = entity.MinLength,
        MaxLength = entity.MaxLength,
        MinValue = entity.MinValue,
        MaxValue = entity.MaxValue,
        LookupTypeCode = entity.LookupTypeCode,
        IsCustomField = entity.IsCustomField,
        AllowedFieldTypes = CaseManagement.Api.Services.BuiltInFieldStorage.AllowedTypes(entity)
    };
}

/// <summary>
/// Save Changes on a field tab. Existing fields are sent in <see cref="Update"/> (matched by id, which the server
/// issued) and brand-new ones in <see cref="Create"/> WITHOUT an id — the server generates it. The two lists are
/// different shapes on purpose, so a new field can never be sent with a made-up id.
/// </summary>
public class UpdateFieldConfigurationsRequest
{
    public string ModuleKey { get; set; } = "Customer360";
    public string SectionKey { get; set; } = "AddNewCustomer";
    public List<FieldConfigurationDto> Update { get; set; } = new();
    public List<CreateCustomFieldDto> Create { get; set; } = new();
}

public class CreateCustomFieldDto
{
    public string ModuleKey { get; set; } = "Customer360";
    public string SectionKey { get; set; } = "AddNewCustomer";
    public string ApiField { get; set; } = string.Empty;
    public string DisplayLabel { get; set; } = string.Empty;
    public string FieldType { get; set; } = "Text"; // Text, Dropdown, Date, Phone, Number, etc.
    public bool IsVisible { get; set; } = true;
    public bool IsRequired { get; set; } = false;
    public string MaskingRule { get; set; } = "None";
    public int VisibleChars { get; set; } = 4;
    public int DisplayOrder { get; set; } = 0;
    public string? LookupTypeCode { get; set; }
    public string? ValidationRegex { get; set; }
    public string? ValidationMessage { get; set; }
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public string? MinValue { get; set; }
    public string? MaxValue { get; set; }
}

public class LookupTypeDto
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>False for lists whose set of values is fixed by what the system supports (e.g. ID types): values can be switched on/off, not added.</summary>
    public bool AllowAdd { get; set; } = true;

    /// <summary>True when each option can carry a format rule (ID types).</summary>
    public bool UsesFormatRules { get; set; }
}

public class LookupValueDto
{
    public string? FormatRule { get; set; }
    public string? FormatRegex { get; set; }
    public string? FormatMessage { get; set; }
    public Guid Id { get; set; }
    public Guid LookupTypeId { get; set; }
    public string TypeCode { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int DisplayOrder { get; set; } = 0;
    public bool IsActive { get; set; } = true;
    /// <summary>Choosing this option needs a typed explanation (see LookupValue.RequiresDetail).</summary>
    public bool RequiresDetail { get; set; }
}

public class CreateLookupValueDto
{
    public string TypeCode { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int DisplayOrder { get; set; } = 0;
}

public class UpdateLookupValueDto
{
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int DisplayOrder { get; set; } = 0;
    public bool IsActive { get; set; } = true;
}

public class CustomerCustomAttributeDto
{
    public string FieldKey { get; set; } = string.Empty;
    public string FieldValue { get; set; } = string.Empty;
}

public class CreateCaseTypeConfigDto
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Prefix { get; set; } = "C-";
    public int DisplayOrder { get; set; } = 1;
}

public class CreateDepartmentSubCategoryDto
{
    public Guid DepartmentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int DisplayOrder { get; set; } = 1;
}

public class UpdateCaseTypeConfigDto
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Prefix { get; set; } = "C-";
    public int DisplayOrder { get; set; } = 1;
    public bool IsActive { get; set; } = true;
}

public class UpdateDepartmentSubCategoryDto
{
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    /// <summary>Omitted (null) = unchanged; empty = cleared.</summary>
    public string? Description { get; set; }
    public int DisplayOrder { get; set; } = 1;
    public bool IsActive { get; set; } = true;
}

/// <summary>Payload for editing a single configurable field from the Edit drawer.</summary>
public class UpdateFieldConfigurationDto
{
    public string DisplayLabel { get; set; } = string.Empty;
    public string FieldType { get; set; } = "Text";
    public bool IsVisible { get; set; } = true;
    public bool IsRequired { get; set; } = false;
    public string MaskingRule { get; set; } = "None";
    public int VisibleChars { get; set; } = 4;
    public int DisplayOrder { get; set; } = 0;
    public string? LookupTypeCode { get; set; }
    public string? ValidationRegex { get; set; }
    public string? ValidationMessage { get; set; }
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public string? MinValue { get; set; }
    public string? MaxValue { get; set; }
}

/// <summary>
/// A priority ("severity") as shown on the Settings screen. It is a view over one PrioritySlaRule row — the same
/// row the Cases SLA & Routing screen edits — so the two screens can never disagree.
/// </summary>
public class SeverityDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public int InternalHours { get; set; }
    public int ExternalHours { get; set; }
    public int FirstResponseMinutes { get; set; } = 240;
    public bool IsSystem { get; set; }
    public int CasesUsing { get; set; }
    public bool IsActive { get; set; } = true;
}

public class CreateSeverityDto
{
    public string Name { get; set; } = string.Empty;
    // No defaults: an omitted target is refused, never silently filled in.
    public int InternalHours { get; set; }
    public int ExternalHours { get; set; }
    public int FirstResponseMinutes { get; set; }
    public int DisplayOrder { get; set; } = 0;
}

public class UpdateSeverityDto
{
    public string Name { get; set; } = string.Empty;
    public int DisplayOrder { get; set; } = 0;
    public int? InternalHours { get; set; }
    public int? ExternalHours { get; set; }
    public int? FirstResponseMinutes { get; set; }
    public bool IsActive { get; set; } = true;
}

public class UpdateDepartmentDto
{
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

/// <summary>One option of a list, as edited in the field drawer. No <see cref="Id"/> = a new option.</summary>
public class LookupValueDraftDto
{
    public string? FormatRule { get; set; }
    public string? FormatRegex { get; set; }
    public string? FormatMessage { get; set; }
    public Guid? Id { get; set; }
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Null = leave the stored setting unchanged (clients that do not know the flag never reset it).</summary>
    public bool? RequiresDetail { get; set; }
}

/// <summary>Saves every pending option change of one list in a single transaction.</summary>
public class SaveLookupValuesRequest
{
    /// <summary>Only used when the list does not exist yet: the name it is created with (so a new dropdown can get its own list).</summary>
    public string? Name { get; set; }
    public List<LookupValueDraftDto> Values { get; set; } = new();
}

public class CountryDto
{
    public string Iso2 { get; set; } = string.Empty;
    public string Iso3 { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DialCode { get; set; } = string.Empty;
    public int MinNationalDigits { get; set; }
    public int MaxNationalDigits { get; set; }
    public string? NationalPattern { get; set; }
}
