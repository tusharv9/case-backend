namespace CaseManagement.Api.Models;

public class TeamAssignmentPointer
{
    // Primary Key is DepartmentId
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    
    public Guid LastAssignedUserId { get; set; }
    public User LastAssignedUser { get; set; } = null!;
    
    public DateTime LastAssignedAt { get; set; } = DateTime.UtcNow;
}
