namespace CaseManagement.Api.Models;

using Microsoft.EntityFrameworkCore;

[Index(nameof(Name), IsUnique = true)]
[Index(nameof(Code), IsUnique = true)]
public class Department : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    
    public Guid? OwnerId { get; set; }
    public User? Owner { get; set; }
    public bool IsActive { get; set; } = true;

    public string Function { get; set; } = string.Empty;
    
    // Navigation property
    public ICollection<Case> Cases { get; set; } = new List<Case>();
    public ICollection<User> Users { get; set; } = new List<User>();
    public ICollection<TeamMember> Members { get; set; } = new List<TeamMember>();
}
