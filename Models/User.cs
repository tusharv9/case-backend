namespace CaseManagement.Api.Models;

using Microsoft.EntityFrameworkCore;

[Index(nameof(Email), IsUnique = true)]
public class User : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty; // e.g. "Micro-Finance Officer", "Sr. CC Agent", "Supervisor"

    /// <summary>
    /// The user's id in the HOST App, which owns login and user management. Opaque string (may be a
    /// GUID, an integer, …). This table is only a local projection of Host users: it carries what
    /// Case Management needs to reference a person (display data, status, team) while <see cref="Id"/>
    /// stays the internal key for foreign keys. Null only for legacy rows not yet linked.
    /// </summary>
    public string? ExternalUserId { get; set; }

    /// <summary>False when the Host has deactivated the user: they keep their history but cannot sign in or be assigned work.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>When this projection was last refreshed from the Host (token claims or directory).</summary>
    public DateTime? LastSyncedAt { get; set; }
    
    public UserStatus Status { get; set; } = UserStatus.Available;
    
    public string? Team { get; set; }
    public string? Queue { get; set; }

    /// <summary>Null for a Host user who has not been placed in a team yet.</summary>
    public Guid? DepartmentId { get; set; }
    public Department? Department { get; set; }
}
