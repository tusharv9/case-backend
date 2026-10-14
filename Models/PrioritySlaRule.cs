namespace CaseManagement.Api.Models;

using Microsoft.EntityFrameworkCore;

[Index(nameof(Priority), IsUnique = true)]
public class PrioritySlaRule : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>
    /// The priority's name (unique, case-insensitive). This table is the SINGLE source of truth for
    /// priorities: the name list, its SLA targets and which sub-categories map to it. Administrators can add,
    /// rename and reorder priorities; nothing in code assumes Critical/High/Medium/Low.
    /// </summary>
    public string Priority { get; set; } = string.Empty;

    /// <summary>Position in lists and charts (lowest first = most urgent).</summary>
    public int DisplayOrder { get; set; }

    /// <summary>Inactive priorities cannot be chosen for new cases; existing cases keep theirs.</summary>
    public bool IsActive { get; set; } = true;

    // First Response SLA
    public int FirstResponseValue { get; set; } = 30;
    public string FirstResponseUnit { get; set; } = "Minutes"; // "Minutes" | "Hours"
    public int FirstResponseMinutes { get; set; } = 30;

    // Internal Resolution SLA
    public int InternalResolutionValue { get; set; } = 2;
    public string InternalResolutionUnit { get; set; } = "Hours"; // "Minutes" | "Hours"
    public int InternalResolutionMinutes { get; set; } = 120;

    // External Resolution SLA
    public int ExternalResolutionValue { get; set; } = 4;
    public string ExternalResolutionUnit { get; set; } = "Hours"; // "Minutes" | "Hours"
    public int ExternalResolutionMinutes { get; set; } = 240;

    public int Version { get; set; } = 1;

    public ICollection<PriorityCategoryMapping> CategoryMappings { get; set; } = new List<PriorityCategoryMapping>();
}
