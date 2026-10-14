namespace CaseManagement.Api.DTOs;

/// <summary>An option of a configured list: the stored value and what the user sees.</summary>
public class LookupOptionDto
{
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;

    /// <summary>ID-type options only: the format rule an ID value must satisfy (key from IdFormatRules) and its optional pattern/message.</summary>
    public string? FormatRule { get; set; }
    public string? FormatRegex { get; set; }
    public string? FormatMessage { get; set; }
}

public class CaseTypeOptionDto
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
}

public class SubCategoryOptionDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class DepartmentOptionDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;

    /// <summary>Active sub-categories. A case needs one, so a department with none cannot receive cases yet.</summary>
    public List<SubCategoryOptionDto> SubCategories { get; set; } = new();
}

public class PriorityOptionDto
{
    public string Name { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public int InternalHours { get; set; }
    public int ExternalHours { get; set; }
    public int FirstResponseMinutes { get; set; }
}

/// <summary>Everything the Create Case form needs, in one response.</summary>
public class CaseFormMetadataDto
{
    public List<FieldConfigurationDto> Fields { get; set; } = new();
    public List<CaseTypeOptionDto> CaseTypes { get; set; } = new();
    public List<DepartmentOptionDto> Departments { get; set; } = new();
    public List<PriorityOptionDto> Priorities { get; set; } = new();

    /// <summary>The active options of every list a field of this form uses, keyed by list code.</summary>
    public Dictionary<string, List<LookupOptionDto>> Lookups { get; set; } = new();
}

/// <summary>Everything the Add Customer form needs, in one response.</summary>
public class CustomerFormMetadataDto
{
    public List<FieldConfigurationDto> Fields { get; set; } = new();
    public Dictionary<string, List<LookupOptionDto>> Lookups { get; set; } = new();
}
