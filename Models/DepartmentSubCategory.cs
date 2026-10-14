namespace CaseManagement.Api.Models;

public class DepartmentSubCategory : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;

    public string Name { get; set; } = string.Empty; // e.g. "Personal Loan", "General Inquiry"
    /// <summary>Business identifier (e.g. the category workbook's "LN-01"); unique when set.</summary>
    public string Code { get; set; } = string.Empty;
    /// <summary>What the sub-category covers, shown to agents when they choose it.</summary>
    public string? Description { get; set; }
    public int DisplayOrder { get; set; } = 1;
    public bool IsActive { get; set; } = true;
}
