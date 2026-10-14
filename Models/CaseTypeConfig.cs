namespace CaseManagement.Api.Models;

using Microsoft.EntityFrameworkCore;

[Index(nameof(Code), IsUnique = true)]
public class CaseTypeConfig : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty; // e.g. "Complaint", "Service", "Inquiry", "InformationRequest"
    public string Name { get; set; } = string.Empty; // e.g. "Complaint", "Service Request", "Information Request"
    public string Prefix { get; set; } = "C-";       // e.g. "C-", "S-", "I-", "IR-"
    public int DisplayOrder { get; set; } = 1;
    public bool IsActive { get; set; } = true;
}
