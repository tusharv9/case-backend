namespace CaseManagement.Api.Models;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// A team: the people who work cases (lead, members, assignment settings, queue). Independent of <see cref="Department"/>:
/// the two are joined only by <see cref="TeamDepartment"/> mapping rows. A team may serve any number of departments,
/// including none; creating or deleting a department never creates or deletes a team.
/// </summary>
[Index(nameof(Name), IsUnique = true)]
[Index(nameof(Code), IsUnique = true)]
public class Team : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    
    public Guid? OwnerId { get; set; }
    public User? Owner { get; set; }
    public bool IsActive { get; set; } = true;

    public string Function { get; set; } = string.Empty;
    
    public ICollection<Case> Cases { get; set; } = new List<Case>();
    public ICollection<TeamMember> Members { get; set; } = new List<TeamMember>();

    /// <summary>The departments this team handles. Each department is mapped to at most one team.</summary>
    public ICollection<TeamDepartment> DepartmentLinks { get; set; } = new List<TeamDepartment>();
    public ICollection<TeamCustomAttribute> CustomAttributes { get; set; } = new List<TeamCustomAttribute>();
}
