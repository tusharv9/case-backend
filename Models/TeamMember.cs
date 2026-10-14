namespace CaseManagement.Api.Models;

public class TeamMember : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    
    public string MemberRole { get; set; } = "Service Agent"; // e.g. "Service Agent — Voice/Chat", "Lead"

    /// <summary>
    /// Whether this person receives cases automatically. Membership is the ONLY thing that makes someone eligible for a
    /// team's work; turning this off keeps them on the team (and visible on the board) without routing cases to them.
    /// </summary>
    public bool IsAssignable { get; set; } = true;

    /// <summary>
    /// Whether a person may CHOOSE this member when assigning or reassigning the team's cases (Assign &amp; Reassign settings →
    /// team membership selection). Independent of <see cref="IsAssignable"/>, which is about automatic routing. New members are included.
    /// </summary>
    public bool CanBeManuallyAssigned { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}
