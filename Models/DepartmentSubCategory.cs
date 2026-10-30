namespace CaseManagement.Api.Models;

public class DepartmentSubCategory : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>
    /// The business department the sub-category classifies cases of (Create Case lists a department's sub-categories, and the
    /// priority mapping is looked up by department + name). Null only for rows written by code older than departments.
    /// </summary>
    public Guid? OrgDepartmentId { get; set; }
    public OrgDepartment? OrgDepartment { get; set; }

    /// <summary>Legacy: the team the sub-category was first created under, from before sub-categories belonged to departments.
    /// Kept (not dropped) so older code and rollbacks keep working; nothing new reads it.</summary>
    public Guid? DepartmentId { get; set; }
    public Department? Department { get; set; }

    public string Name { get; set; } = string.Empty; // e.g. "Personal Loan", "General Inquiry"
    /// <summary>Business identifier (e.g. the category workbook's "LN-01"); unique when set.</summary>
    public string Code { get; set; } = string.Empty;
    /// <summary>What the sub-category covers, shown to agents when they choose it.</summary>
    public string? Description { get; set; }
    public int DisplayOrder { get; set; } = 1;
    public bool IsActive { get; set; } = true;
}
