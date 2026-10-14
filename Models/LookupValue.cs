namespace CaseManagement.Api.Models;

public class LookupValue : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LookupTypeId { get; set; }
    public string TypeCode { get; set; } = string.Empty; // Redundant code for easy indexing & querying
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int DisplayOrder { get; set; } = 0;
    public bool IsActive { get; set; } = true;

    /// <summary>For lists whose type <see cref="LookupType.UsesFormatRules"/> (ID types): the key of the format rule an ID value of this type must satisfy (see IdFormatRules). Null = the built-in default for the value's name.</summary>
    public string? FormatRule { get; set; }

    /// <summary>Pattern used when <see cref="FormatRule"/> is REGEX.</summary>
    public string? FormatRegex { get; set; }

    /// <summary>Message shown when the pattern does not match.</summary>
    public string? FormatMessage { get; set; }

    /// <summary>
    /// Choosing this option needs a typed explanation (e.g. "Other" in a reason list). The server refuses the choice without one,
    /// so the rule is configuration, not a hard-coded option name.
    /// </summary>
    public bool RequiresDetail { get; set; } = false;

    public LookupType? LookupType { get; set; }
}
