namespace CaseManagement.Api.Models;

public enum ChildRelationType
{
    Link = 1,
    Reopen = 2
}

public class CaseChildRelation : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    // Formatted Sub-Case ID (e.g. C-00045-L01, C-00045-R01)
    public string ChildId { get; set; } = string.Empty;
    
    public Guid ParentCaseId { get; set; }
    public Case ParentCase { get; set; } = null!;
    
    public ChildRelationType RelationType { get; set; }
    
    // Optional target case for Link relationships
    public Guid? LinkedCaseId { get; set; }
    public Case? LinkedCase { get; set; }
    
    public string Reason { get; set; } = string.Empty;
    
    public Guid CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
}
