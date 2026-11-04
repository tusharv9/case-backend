namespace CaseManagement.Api.Models;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// A business department (Accounts, Loans, Cards…): what a case is about. Owns its sub-categories; Create Case lists it.
/// Independent of <see cref="Team"/>: a department is handled by exactly one team through a <see cref="TeamDepartment"/>
/// mapping, and until it has one it cannot be used to create cases.
/// </summary>
[Index(nameof(Name), IsUnique = true)]
[Index(nameof(Code), IsUnique = true)]
public class Department : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>An inactive department is not offered on Create Case or for new mappings; an existing mapping stays.</summary>
    public bool IsActive { get; set; } = true;
    public int DisplayOrder { get; set; }

    /// <summary>The mapping to the team that handles it: empty or one row (unique on <see cref="TeamDepartment.DepartmentId"/>).</summary>
    public ICollection<TeamDepartment> TeamLinks { get; set; } = new List<TeamDepartment>();
}

/// <summary>
/// The team–department mapping: this team handles this department. One team may have many rows (many departments); a
/// department has at most one row (unique index on <see cref="DepartmentId"/>), so it is never handled by two teams.
/// </summary>
public class TeamDepartment
{
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>The value of one administrator-defined field of the Create/Edit Team form (Case Settings → Teams → Create Team Form).</summary>
public class TeamCustomAttribute
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;
    public string FieldKey { get; set; } = string.Empty;
    public string FieldValue { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
