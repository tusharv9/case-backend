namespace CaseManagement.Api.Models;

public class TeamAssignmentPointer
{
    // Primary Key is DepartmentId
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;
    
    public Guid LastAssignedUserId { get; set; }
    public User LastAssignedUser { get; set; } = null!;
    
    public DateTime LastAssignedAt { get; set; } = DateTime.UtcNow;
}
