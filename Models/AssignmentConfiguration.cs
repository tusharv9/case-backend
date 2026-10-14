namespace CaseManagement.Api.Models;

public class AssignmentConfiguration : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    // If DepartmentId is null, this is the global fallback assignment configuration
    public Guid? DepartmentId { get; set; }
    public Department? Department { get; set; }
    
    // "RoundRobin", "SkillBased", "LeastOccupancy"
    public string Algorithm { get; set; } = "RoundRobin";
    
    public int MaxConcurrentCapacity { get; set; } = 5;
    public bool IsActive { get; set; } = true;
}
