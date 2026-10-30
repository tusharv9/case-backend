namespace CaseManagement.Api.Models;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// A business department (Retail Banking, Card Operations…): an administrator-managed label that teams are grouped by.
/// NOT <see cref="Department"/>, which is a TEAM (cases, sub-categories, routing, SLA and members all point at it). A team
/// may serve one or several of these departments, and a department may be served by several teams.
/// </summary>
[Index(nameof(Name), IsUnique = true)]
[Index(nameof(Code), IsUnique = true)]
public class OrgDepartment : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>An inactive department is not offered for new links; teams already linked to it keep the link.</summary>
    public bool IsActive { get; set; } = true;
    public int DisplayOrder { get; set; }

    public ICollection<TeamDepartment> Teams { get; set; } = new List<TeamDepartment>();
}

/// <summary>One team serving one business department. The key is the pair, so a link exists at most once.</summary>
public class TeamDepartment
{
    public Guid TeamId { get; set; }
    public Department Team { get; set; } = null!;
    public Guid OrgDepartmentId { get; set; }
    public OrgDepartment OrgDepartment { get; set; } = null!;
    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The team a case of this department goes to when no routing rule matches. At most one per department.</summary>
    public bool IsDefault { get; set; }
}

/// <summary>The value of one administrator-defined field of the Create/Edit Team form (Case Settings → Teams → Create Team Form).</summary>
public class TeamCustomAttribute
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TeamId { get; set; }
    public Department Team { get; set; } = null!;
    public string FieldKey { get; set; } = string.Empty;
    public string FieldValue { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
