namespace CaseManagement.Api.Models;

/// <summary>
/// The value of an administrator-defined custom field (a FieldConfiguration with IsCustomField on the
/// Create Case form) for one case. Mirrors <see cref="CustomerCustomAttribute"/>.
/// </summary>
public class CaseCustomAttribute : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public string FieldKey { get; set; } = string.Empty;
    public string FieldValue { get; set; } = string.Empty;

    public Case? Case { get; set; }
}
