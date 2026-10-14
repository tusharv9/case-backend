namespace CaseManagement.Api.Models;

/// <summary>
/// "Cases of this sub-category get this priority." Keyed by the sub-category's ID, so two departments can
/// each have a "Fraud" sub-category with different priorities, renaming a sub-category cannot orphan the
/// mapping, and deleting a sub-category removes it. One sub-category maps to at most one priority.
/// </summary>
public class PriorityCategoryMapping : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PrioritySlaRuleId { get; set; }
    public PrioritySlaRule PrioritySlaRule { get; set; } = null!;

    public Guid DepartmentSubCategoryId { get; set; }
    public DepartmentSubCategory DepartmentSubCategory { get; set; } = null!;
}
